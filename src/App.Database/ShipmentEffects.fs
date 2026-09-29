namespace App.Database

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Domain
open App.Orders
open App.Shipments
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

/// <summary>
/// Effects for the shipments machine. Label creation crosses the carrier boundary outside any
/// database transaction; the deterministic sandbox carrier makes redelivery converge by storing
/// the resulting reference on the shipment row and replaying the callback idempotently.
/// </summary>
[<RequireQualifiedAccess>]
module ShipmentEffects =
    let private labelFailed = ReasonCode.ofLiteral "label-failed"

    let actionKind =
        function
        | ConfirmAllocation _ -> "confirm-allocation"
        | CreateCarrierLabel _ -> "create-carrier-label"
        | NotifyOrderDispatched _ -> "notify-order-dispatched"
        | NotifyOrderDelivered _ -> "notify-order-delivered"
        | RequestDeliveryRetry _ -> "request-delivery-retry"

    let private key (record: ActionRecord<ShipmentEntityId, ShipmentAction>) purpose =
        $"xmsg:v1:{MachineId.value record.MachineId}:{CommandId.value record.CommandId}:{record.Ordinal}:{purpose}"

    let private receipt
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<ShipmentEntityId, ShipmentAction>)
        (ct: CancellationToken)
        =
        task {
            let json =
                ShipmentCodec.action.Encode record.Action
                |> Result.defaultWith (fun _ -> "invalid")

            let hash = SHA256.HashData(Encoding.UTF8.GetBytes json)

            use insert =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.action_receipts(machine_id,command_id,ordinal,action_kind,payload_hash) VALUES(@machine,@command,@ordinal,@kind,@hash) ON CONFLICT DO NOTHING RETURNING command_id",
                    connection,
                    tx
                )

            insert.Parameters.AddWithValue("machine", MachineId.value record.MachineId)
            |> ignore

            insert.Parameters.AddWithValue("command", CommandId.value record.CommandId)
            |> ignore

            insert.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore
            insert.Parameters.AddWithValue("kind", actionKind record.Action) |> ignore
            insert.Parameters.AddWithValue("hash", hash) |> ignore
            let! inserted = insert.ExecuteScalarAsync ct

            if not (isNull inserted) then
                return Ok true
            else
                use verify =
                    new NpgsqlCommand(
                        "SELECT action_kind,payload_hash FROM fsnix.action_receipts WHERE machine_id=@machine AND command_id=@command AND ordinal=@ordinal",
                        connection,
                        tx
                    )

                verify.Parameters.AddWithValue("machine", MachineId.value record.MachineId)
                |> ignore

                verify.Parameters.AddWithValue("command", CommandId.value record.CommandId)
                |> ignore

                verify.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore
                let! reader = verify.ExecuteReaderAsync ct
                let! found = reader.ReadAsync ct

                let matches =
                    found
                    && reader.GetString 0 = actionKind record.Action
                    && CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte array>(1), hash)

                reader.Dispose()

                return
                    if matches then
                        Ok false
                    else
                        Error ShipmentActionError.ActionReceiptMismatch
        }

    let private callback
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        callbackKey
        machine
        entity
        eventJson
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.integration_outbox(callback_key,machine_id,entity_id,event) VALUES(@key,@machine,@entity,@event::jsonb) ON CONFLICT(callback_key) DO NOTHING",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("key", callbackKey) |> ignore
            command.Parameters.AddWithValue("machine", machine) |> ignore
            command.Parameters.AddWithValue("entity", entity) |> ignore
            command.Parameters.AddWithValue("event", eventJson) |> ignore
            let! _ = command.ExecuteNonQueryAsync ct
            return ()
        }

    let private selfCallback connection tx record purpose (event: ShipmentEvent) ct =
        task {
            match ShipmentCodec.event.Encode event with
            | Error _ -> return Error ShipmentActionError.CallbackEncodingFailed
            | Ok json ->
                do!
                    callback
                        connection
                        tx
                        (key record purpose)
                        Shipments.MachineKey
                        (EntityId.value record.EntityId)
                        json
                        ct

                return Ok()
        }

    let private shipmentExists
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        allocationId
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM fsnix.shipments WHERE allocation_id=@allocation)",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("allocation", ShipmentAllocationId.value allocationId)
            |> ignore

            let! result = command.ExecuteScalarAsync ct
            return result :?> bool
        }

    let private readCarrierReference
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        shipmentId
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT carrier_reference FROM fsnix.shipments WHERE shipment_id=@shipment",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId)
            |> ignore

            let! result = command.ExecuteScalarAsync ct

            return
                if isNull result || result = DBNull.Value then
                    None
                else
                    Some(result :?> string)
        }

    let private recordCarrierReference
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        shipmentId
        reference
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "UPDATE fsnix.shipments SET carrier_reference=@reference WHERE shipment_id=@shipment AND carrier_reference IS NULL",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("reference", CarrierReference.value reference)
            |> ignore

            command.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId)
            |> ignore

            let! _ = command.ExecuteNonQueryAsync ct
            return ()
        }

    let applyConfirmAllocation
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<ShipmentEntityId, ShipmentAction>)
        (ct: CancellationToken)
        =
        task {
            match record.Action with
            | ConfirmAllocation allocationId ->
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync ct
                use! tx = connection.BeginTransactionAsync ct

                match! receipt connection tx record ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok _ ->
                    let! exists = shipmentExists connection tx allocationId ct

                    let event =
                        if exists then
                            AllocationConfirmed allocationId
                        else
                            AllocationRejected(allocationId, ReasonCode.ofLiteral "allocation-not-found")

                    let! callbackOutcome = selfCallback connection tx record "allocation-result" event ct

                    match callbackOutcome with
                    | Error error ->
                        do! tx.RollbackAsync ct
                        return Error error
                    | Ok() ->
                        do! tx.CommitAsync ct
                        return Ok()
            | _ -> return Error ShipmentActionError.InvalidAction
        }

    let applyCreateLabel
        (dataSource: NpgsqlDataSource)
        (carrier: ICarrier)
        (record: ActionRecord<ShipmentEntityId, ShipmentAction>)
        (ct: CancellationToken)
        =
        task {
            match record.Action with
            | CreateCarrierLabel(shipmentId, generation) ->
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync ct
                use! tx = connection.BeginTransactionAsync ct

                let! existing = readCarrierReference connection tx shipmentId ct

                match existing with
                | Some reference ->
                    // Label already created durably: replay the callback idempotently.
                    match! receipt connection tx record ct with
                    | Error error ->
                        do! tx.RollbackAsync ct
                        return Error error
                    | Ok _ ->
                        match CarrierReference.tryParse reference with
                        | Error _ ->
                            do! tx.RollbackAsync ct
                            return Error ShipmentActionError.CarrierRequestFailed
                        | Ok carrierReference ->
                            let! callbackOutcome =
                                selfCallback
                                    connection
                                    tx
                                    record
                                    "label-result"
                                    (LabelCreated(generation, carrierReference))
                                    ct

                            match callbackOutcome with
                            | Error error ->
                                do! tx.RollbackAsync ct
                                return Error error
                            | Ok() ->
                                do! tx.CommitAsync ct
                                return Ok()
                | None ->
                    do! tx.CommitAsync ct

                    // The carrier call runs outside any database transaction.
                    let! outcome = carrier.CreateLabel(shipmentId, ct)

                    use settleConnection = dataSource.CreateConnection()
                    do! settleConnection.OpenAsync ct
                    use! settleTx = settleConnection.BeginTransactionAsync ct

                    match! receipt settleConnection settleTx record ct with
                    | Error error ->
                        do! settleTx.RollbackAsync ct
                        return Error error
                    | Ok _ ->
                        let reference, event =
                            match outcome with
                            | CarrierLabelCreated reference -> Some reference, LabelCreated(generation, reference)
                            | CarrierLabelFailed reason ->
                                None, LabelCreationFailed(generation, ReasonCode.sanitize labelFailed reason)

                        match reference with
                        | Some reference -> do! recordCarrierReference settleConnection settleTx shipmentId reference ct
                        | None -> ()

                        let! callbackOutcome = selfCallback settleConnection settleTx record "label-result" event ct

                        match callbackOutcome with
                        | Error error ->
                            do! settleTx.RollbackAsync ct
                            return Error error
                        | Ok() ->
                            do! settleTx.CommitAsync ct
                            return Ok()
            | _ -> return Error ShipmentActionError.InvalidAction
        }

    let applyNotifyOrder
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<ShipmentEntityId, ShipmentAction>)
        (ct: CancellationToken)
        =
        task {
            let notify =
                match record.Action with
                | NotifyOrderDispatched(orderId, shipmentId, allocationId, _) ->
                    Some(ShipmentDispatched(shipmentId, allocationId), orderId, "notify-order-dispatched")
                | NotifyOrderDelivered(orderId, shipmentId, allocationId, _) ->
                    Some(ShipmentDelivered(shipmentId, allocationId), orderId, "notify-order-delivered")
                | _ -> None

            match notify with
            | None -> return Error ShipmentActionError.InvalidAction
            | Some(orderEvent, entity, purpose) ->
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync ct
                use! tx = connection.BeginTransactionAsync ct

                match! receipt connection tx record ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok _ ->
                    match record.Action with
                    | NotifyOrderDelivered(_, shipmentId, _, deliveredAt) ->
                        use delivered =
                            new NpgsqlCommand(
                                "UPDATE fsnix.shipments SET delivered_at=COALESCE(delivered_at,@delivered) WHERE shipment_id=@shipment",
                                connection,
                                tx
                            )

                        delivered.Parameters.AddWithValue("delivered", deliveredAt) |> ignore

                        delivered.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId)
                        |> ignore

                        let! _ = delivered.ExecuteNonQueryAsync ct
                        ()
                    | _ -> ()

                    match OrderCodec.event.Encode orderEvent with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error ShipmentActionError.CallbackEncodingFailed
                    | Ok eventJson ->
                        do! callback connection tx (key record purpose) Orders.MachineKey entity eventJson ct
                        do! tx.CommitAsync ct
                        return Ok()
        }

    let applyDeliveryRetry
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<ShipmentEntityId, ShipmentAction>)
        (ct: CancellationToken)
        =
        task {
            match record.Action with
            | RequestDeliveryRetry _ ->
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync ct
                use! tx = connection.BeginTransactionAsync ct

                match! receipt connection tx record ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok _ ->
                    do! tx.CommitAsync ct
                    return Ok()
            | _ -> return Error ShipmentActionError.InvalidAction
        }
