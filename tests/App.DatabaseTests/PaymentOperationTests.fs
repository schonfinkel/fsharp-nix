namespace App.DatabaseTests

open System
open System.Threading
open System.Threading.Tasks
open App
open App.Database
open App.Domain
open App.Payments
open App.Tests
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging.Abstractions
open Npgsql

[<RequireQualifiedAccess>]
module PaymentOperationTests =
    type private Started =
        { DataSource: NpgsqlDataSource
          Machine: Machine<PaymentId, PaymentState, PaymentEvent, PaymentAction, PaymentActionError> }

    let private start connectionString =
        task {
            let dataSource = AutomataStore.createDataSource connectionString
            let context = AutomataStore.createContext dataSource ignore

            let machine =
                match PaymentCodec.buildWorker NullLogger.Instance context with
                | Ok machine -> machine
                | Error errors -> invalidOp $"payments machine is invalid: %A{errors}"

            match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) CancellationToken.None with
            | Ok(Startup.Started _) ->
                return
                    { DataSource = dataSource
                      Machine = machine }
            | Ok(Startup.Refused defects) -> return invalidOp $"payments machine refused to boot: %A{defects}"
            | Error error -> return invalidOp $"payments machine failed to start: %A{error}"
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
                |> Option.defaultWith (fun () -> Assert.Fail "The payment did not reach the expected state.")
        }

    let ``authorization persists one provider operation and one order callback`` (fixture: PostgreSqlFixture) =
        task {
            let! started = start fixture.ConnectionString
            use workers = new CancellationTokenSource()
            let processing = (Machine.processor started.Machine).RunAsync workers.Token
            let gateway = SimulatedPaymentGateway()

            let dispatcher =
                Machine.dispatcher
                    started.Machine
                    (fun (leased: LeasedAction<PaymentId, PaymentAction>) (ct: CancellationToken) ->
                        let record = leased.Work

                        match record.Action with
                        | CallGatewayAuthorize _ -> PaymentEffects.applyAuthorize started.DataSource gateway record ct
                        | QueryGatewayAuthorization _ ->
                            PaymentEffects.applyQueryAuthorization started.DataSource gateway record ct
                        | CallGatewayCapture _ -> PaymentEffects.applyCapture started.DataSource gateway record ct
                        | QueryGatewayCapture _ ->
                            PaymentEffects.applyQueryCapture started.DataSource gateway record ct
                        | CallGatewayVoid _ -> PaymentEffects.applyVoid started.DataSource gateway record ct
                        | NotifyOrderAuthorized _
                        | NotifyOrderDeclined _
                        | NotifyOrderCancelled _
                        | NotifyOrderVoided _
                        | NotifyOrderCaptured _ -> PaymentEffects.applyNotify started.DataSource record ct
                        | NotifyRefundApproved _
                        | NotifyRefundDenied _
                        | NotifyRefundSettled _ -> PaymentEffects.applyNotifyRefund started.DataSource record ct)

            let run =
                task {
                    try
                        let paymentGuid = Guid.NewGuid()
                        let entity = Payments.paymentId paymentGuid

                        let attempt =
                            { OperationId =
                                PaymentOperationId.create $"authorize:v1:db:{paymentGuid:N}"
                                |> Result.defaultWith Assert.Fail
                              OrderId = $"order:{paymentGuid:D}"
                              Amount = Money.create 32.29m "USD" |> Result.defaultWith Assert.Fail
                              Method = PaymentMethodReference.Sandbox.Success }

                        let key = $"payment-db:v1:{paymentGuid:D}"

                        let! submitted =
                            Machine.send
                                started.Machine
                                entity
                                (EventEnvelope.create key (AuthorizeRequested attempt))
                                CancellationToken.None

                        Assert.True(Result.isOk submitted)

                        let! _ =
                            waitForState started entity (function
                                | AuthorizationPending _ -> true
                                | _ -> false)

                        let! _ = dispatcher.PollAsync CancellationToken.None

                        let destination =
                            OutboxDestination.forMachine
                                Payments.MachineKey
                                EntityId.create
                                PaymentCodec.event
                                started.Machine

                        let! delivered =
                            Outbox.deliverPending
                                started.DataSource
                                (Outbox.RelayOptions.defaults "payment-operation-test")
                                [ destination ]
                                CancellationToken.None

                        Assert.Equal(1, delivered)

                        let! _ =
                            waitForState started entity (function
                                | Authorized _ -> true
                                | _ -> false)

                        let! _ = dispatcher.PollAsync CancellationToken.None
                        Assert.Equal(1, gateway.AuthorizeCalls attempt.OperationId)

                        use connection = started.DataSource.CreateConnection()
                        do! connection.OpenAsync()

                        use operation =
                            new NpgsqlCommand(
                                "SELECT status,result_code,attempts FROM fsnix.payment_operations WHERE operation_id=@operation",
                                connection
                            )

                        operation.Parameters.AddWithValue("operation", PaymentOperationId.value attempt.OperationId)
                        |> ignore

                        let! reader = operation.ExecuteReaderAsync()
                        Assert.True(reader.Read())
                        Assert.Equal("succeeded", reader.GetString 0)
                        Assert.Equal("approved", reader.GetString 1)
                        Assert.Equal(1, reader.GetInt32 2)
                        reader.Dispose()

                        use callback =
                            new NpgsqlCommand(
                                "SELECT COUNT(*) FROM fsnix.integration_outbox WHERE machine_id='orders' AND entity_id=@order",
                                connection
                            )

                        callback.Parameters.AddWithValue("order", attempt.OrderId) |> ignore
                        let! callbacks = callback.ExecuteScalarAsync()
                        Assert.Equal(1L, callbacks :?> int64)

                        let! replay =
                            Machine.send
                                started.Machine
                                entity
                                (EventEnvelope.create key (AuthorizeRequested attempt))
                                CancellationToken.None

                        Assert.True(Result.isOk replay)
                        let! _ = dispatcher.PollAsync CancellationToken.None
                        Assert.Equal(1, gateway.AuthorizeCalls attempt.OperationId)

                        let otherGuid = Guid.NewGuid()
                        let otherEntity = Payments.paymentId otherGuid

                        let conflictingAttempt =
                            { attempt with
                                OrderId = $"order:{otherGuid:D}" }

                        let! conflicting =
                            Machine.send
                                started.Machine
                                otherEntity
                                (EventEnvelope.create
                                    $"payment-db:conflict:{otherGuid:D}"
                                    (AuthorizeRequested conflictingAttempt))
                                CancellationToken.None

                        Assert.True(Result.isOk conflicting)

                        let! _ =
                            waitForState started otherEntity (function
                                | AuthorizationPending _ -> true
                                | _ -> false)

                        let! _ = dispatcher.PollAsync CancellationToken.None
                        Assert.Equal(1, gateway.AuthorizeCalls attempt.OperationId)

                        use binding =
                            new NpgsqlCommand(
                                "SELECT payment_entity_id,order_id FROM fsnix.payment_operations WHERE operation_id=@operation",
                                connection
                            )

                        binding.Parameters.AddWithValue("operation", PaymentOperationId.value attempt.OperationId)
                        |> ignore

                        let! bindingReader = binding.ExecuteReaderAsync()
                        Assert.True(bindingReader.Read())
                        Assert.Equal(EntityId.value entity, bindingReader.GetString 0)
                        Assert.Equal(attempt.OrderId, bindingReader.GetString 1)
                    finally
                        workers.Cancel()
                }

            do! run
            let! _ = processing
            do! Machine.stopAsync started.Machine CancellationToken.None
            started.DataSource.Dispose()
        }
