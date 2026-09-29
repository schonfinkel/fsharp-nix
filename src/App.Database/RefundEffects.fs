namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open App.Orders
open App.Payments
open App.Refunds
open App.Returns
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

[<RequireQualifiedAccess>]
module RefundSql =
    let insertRefundOperation = Sql.load "Refunds/insert-refund-operation"
    let refundOperationRow = Sql.load "Refunds/refund-operation-row"
    let refundOperationStatus = Sql.load "Refunds/refund-operation-status"
    let releaseAllocation = Sql.load "Refunds/release-allocation"
    let settleAllocation = Sql.load "Refunds/settle-allocation"
    let settleRefundOperation = Sql.load "Refunds/settle-refund-operation"

[<RequireQualifiedAccess>]
module RefundEffects =
    let actionKind =
        function
        | RequestAllocation _ -> "request-refund-allocation"
        | CallGatewayRefund _ -> "call-gateway-refund"
        | QueryGatewayRefund _ -> "query-gateway-refund"
        | SettleAllocation _ -> "settle-refund-allocation"
        | ReleaseAllocation _ -> "release-refund-allocation"
        | NotifyOriginSucceeded _ -> "notify-refund-origin-success"
        | NotifyOriginFailed _ -> "notify-refund-origin-failure"

    let private receipt connection tx record ct =
        match RefundCodec.action.Encode record.Action with
        | Error _ -> Task.FromResult(Error RefundActionError.CallbackEncodingFailed)
        | Ok json ->
            task {
                match! WorkflowEffects.receipt connection tx record (actionKind record.Action) json ct with
                | Ok ReceiptStatus.FirstRun -> return Ok true
                | Ok ReceiptStatus.Duplicate -> return Ok false
                | Error ReceiptFailure.EncodingFailed -> return Error RefundActionError.CallbackEncodingFailed
                | Error ReceiptFailure.Mismatch -> return Error RefundActionError.ActionReceiptMismatch
            }

    let private deliver connection tx record purpose machine entity codecEvent (codec: Codec<'Event>) ct =
        task {
            match codec.Encode codecEvent with
            | Error _ -> return Error RefundActionError.CallbackEncodingFailed
            | Ok json ->
                do! WorkflowEffects.callback connection tx (WorkflowEffects.key record purpose) machine entity json ct
                return Ok()
        }

    let private paymentEntity (orderId: string) =
        if not (orderId.StartsWith("order:", StringComparison.Ordinal)) then
            invalidOp "Invalid refund order id."
        else
            "payment:" + orderId.Substring(6)

    let applyLocal
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<RefundEntityId, RefundAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok _ ->
                let! result =
                    task {
                        match record.Action with
                        | RequestAllocation request ->
                            return!
                                deliver
                                    connection
                                    tx
                                    record
                                    "allocation-request"
                                    Payments.MachineKey
                                    (paymentEntity (RefundOrigin.orderId request.Origin))
                                    (RefundAllocationRequested request)
                                    PaymentCodec.event
                                    ct
                        | SettleAllocation(approved, _) ->
                            use update = new NpgsqlCommand(RefundSql.settleAllocation, connection, tx)

                            update.Parameters.AddWithValue("id", RefundAllocationId.value approved.Request.AllocationId)
                            |> ignore

                            let! _ = update.ExecuteNonQueryAsync ct

                            return!
                                deliver
                                    connection
                                    tx
                                    record
                                    "allocation-settled"
                                    Payments.MachineKey
                                    (paymentEntity (RefundOrigin.orderId approved.Request.Origin))
                                    (RefundAllocationSettled approved.Request.AllocationId)
                                    PaymentCodec.event
                                    ct
                        | ReleaseAllocation request ->
                            use update = new NpgsqlCommand(RefundSql.releaseAllocation, connection, tx)

                            update.Parameters.AddWithValue("id", RefundAllocationId.value request.AllocationId)
                            |> ignore

                            let! _ = update.ExecuteNonQueryAsync ct

                            return!
                                deliver
                                    connection
                                    tx
                                    record
                                    "allocation-released"
                                    Payments.MachineKey
                                    (paymentEntity (RefundOrigin.orderId request.Origin))
                                    (RefundAllocationReleased request.AllocationId)
                                    PaymentCodec.event
                                    ct
                        | NotifyOriginSucceeded request ->
                            match request.Origin with
                            | OrderCancellation orderId ->
                                return!
                                    deliver
                                        connection
                                        tx
                                        record
                                        "order-refunded"
                                        Orders.MachineKey
                                        orderId
                                        (OrderRefunded request.RefundId)
                                        OrderCodec.event
                                        ct
                            | InspectedReturn(_, returnId) ->
                                let id = ReturnId.create returnId |> Result.defaultWith invalidOp

                                return!
                                    deliver
                                        connection
                                        tx
                                        record
                                        "return-refunded"
                                        Returns.MachineKey
                                        (EntityId.value (Returns.returnEntityId id))
                                        (RefundSucceeded request.RefundId)
                                        ReturnCodec.event
                                        ct
                        | NotifyOriginFailed(request, reason) ->
                            match request.Origin with
                            | OrderCancellation orderId ->
                                return!
                                    deliver
                                        connection
                                        tx
                                        record
                                        "order-refund-failed"
                                        Orders.MachineKey
                                        orderId
                                        (OrderRefundFailed(request.RefundId, reason))
                                        OrderCodec.event
                                        ct
                            | InspectedReturn(_, returnId) ->
                                let id = ReturnId.create returnId |> Result.defaultWith invalidOp

                                return!
                                    deliver
                                        connection
                                        tx
                                        record
                                        "return-refund-failed"
                                        Returns.MachineKey
                                        (EntityId.value (Returns.returnEntityId id))
                                        (RefundFailed(request.RefundId, reason))
                                        ReturnCodec.event
                                        ct
                        | _ -> return Error RefundActionError.InvalidAction
                    }

                match result with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok() ->
                    do! tx.CommitAsync ct
                    return Ok()
        }

    let private providerDeclined = ReasonCode.ofLiteral "provider-declined"

    let private recordedEvent approved status reference result =
        match status with
        | "succeeded" ->
            GatewayRefunded(
                approved,
                reference
                |> Option.defaultWith (fun () -> invalidOp "Missing refund reference.")
            )
        | "failed" ->
            GatewayDeclined(
                approved,
                result
                |> Option.map (ReasonCode.sanitize providerDeclined)
                |> Option.defaultValue providerDeclined
            )
        | _ -> GatewayUnknown approved

    let applyGateway
        (dataSource: NpgsqlDataSource)
        (gateway: IPaymentGateway)
        (record: ActionRecord<RefundEntityId, RefundAction>)
        (ct: CancellationToken)
        =
        task {
            let action =
                match record.Action with
                | CallGatewayRefund approved -> Some(approved, false)
                | QueryGatewayRefund approved -> Some(approved, true)
                | _ -> None

            match action with
            | None -> return Error RefundActionError.InvalidAction
            | Some(approved, forceQuery) ->
                let request = approved.Request
                let operationId = PaymentOperationId.value request.OperationId
                let orderId = RefundOrigin.orderId request.Origin
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync ct
                use! tx = connection.BeginTransactionAsync ct

                match! receipt connection tx record ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok first ->
                    use insert = new NpgsqlCommand(RefundSql.insertRefundOperation, connection, tx)

                    insert.Parameters.AddWithValue("id", operationId) |> ignore
                    insert.Parameters.AddWithValue("entity", paymentEntity orderId) |> ignore
                    insert.Parameters.AddWithValue("order", orderId) |> ignore

                    insert.Parameters.AddWithValue("refund_entity", EntityId.value record.EntityId)
                    |> ignore

                    insert.Parameters.AddWithValue("amount", Money.amount request.Amount) |> ignore

                    insert.Parameters.AddWithValue("currency", Money.currencyCode request.Amount)
                    |> ignore

                    let! _ = insert.ExecuteNonQueryAsync ct

                    use read = new NpgsqlCommand(RefundSql.refundOperationRow, connection, tx)

                    read.Parameters.AddWithValue("id", operationId) |> ignore
                    use! reader = read.ExecuteReaderAsync ct
                    let! found = reader.ReadAsync ct

                    if not found then
                        invalidOp "Missing refund operation."

                    let matches =
                        reader.GetString 0 = paymentEntity orderId
                        && reader.GetString 1 = orderId
                        && reader.GetString 2 = "refund"
                        && reader.GetDecimal 3 = Money.amount request.Amount
                        && reader.GetString 4 = Money.currencyCode request.Amount

                    let status = reader.GetString 5
                    let reference = if reader.IsDBNull 6 then None else Some(reader.GetString 6)
                    let resultCode = if reader.IsDBNull 7 then None else Some(reader.GetString 7)
                    reader.Dispose()

                    if not matches then
                        do! tx.RollbackAsync ct
                        return Error RefundActionError.OperationConflict
                    elif status = "succeeded" || status = "failed" then
                        let event = recordedEvent approved status reference resultCode

                        let! delivered =
                            deliver
                                connection
                                tx
                                record
                                "gateway-refund-result"
                                Refunds.MachineKey
                                (EntityId.value record.EntityId)
                                event
                                RefundCodec.event
                                ct

                        match delivered with
                        | Error error ->
                            do! tx.RollbackAsync ct
                            return Error error
                        | Ok() ->
                            do! tx.CommitAsync ct
                            return Ok()
                    else
                        do! tx.CommitAsync ct

                        let! outcome =
                            task {
                                if first && not forceQuery then
                                    return!
                                        gateway.Refund(
                                            request.OperationId,
                                            approved.PaymentReference,
                                            request.Amount,
                                            ct
                                        )
                                else
                                    let! queried =
                                        gateway.QueryRefund(request.OperationId, approved.PaymentReference, ct)

                                    return queried |> Option.defaultValue GatewayRefund.GatewayRefundUnknown
                            }

                        let status, reference, result, event =
                            match outcome with
                            | GatewayRefund.GatewayRefunded providerRef when
                                not (String.IsNullOrWhiteSpace providerRef) && providerRef.Length <= 256
                                ->
                                "succeeded", Some providerRef, "refunded", GatewayRefunded(approved, providerRef)
                            | GatewayRefund.GatewayRefundDeclined text ->
                                let reason = ReasonCode.sanitize providerDeclined text
                                "failed", None, ReasonCode.value reason, GatewayDeclined(approved, reason)
                            | _ -> "unknown", None, "outcome-unknown", GatewayUnknown approved

                        use settleConnection = dataSource.CreateConnection()
                        do! settleConnection.OpenAsync ct
                        use! settleTx = settleConnection.BeginTransactionAsync ct

                        use update =
                            new NpgsqlCommand(RefundSql.settleRefundOperation, settleConnection, settleTx)

                        update.Parameters.AddWithValue("id", operationId) |> ignore
                        update.Parameters.AddWithValue("status", status) |> ignore

                        update.Parameters.AddWithValue(
                            "ref",
                            reference |> Option.map box |> Option.defaultValue (box DBNull.Value)
                        )
                        |> ignore

                        update.Parameters.AddWithValue("result", result) |> ignore
                        let! updated = update.ExecuteNonQueryAsync ct

                        let! authoritative =
                            if updated = 1 then
                                Task.FromResult event
                            else
                                task {
                                    use readBack =
                                        new NpgsqlCommand(RefundSql.refundOperationStatus, settleConnection, settleTx)

                                    readBack.Parameters.AddWithValue("id", operationId) |> ignore
                                    use! row = readBack.ExecuteReaderAsync ct
                                    let! _ = row.ReadAsync ct

                                    return
                                        recordedEvent
                                            approved
                                            (row.GetString 0)
                                            (if row.IsDBNull 1 then None else Some(row.GetString 1))
                                            (if row.IsDBNull 2 then None else Some(row.GetString 2))
                                }

                        let! delivered =
                            deliver
                                settleConnection
                                settleTx
                                record
                                "gateway-refund-result"
                                Refunds.MachineKey
                                (EntityId.value record.EntityId)
                                authoritative
                                RefundCodec.event
                                ct

                        match delivered with
                        | Error error ->
                            do! settleTx.RollbackAsync ct
                            return Error error
                        | Ok() ->
                            do! settleTx.CommitAsync ct
                            return Ok()
        }
