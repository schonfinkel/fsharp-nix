namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open App.Orders
open App.Refunds
open App.Returns
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

[<RequireQualifiedAccess>]
module ReturnEffects =
    let private labelFailed = ReasonCode.ofLiteral "label-failed"

    let actionKind =
        function
        | VerifyAuthorization _ -> "verify-return-authorization"
        | IssueLabel _ -> "issue-return-label"
        | RestockItems _ -> "restock-return-items"
        | RequestRefund _ -> "request-return-refund"
        | NotifyOrderRefunded _ -> "notify-order-return-refunded"
        | NotifyOrderRejected _ -> "notify-order-return-rejected"

    let private receipt connection tx record ct =
        match ReturnCodec.action.Encode record.Action with
        | Error _ -> Task.FromResult(Error ReturnActionError.CallbackEncodingFailed)
        | Ok json ->
            task {
                match! WorkflowEffects.receipt connection tx record (actionKind record.Action) json ct with
                | Ok ReceiptStatus.FirstRun -> return Ok true
                | Ok ReceiptStatus.Duplicate -> return Ok false
                | Error ReceiptFailure.EncodingFailed -> return Error ReturnActionError.CallbackEncodingFailed
                | Error ReceiptFailure.Mismatch -> return Error ReturnActionError.ActionReceiptMismatch
            }

    let private deliver connection tx record purpose machine entity value (codec: Codec<'Event>) ct =
        task {
            match codec.Encode value with
            | Error _ -> return Error ReturnActionError.CallbackEncodingFailed
            | Ok json ->
                do! WorkflowEffects.callback connection tx (WorkflowEffects.key record purpose) machine entity json ct
                return Ok()
        }

    let applyLabel
        (dataSource: NpgsqlDataSource)
        (carrier: ICarrier)
        (record: ActionRecord<ReturnEntityId, ReturnAction>)
        (ct: CancellationToken)
        =
        task {
            match record.Action with
            | IssueLabel request ->
                let shipmentId =
                    ShipmentId.create (ReturnId.value request.ReturnId)
                    |> Result.defaultWith invalidOp

                let! outcome = carrier.CreateLabel(shipmentId, ct)

                let event =
                    match outcome with
                    | CarrierLabelCreated reference -> LabelCreated reference
                    | CarrierLabelFailed reason -> LabelFailed(ReasonCode.sanitize labelFailed reason)

                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync ct
                use! tx = connection.BeginTransactionAsync ct

                match! receipt connection tx record ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok _ ->
                    let! result =
                        deliver
                            connection
                            tx
                            record
                            "return-label"
                            Returns.MachineKey
                            (EntityId.value record.EntityId)
                            event
                            ReturnCodec.event
                            ct

                    match result with
                    | Error error ->
                        do! tx.RollbackAsync ct
                        return Error error
                    | Ok() ->
                        do! tx.CommitAsync ct
                        return Ok()
            | _ -> return Error ReturnActionError.InvalidAction
        }

    let applyLocal
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<ReturnEntityId, ReturnAction>)
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
            | Ok first ->
                let! result =
                    task {
                        match record.Action with
                        | VerifyAuthorization request ->
                            use query =
                                new NpgsqlCommand(
                                    "SELECT EXISTS(SELECT 1 FROM fsnix.return_requests WHERE return_id=@id AND authorization_id=@auth AND order_id=@order AND status='pending' AND window_ends_at > statement_timestamp())",
                                    connection,
                                    tx
                                )

                            query.Parameters.AddWithValue("id", ReturnId.value request.ReturnId) |> ignore

                            query.Parameters.AddWithValue("auth", ReturnAuthorizationId.value request.AuthorizationId)
                            |> ignore

                            query.Parameters.AddWithValue("order", request.OrderId) |> ignore
                            let! valid = query.ExecuteScalarAsync ct

                            let event =
                                if valid :?> bool then
                                    AuthorizationApproved request.AuthorizationId
                                else
                                    AuthorizationRejected(
                                        request.AuthorizationId,
                                        ReasonCode.ofLiteral "return-not-authorized"
                                    )

                            return!
                                deliver
                                    connection
                                    tx
                                    record
                                    "return-authorization"
                                    Returns.MachineKey
                                    (EntityId.value record.EntityId)
                                    event
                                    ReturnCodec.event
                                    ct
                        | RestockItems(request, quantities) ->
                            if first then
                                for (lineId, quantity) in quantities do
                                    use insert =
                                        new NpgsqlCommand(
                                            "INSERT INTO fsnix.return_restock(return_id,order_line_id,quantity,source_command_id) SELECT @return,@line,@quantity,@command WHERE EXISTS(SELECT 1 FROM fsnix.return_lines WHERE return_id=@return AND order_line_id=@line AND quantity>=@quantity) ON CONFLICT DO NOTHING RETURNING quantity",
                                            connection,
                                            tx
                                        )

                                    insert.Parameters.AddWithValue("return", ReturnId.value request.ReturnId)
                                    |> ignore

                                    insert.Parameters.AddWithValue("line", OrderLineId.value lineId) |> ignore
                                    insert.Parameters.AddWithValue("quantity", quantity) |> ignore

                                    insert.Parameters.AddWithValue("command", CommandId.value record.CommandId)
                                    |> ignore

                                    let! inserted = insert.ExecuteScalarAsync ct

                                    if isNull inserted then
                                        invalidOp "Duplicate or invalid restock allocation."

                                    use stock =
                                        new NpgsqlCommand(
                                            "UPDATE fsnix.product_stock SET on_hand=on_hand+@quantity FROM fsnix.stock_reservations r WHERE r.order_line_id=@line AND r.product_id=fsnix.product_stock.product_id AND r.status='committed'",
                                            connection,
                                            tx
                                        )

                                    stock.Parameters.AddWithValue("line", OrderLineId.value lineId) |> ignore
                                    stock.Parameters.AddWithValue("quantity", quantity) |> ignore
                                    let! affected = stock.ExecuteNonQueryAsync ct

                                    if affected <> 1 then
                                        invalidOp "Return restock has no committed stock line."

                            return!
                                deliver
                                    connection
                                    tx
                                    record
                                    "restock-completed"
                                    Returns.MachineKey
                                    (EntityId.value record.EntityId)
                                    (RestockCompleted request.ReturnId)
                                    ReturnCodec.event
                                    ct
                        | RequestRefund request ->
                            return!
                                deliver
                                    connection
                                    tx
                                    record
                                    "return-refund-request"
                                    Refunds.MachineKey
                                    (EntityId.value (Refunds.refundEntityId request.RefundId))
                                    (RefundRequested request)
                                    RefundCodec.event
                                    ct
                        | NotifyOrderRefunded request ->
                            return!
                                deliver
                                    connection
                                    tx
                                    record
                                    "return-refunded"
                                    Orders.MachineKey
                                    request.OrderId
                                    (ReturnRefunded request.ReturnId)
                                    OrderCodec.event
                                    ct
                        | NotifyOrderRejected request ->
                            return!
                                deliver
                                    connection
                                    tx
                                    record
                                    "return-rejected"
                                    Orders.MachineKey
                                    request.OrderId
                                    (ReturnRejected request.ReturnId)
                                    OrderCodec.event
                                    ct
                        | _ -> return Error ReturnActionError.InvalidAction
                    }

                match result with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok() ->
                    do! tx.CommitAsync ct
                    return Ok()
        }
