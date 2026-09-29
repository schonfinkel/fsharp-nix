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

[<RequireQualifiedAccess>]
module ShipmentSql =
    let allocationExists = Sql.load "Shipments/allocation-exists"
    let carrierReference = Sql.load "Shipments/carrier-reference"
    let markDelivered = Sql.load "Shipments/mark-delivered"
    let recordTrackingCheckpoint = Sql.load "Shipments/record-tracking-checkpoint"
    let setCarrierReference = Sql.load "Shipments/set-carrier-reference"
    let stopTrackingCheck = Sql.load "Shipments/stop-tracking-check"

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
        | RecordTrackingCheckpoint _ -> "record-tracking-checkpoint"
        | StopTrackingCheck _ -> "stop-tracking-check"

    let private key (record: ActionRecord<ShipmentEntityId, ShipmentAction>) purpose =
        $"xmsg:v1:{MachineId.value record.MachineId}:{CommandId.value record.CommandId}:{record.Ordinal}:{purpose}"

    let private receipt
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<ShipmentEntityId, ShipmentAction>)
        (ct: CancellationToken)
        =
        WorkflowEffects.firstDelivery
            ShipmentCodec.action
            actionKind
            ShipmentActionError.CallbackEncodingFailed
            ShipmentActionError.ActionReceiptMismatch
            connection
            tx
            record
            ct

    let private callback connection tx callbackKey machine entity eventJson ct =
        WorkflowEffects.callback connection tx callbackKey machine entity eventJson ct

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
            use command = new NpgsqlCommand(ShipmentSql.allocationExists, connection, tx)

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
            use command = new NpgsqlCommand(ShipmentSql.carrierReference, connection, tx)

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
            use command = new NpgsqlCommand(ShipmentSql.setCarrierReference, connection, tx)

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
                        use delivered = new NpgsqlCommand(ShipmentSql.markDelivered, connection, tx)

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

    /// <summary>Keeps the lost-shipment ledger in step with the carrier scans the machine
    /// accepted. Local only (receipt plus one ledger write); the scanner is the only reader.</summary>
    let applyTrackingCheck
        (dataSource: NpgsqlDataSource)
        (lostAfter: TimeSpan)
        (record: ActionRecord<ShipmentEntityId, ShipmentAction>)
        (ct: CancellationToken)
        : Task<Result<unit, ShipmentActionError>> =
        let receiptOf connection tx token =
            task {
                let! receipt = WorkflowEffects.receiptFor ShipmentCodec.action actionKind connection tx record token

                return
                    receipt
                    |> Result.mapError (function
                        | ReceiptFailure.EncodingFailed -> ShipmentActionError.CallbackEncodingFailed
                        | ReceiptFailure.Mismatch -> ShipmentActionError.ActionReceiptMismatch)
            }

        let write (sql: string) (bind: NpgsqlCommand -> unit) =
            WorkflowEffects.runLocalEffect
                dataSource
                receiptOf
                (fun connection tx token ->
                    task {
                        use command = new NpgsqlCommand(sql, connection, tx)
                        bind command
                        let! _ = command.ExecuteNonQueryAsync token
                        return Ok()
                    })
                ct

        match record.Action with
        | RecordTrackingCheckpoint(shipmentId, generation, lastScanAt, observedAt) when
            Shipments.shipmentEntityId shipmentId = record.EntityId
            ->
            write ShipmentSql.recordTrackingCheckpoint (fun command ->
                command.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId)
                |> ignore

                command.Parameters.AddWithValue("generation", generation) |> ignore

                command.Parameters.Add(
                    NpgsqlParameter(
                        "last_scan",
                        NpgsqlTypes.NpgsqlDbType.TimestampTz,
                        Value = (lastScanAt |> Option.map box |> Option.defaultValue DBNull.Value)
                    )
                )
                |> ignore

                command.Parameters.AddWithValue("observed", observedAt) |> ignore

                command.Parameters.AddWithValue("lost_after_seconds", int64 lostAfter.TotalSeconds)
                |> ignore)
        | StopTrackingCheck shipmentId when Shipments.shipmentEntityId shipmentId = record.EntityId ->
            write ShipmentSql.stopTrackingCheck (fun command ->
                command.Parameters.AddWithValue("shipment", ShipmentId.value shipmentId)
                |> ignore)
        | _ -> Task.FromResult(Error ShipmentActionError.InvalidAction)
