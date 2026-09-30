namespace App.DatabaseTests

open System
open System.Threading
open System.Threading.Tasks
open App
open App.Database
open App.Domain
open App.Shipments
open App.Tests
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging.Abstractions
open Npgsql

[<RequireQualifiedAccess>]
module ShipmentOperationTests =
    type private Started =
        { DataSource: NpgsqlDataSource
          Machine: Machine<ShipmentEntityId, ShipmentState, ShipmentEvent, ShipmentAction, ShipmentActionError> }

    let private start connectionString =
        task {
            let dataSource = AutomataStore.createDataSource connectionString
            let context = AutomataStore.createContext dataSource ignore

            let machine =
                match ShipmentCodec.buildWorker NullLogger.Instance context with
                | Ok machine -> machine
                | Error errors -> invalidOp $"shipments machine is invalid: %A{errors}"

            match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) CancellationToken.None with
            | Ok(Startup.Started _) ->
                return
                    { DataSource = dataSource
                      Machine = machine }
            | Ok(Startup.Refused defects) -> return invalidOp $"shipments machine refused to boot: %A{defects}"
            | Error error -> return invalidOp $"shipments machine failed to start: %A{error}"
        }

    let private waitForState started entity predicate =
        task {
            let deadline = DateTimeOffset.UtcNow.AddSeconds 10.
            let mutable found = None

            while DateTimeOffset.UtcNow < deadline && found.IsNone do
                match! Machine.state started.Machine entity CancellationToken.None with
                | Ok(Some snapshot) when predicate snapshot.State -> found <- Some snapshot.State
                | _ -> do! Task.Delay 50

            return
                found
                |> Option.defaultWith (fun () -> Assert.Fail "The shipment did not reach the expected state.")
        }

    let private insertShipmentRow (dataSource: NpgsqlDataSource) shipmentId allocationId orderId ct =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct

            use command =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.shipments(shipment_id,allocation_id,order_id,capture_id,merchandise,shipping,tax,total,currency,lines) VALUES(@shipment,@allocation,@order,gen_random_uuid(),0,0,0,0,'USD','[]') ON CONFLICT DO NOTHING",
                    connection
                )

            command.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId)
            |> ignore

            command.Parameters.AddWithValue("allocation", ShipmentAllocationId.value allocationId)
            |> ignore

            command.Parameters.AddWithValue("order", orderId) |> ignore
            let! _ = command.ExecuteNonQueryAsync ct
            return ()
        }

    let ``shipment confirms allocation and creates a carrier label`` (fixture: PostgreSqlFixture) =
        task {
            let! started = start fixture.ConnectionString
            use workers = new CancellationTokenSource()
            let processing = (Machine.processor started.Machine).RunAsync workers.Token
            let carrier = SimulatedCarrier() :> ICarrier

            let dispatcher =
                Machine.dispatcher
                    started.Machine
                    (fun (leased: LeasedAction<ShipmentEntityId, ShipmentAction>) (ct: CancellationToken) ->
                        let record = leased.Work

                        match record.Action with
                        | ConfirmAllocation _ -> ShipmentEffects.applyConfirmAllocation started.DataSource record ct
                        | CreateCarrierLabel _ -> ShipmentEffects.applyCreateLabel started.DataSource carrier record ct
                        | NotifyOrderDispatched _
                        | NotifyOrderDelivered _ -> ShipmentEffects.applyNotifyOrder started.DataSource record ct
                        | RequestDeliveryRetry _ -> ShipmentEffects.applyDeliveryRetry started.DataSource record ct
                        | RecordTrackingCheckpoint _
                        | StopTrackingCheck _ ->
                            ShipmentEffects.applyTrackingCheck started.DataSource (TimeSpan.FromDays 7.) record ct)

            let destination =
                OutboxDestination.forMachine Shipments.MachineKey EntityId.create ShipmentCodec.event started.Machine

            let run =
                task {
                    try
                        let shipmentId =
                            ShipmentId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail

                        let allocationId =
                            ShipmentAllocationId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail

                        let orderId = $"order:{Guid.NewGuid():D}"

                        do! insertShipmentRow started.DataSource shipmentId allocationId orderId CancellationToken.None

                        let request =
                            { ShipmentId = shipmentId
                              AllocationId = allocationId
                              OrderId = orderId
                              Lines =
                                [ { LineId = OrderLineId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
                                    Quantity = 1 } ] }

                        let entity = Shipments.shipmentEntityId shipmentId
                        let key = $"shipment-db:v1:{shipmentId |> ShipmentId.value}"

                        let! submitted =
                            Machine.send
                                started.Machine
                                entity
                                (EventEnvelope.create key (ShipmentRequested request))
                                CancellationToken.None

                        Assert.True(Result.isOk submitted)

                        let! _ =
                            waitForState started entity (function
                                | AllocationPending(Some _) -> true
                                | _ -> false)

                        let! _ = dispatcher.PollAsync CancellationToken.None

                        let! delivered =
                            Outbox.deliverPending
                                started.DataSource
                                (Outbox.RelayOptions.defaults "shipment-op-test")
                                [ destination ]
                                CancellationToken.None

                        Assert.Equal(1, delivered)

                        let! _ =
                            waitForState started entity (function
                                | Preparing _ -> true
                                | _ -> false)

                        let! prepared =
                            Machine.send
                                started.Machine
                                entity
                                (EventEnvelope.create $"{key}:prepare" PreparationCompleted)
                                CancellationToken.None

                        Assert.True(Result.isOk prepared)

                        let! _ = dispatcher.PollAsync CancellationToken.None

                        let! _ =
                            Outbox.deliverPending
                                started.DataSource
                                (Outbox.RelayOptions.defaults "shipment-op-test-2")
                                [ destination ]
                                CancellationToken.None

                        let! ready =
                            waitForState started entity (function
                                | ReadyToDispatch ready ->
                                    not (String.IsNullOrWhiteSpace(CarrierReference.value ready.CarrierReference))
                                | _ -> false)

                        match ready with
                        | ReadyToDispatch shipment ->
                            Assert.True(
                                shipment.CarrierReference
                                |> CarrierReference.value
                                |> fun value -> value.StartsWith "sim-"
                            )
                        | other -> Assert.Fail $"Expected ready-to-dispatch, got %A{other}."

                        let dispatchedAt =
                            DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

                        let! dispatched =
                            Machine.send
                                started.Machine
                                entity
                                (EventEnvelope.create $"{key}:dispatch" (DispatchConfirmed dispatchedAt))
                                CancellationToken.None

                        Assert.True(Result.isOk dispatched)
                        let! _ = dispatcher.PollAsync CancellationToken.None

                        use connection = started.DataSource.CreateConnection()
                        do! connection.OpenAsync()

                        use armed =
                            new NpgsqlCommand(
                                "SELECT status || ':' || tracking_generation || ':' || (last_scan_at IS NULL) || ':' || (lost_due_at = @dispatched + interval '7 days') FROM fsnix.shipment_tracking WHERE shipment_id=@shipment",
                                connection
                            )

                        armed.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId) |> ignore
                        armed.Parameters.AddWithValue("dispatched", dispatchedAt) |> ignore
                        let! armedRow = armed.ExecuteScalarAsync()
                        Assert.Equal("pending:1:true:true", string armedRow)

                        use makeDue =
                            new NpgsqlCommand(
                                "UPDATE fsnix.shipment_tracking SET lost_due_at = statement_timestamp() - interval '1 second' WHERE shipment_id=@shipment",
                                connection
                            )

                        makeDue.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId)
                        |> ignore

                        let! _ = makeDue.ExecuteNonQueryAsync()

                        // What ShipmentLostScanner does: claim, send MarkLost from the ledger row, settle.
                        let options = LeaseOptions.defaults "shipment-lost-test"

                        let! claimed =
                            LeasedLedger.claim
                                started.DataSource
                                Ledgers.lostShipments
                                options
                                Ledgers.readLostShipmentCheck
                                CancellationToken.None

                        let check = claimed |> List.exactlyOne

                        let! marked =
                            Machine.send
                                started.Machine
                                entity
                                (EventEnvelope.create
                                    $"{key}:lost"
                                    (MarkLost(check.TrackingGeneration, check.LastScanAt, DateTimeOffset.UtcNow)))
                                CancellationToken.None

                        Assert.True(Result.isOk marked)

                        let! _ =
                            waitForState started entity (function
                                | Lost _ -> true
                                | _ -> false)

                        do!
                            LeasedLedger.settle
                                started.DataSource
                                Ledgers.lostShipments
                                options
                                check.ShipmentId
                                Settlement.Done
                                CancellationToken.None

                        use fired =
                            new NpgsqlCommand(
                                "SELECT status FROM fsnix.shipment_tracking WHERE shipment_id=@shipment",
                                connection
                            )

                        fired.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId) |> ignore
                        let! firedStatus = fired.ExecuteScalarAsync()
                        Assert.Equal("fired", string firedStatus)
                    finally
                        workers.Cancel()
                }

            do! run
            let! _ = processing
            do! Machine.stopAsync started.Machine CancellationToken.None
            started.DataSource.Dispose()
        }
