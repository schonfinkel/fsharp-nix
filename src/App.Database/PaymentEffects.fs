namespace App.Database

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Domain
open App.Orders
open App.Payments
open App.Refunds
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

/// <summary>
/// Gateway-bound effects for the payments machine. Provider calls never run inside a database
/// transaction: phase one durably records the operation, the call happens outside, and phase
/// two settles the ledger row and queues the machine callback. Redelivery re-emits the
/// recorded terminal outcome, or queries before ever retrying, so one operation id performs at
/// most one real provider effect.
/// </summary>
[<RequireQualifiedAccess>]
module PaymentEffects =
    let actionKind =
        function
        | CallGatewayAuthorize _ -> "call-gateway-authorize"
        | QueryGatewayAuthorization _ -> "query-gateway-authorization"
        | CallGatewayCapture _ -> "call-gateway-capture"
        | QueryGatewayCapture _ -> "query-gateway-capture"
        | CallGatewayVoid _ -> "call-gateway-void"
        | NotifyOrderAuthorized _ -> "notify-order-authorized"
        | NotifyOrderDeclined _ -> "notify-order-declined"
        | NotifyOrderCancelled _ -> "notify-order-cancelled"
        | NotifyOrderVoided _ -> "notify-order-voided"
        | NotifyOrderCaptured _ -> "notify-order-captured"
        | NotifyRefundApproved _ -> "notify-refund-approved"
        | NotifyRefundDenied _ -> "notify-refund-denied"
        | NotifyRefundSettled _ -> "notify-refund-settled"

    let private key (record: ActionRecord<PaymentId, PaymentAction>) purpose =
        $"xmsg:v1:{MachineId.value record.MachineId}:{CommandId.value record.CommandId}:{record.Ordinal}:{purpose}"

    let private receipt
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (ct: CancellationToken)
        =
        task {
            let json =
                PaymentCodec.action.Encode record.Action
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
                        Error PaymentActionError.ActionReceiptMismatch
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

    let private selfCallback connection tx record purpose (event: PaymentEvent) ct =
        task {
            match PaymentCodec.event.Encode event with
            | Error _ -> return Error PaymentActionError.CallbackEncodingFailed
            | Ok json ->
                do!
                    callback
                        connection
                        tx
                        (key record purpose)
                        Payments.MachineKey
                        (EntityId.value record.EntityId)
                        json
                        ct

                return Ok()
        }

    type OperationRow =
        { PaymentEntityId: string
          OrderId: string
          Kind: string
          Status: string
          ProviderReference: string option
          ResultCode: string option
          ExpiresAt: DateTimeOffset option }

    let private readOperation
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        operationId
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT payment_entity_id,order_id,kind,status,provider_reference,result_code,expires_at FROM fsnix.payment_operations WHERE operation_id=@operation",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("operation", operationId) |> ignore
            let! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync ct

            let row =
                if found then
                    Some
                        { PaymentEntityId = reader.GetString 0
                          OrderId = reader.GetString 1
                          Kind = reader.GetString 2
                          Status = reader.GetString 3
                          ProviderReference = (if reader.IsDBNull 4 then None else Some(reader.GetString 4))
                          ResultCode = (if reader.IsDBNull 5 then None else Some(reader.GetString 5))
                          ExpiresAt =
                            (if reader.IsDBNull 6 then
                                 None
                             else
                                 Some(reader.GetFieldValue<DateTimeOffset> 6)) }
                else
                    None

            reader.Dispose()
            return row
        }

    let private ensureOperation
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<PaymentId, PaymentAction>)
        kind
        operationId
        orderId
        (ct: CancellationToken)
        =
        task {
            use insert =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.payment_operations(operation_id,payment_entity_id,order_id,kind) VALUES(@operation,@entity,@order,@kind) ON CONFLICT(operation_id) DO NOTHING",
                    connection,
                    tx
                )

            insert.Parameters.AddWithValue("operation", operationId) |> ignore

            insert.Parameters.AddWithValue("entity", EntityId.value record.EntityId)
            |> ignore

            insert.Parameters.AddWithValue("order", orderId) |> ignore
            insert.Parameters.AddWithValue("kind", kind) |> ignore
            let! _ = insert.ExecuteNonQueryAsync ct
            let! row = readOperation connection tx operationId ct

            return
                match row with
                | Some existing when
                    existing.PaymentEntityId <> EntityId.value record.EntityId
                    || existing.OrderId <> orderId
                    || existing.Kind <> kind
                    ->
                    Error PaymentActionError.OperationConflict
                | _ -> Ok row
        }

    let private settleOperation
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        operationId
        status
        reference
        result
        expires
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.payment_operations
                       SET status=@status,
                           provider_reference=@reference,
                           result_code=@result,
                           expires_at=@expires,
                           attempts=attempts+1,
                           updated_at=statement_timestamp()
                       WHERE operation_id=@operation
                         AND status IN ('pending','unknown')""",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("operation", operationId) |> ignore
            command.Parameters.AddWithValue("status", status) |> ignore

            command.Parameters.AddWithValue(
                "reference",
                match reference with
                | Some value -> box value
                | None -> DBNull.Value
            )
            |> ignore

            command.Parameters.AddWithValue(
                "result",
                match result with
                | Some value -> box value
                | None -> DBNull.Value
            )
            |> ignore

            command.Parameters.AddWithValue(
                "expires",
                match expires with
                | Some value -> box value
                | None -> DBNull.Value
            )
            |> ignore

            let! affected = command.ExecuteNonQueryAsync ct
            return affected = 1
        }

    let private validProviderReference (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && value
           |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = ':' || c = '/' || c = '_' || c = '-')

    let private providerDeclined = ReasonCode.ofLiteral "provider-declined"

    /// <summary>The recorded decline reason of a settled operation row.</summary>
    let private recordedReason (row: OperationRow) =
        row.ResultCode
        |> Option.map (ReasonCode.sanitize providerDeclined)
        |> Option.defaultValue providerDeclined

    let private authorizeOutcome attempt (outcome: GatewayAuthorization) =
        match outcome with
        | GatewayAuthorized(reference, expiresAt) when validProviderReference reference ->
            "succeeded",
            Some reference,
            Some "approved",
            Some expiresAt,
            AuthorizationSucceeded(attempt, reference, expiresAt)
        | GatewayAuthorized _ ->
            "unknown", None, Some "invalid-provider-response", None, AuthorizationOutcomeUnknown attempt
        | GatewayAuthorization.GatewayDeclined text ->
            let reason = ReasonCode.sanitize providerDeclined text
            "failed", None, Some(ReasonCode.value reason), None, AuthorizationDeclined(attempt, reason)
        | GatewayOutcomeUnknown -> "unknown", None, Some "outcome-unknown", None, AuthorizationOutcomeUnknown attempt

    let private authorizeRowEvent attempt (row: OperationRow) =
        match row.Status with
        | "succeeded" ->
            AuthorizationSucceeded(
                attempt,
                row.ProviderReference
                |> Option.defaultWith (fun () -> invalidOp "succeeded operations carry a reference"),
                row.ExpiresAt
                |> Option.defaultWith (fun () -> invalidOp "succeeded authorizations carry an expiry")
            )
        | "failed" -> AuthorizationDeclined(attempt, recordedReason row)
        | _ -> AuthorizationOutcomeUnknown attempt

    let private captureOutcome request (outcome: GatewayCapture) =
        match outcome with
        | GatewayCaptured reference when validProviderReference reference ->
            "succeeded", Some reference, Some "captured", None, CaptureSucceeded(request, reference)
        | GatewayCaptured _ -> "unknown", None, Some "invalid-provider-response", None, CaptureOutcomeUnknown request
        | GatewayCaptureDeclined text ->
            let reason = ReasonCode.sanitize providerDeclined text
            "failed", None, Some(ReasonCode.value reason), None, CaptureDeclined(request, reason)
        | GatewayCaptureUnknown -> "unknown", None, Some "outcome-unknown", None, CaptureOutcomeUnknown request

    let private captureRowEvent request (row: OperationRow) =
        match row.Status with
        | "succeeded" ->
            CaptureSucceeded(
                request,
                row.ProviderReference
                |> Option.defaultWith (fun () -> invalidOp "succeeded captures carry a reference")
            )
        | "failed" -> CaptureDeclined(request, recordedReason row)
        | _ -> CaptureOutcomeUnknown request

    let private recordAndSettle
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<PaymentId, PaymentAction>)
        kind
        operationId
        orderId
        firstCall
        query
        outcomeOf
        unknownEvent
        rowEvent
        purpose
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
            | Ok firstExecution ->
                match! ensureOperation connection tx record kind operationId orderId ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok row ->
                    let recorded =
                        row
                        |> Option.bind (fun row ->
                            if row.Status = "succeeded" || row.Status = "failed" then
                                Some(rowEvent row)
                            else
                                None)

                    match recorded with
                    | Some event ->
                        let! callbackOutcome = selfCallback connection tx record purpose event ct

                        match callbackOutcome with
                        | Error error ->
                            do! tx.RollbackAsync ct
                            return Error error
                        | Ok() ->
                            do! tx.CommitAsync ct
                            return Ok()
                    | None ->
                        do! tx.CommitAsync ct

                        let! outcome =
                            task {
                                if firstExecution then
                                    let! called = firstCall ()
                                    return Some called
                                else
                                    match! query () with
                                    | Some resolved -> return Some resolved
                                    | None ->
                                        let parked =
                                            row
                                            |> Option.map (fun existing -> existing.Status = "unknown")
                                            |> Option.defaultValue false

                                        if parked then
                                            return None
                                        else
                                            let! called = firstCall ()
                                            return Some called
                            }

                        let status, reference, result, expires, event =
                            match outcome with
                            | Some resolved -> outcomeOf resolved
                            | None -> ("unknown", None, Some "outcome-unknown", None, unknownEvent)

                        use settleConnection = dataSource.CreateConnection()
                        do! settleConnection.OpenAsync ct
                        use! settleTx = settleConnection.BeginTransactionAsync ct

                        let! settled =
                            settleOperation settleConnection settleTx operationId status reference result expires ct

                        let! callbackEvent =
                            if settled then
                                Task.FromResult event
                            else
                                task {
                                    match! readOperation settleConnection settleTx operationId ct with
                                    | Some authoritative -> return rowEvent authoritative
                                    | None -> return unknownEvent
                                }

                        let! callbackOutcome = selfCallback settleConnection settleTx record purpose callbackEvent ct

                        match callbackOutcome with
                        | Error error ->
                            do! settleTx.RollbackAsync ct
                            return Error error
                        | Ok() ->
                            do! settleTx.CommitAsync ct
                            return Ok()
        }

    let applyAuthorize
        (dataSource: NpgsqlDataSource)
        (gateway: IPaymentGateway)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (ct: CancellationToken)
        =
        match record.Action with
        | CallGatewayAuthorize attempt ->
            let operationId = PaymentOperationId.value attempt.OperationId
            let unknown = AuthorizationOutcomeUnknown attempt
            let unknownRowEvent (row: OperationRow) = authorizeRowEvent attempt row

            recordAndSettle
                dataSource
                record
                "authorize"
                operationId
                attempt.OrderId
                (fun () -> gateway.Authorize(attempt.OperationId, attempt.Amount, attempt.Method, ct))
                (fun () -> gateway.QueryAuthorization(attempt.OperationId, attempt.Method, ct))
                (authorizeOutcome attempt)
                unknown
                unknownRowEvent
                "authorize-result"
                ct
        | _ -> Task.FromResult(Error PaymentActionError.InvalidAction)

    let applyVoid
        (dataSource: NpgsqlDataSource)
        (gateway: IPaymentGateway)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (ct: CancellationToken)
        =
        match record.Action with
        | CallGatewayVoid authorized ->
            let voidOperation = PaymentOperationId.voidOf authorized.Attempt.OperationId
            let operationId = PaymentOperationId.value voidOperation

            let voidOutcome (outcome: GatewayVoid) =
                match outcome with
                | GatewayVoided -> "succeeded", None, Some "voided", None, VoidSucceeded authorized
                | GatewayVoidUnknown -> "unknown", None, Some "outcome-unknown", None, VoidOutcomeUnknown authorized

            let voidRowEvent (row: OperationRow) =
                match row.Status with
                | "succeeded" -> VoidSucceeded authorized
                | _ -> VoidOutcomeUnknown authorized

            recordAndSettle
                dataSource
                record
                "void"
                operationId
                authorized.Attempt.OrderId
                (fun () -> gateway.Void(voidOperation, authorized.ProviderReference, ct))
                (fun () -> Task.FromResult None)
                voidOutcome
                (VoidOutcomeUnknown authorized)
                voidRowEvent
                "void-result"
                ct
        | _ -> Task.FromResult(Error PaymentActionError.InvalidAction)

    let private applyCaptureAction
        (dataSource: NpgsqlDataSource)
        (gateway: IPaymentGateway)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (authorized: AuthorizedPayment)
        (request: CaptureRequest)
        (queryOnly: bool)
        (ct: CancellationToken)
        =
        let query () =
            gateway.QueryCapture(request.OperationId, authorized.ProviderReference, authorized.Attempt.Method, ct)

        let firstCall () =
            if queryOnly then
                task {
                    match! query () with
                    | Some outcome -> return outcome
                    | None -> return GatewayCaptureUnknown
                }
            else
                gateway.Capture(
                    request.OperationId,
                    authorized.ProviderReference,
                    request.Amount,
                    authorized.Attempt.Method,
                    ct
                )

        recordAndSettle
            dataSource
            record
            "capture"
            (PaymentOperationId.value request.OperationId)
            request.OrderId
            firstCall
            query
            (captureOutcome request)
            (CaptureOutcomeUnknown request)
            (captureRowEvent request)
            "capture-result"
            ct

    let applyCapture
        (dataSource: NpgsqlDataSource)
        (gateway: IPaymentGateway)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (ct: CancellationToken)
        =
        match record.Action with
        | CallGatewayCapture(authorized, request) ->
            applyCaptureAction dataSource gateway record authorized request false ct
        | _ -> Task.FromResult(Error PaymentActionError.InvalidAction)

    let applyQueryCapture
        (dataSource: NpgsqlDataSource)
        (gateway: IPaymentGateway)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (ct: CancellationToken)
        =
        match record.Action with
        | QueryGatewayCapture(authorized, request) ->
            applyCaptureAction dataSource gateway record authorized request true ct
        | _ -> Task.FromResult(Error PaymentActionError.InvalidAction)

    let applyQueryAuthorization
        (dataSource: NpgsqlDataSource)
        (gateway: IPaymentGateway)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (ct: CancellationToken)
        =
        task {
            match record.Action with
            | QueryGatewayAuthorization attempt ->
                let operationId = PaymentOperationId.value attempt.OperationId

                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync ct
                use! tx = connection.BeginTransactionAsync ct

                match! receipt connection tx record ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok _ ->
                    let! row = readOperation connection tx operationId ct

                    let recorded =
                        row
                        |> Option.bind (fun row ->
                            if row.Status = "succeeded" || row.Status = "failed" then
                                Some(authorizeRowEvent attempt row)
                            else
                                None)

                    do! tx.CommitAsync ct

                    match recorded with
                    | Some event ->
                        use settleConnection = dataSource.CreateConnection()
                        do! settleConnection.OpenAsync ct
                        use! settleTx = settleConnection.BeginTransactionAsync ct

                        let! callbackOutcome = selfCallback settleConnection settleTx record "authorize-result" event ct

                        match callbackOutcome with
                        | Error error ->
                            do! settleTx.RollbackAsync ct
                            return Error error
                        | Ok() ->
                            do! settleTx.CommitAsync ct
                            return Ok()
                    | None ->
                        let! resolved = gateway.QueryAuthorization(attempt.OperationId, attempt.Method, ct)

                        let status, reference, result, expires, event =
                            match resolved with
                            | Some outcome -> authorizeOutcome attempt outcome
                            | None ->
                                ("unknown", None, Some "outcome-unknown", None, AuthorizationOutcomeUnknown attempt)

                        use settleConnection = dataSource.CreateConnection()
                        do! settleConnection.OpenAsync ct
                        use! settleTx = settleConnection.BeginTransactionAsync ct

                        let! settled =
                            settleOperation settleConnection settleTx operationId status reference result expires ct

                        let! callbackEvent =
                            if settled then
                                Task.FromResult event
                            else
                                task {
                                    match! readOperation settleConnection settleTx operationId ct with
                                    | Some authoritative -> return authorizeRowEvent attempt authoritative
                                    | None -> return AuthorizationOutcomeUnknown attempt
                                }

                        let! callbackOutcome =
                            selfCallback settleConnection settleTx record "authorize-result" callbackEvent ct

                        match callbackOutcome with
                        | Error error ->
                            do! settleTx.RollbackAsync ct
                            return Error error
                        | Ok() ->
                            do! settleTx.CommitAsync ct
                            return Ok()
            | _ -> return Error PaymentActionError.InvalidAction
        }

    let applyNotify
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            let notify =
                match record.Action with
                | NotifyOrderAuthorized authorized ->
                    Some(
                        PaymentAuthorized(authorized.Attempt.OperationId, authorized.ProviderReference),
                        authorized.Attempt.OrderId,
                        "notify-order-authorized"
                    )
                | NotifyOrderDeclined(attempt, reason) ->
                    Some(PaymentDeclined(attempt.OperationId, reason), attempt.OrderId, "notify-order-declined")
                | NotifyOrderCancelled orderId -> Some(PaymentSettled, orderId, "notify-order-cancelled")
                | NotifyOrderVoided authorized ->
                    Some(PaymentSettled, authorized.Attempt.OrderId, "notify-order-voided")
                | NotifyOrderCaptured capture ->
                    Some(
                        PaymentCaptured(capture.Request.CaptureId, capture.Request.OperationId),
                        capture.Request.OrderId,
                        "notify-order-captured"
                    )
                | _ -> None

            match notify with
            | None ->
                do! tx.RollbackAsync ct
                return Error PaymentActionError.InvalidAction
            | Some(orderEvent, entity, purpose) ->
                match! receipt connection tx record ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok _ ->
                    match OrderCodec.event.Encode orderEvent with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error PaymentActionError.CallbackEncodingFailed
                    | Ok eventJson ->
                        do! callback connection tx (key record purpose) Orders.MachineKey entity eventJson ct
                        do! tx.CommitAsync ct
                        return Ok()
        }

    let applyNotifyRefund
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<PaymentId, PaymentAction>)
        (ct: CancellationToken)
        =
        task {
            let notify =
                match record.Action with
                | NotifyRefundApproved approved ->
                    Some(approved.Request, AllocationApproved approved, "refund-approved", true)
                | NotifyRefundDenied(request, reason) ->
                    Some(request, AllocationDenied(request, reason), "refund-denied", false)
                | NotifyRefundSettled request ->
                    Some(request, AllocationSettled request.AllocationId, "refund-settled", false)
                | _ -> None

            match notify with
            | None -> return Error PaymentActionError.InvalidAction
            | Some(request, event, purpose, reserve) ->
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync ct
                use! tx = connection.BeginTransactionAsync ct

                match! receipt connection tx record ct with
                | Error error ->
                    do! tx.RollbackAsync ct
                    return Error error
                | Ok _ ->
                    if reserve then
                        use insert =
                            new NpgsqlCommand(
                                "INSERT INTO fsnix.refund_allocations(allocation_id,refund_id,order_id,amount,currency,status) VALUES(@allocation,@refund,@order,@amount,@currency,'pending') ON CONFLICT(allocation_id) DO NOTHING",
                                connection,
                                tx
                            )

                        insert.Parameters.AddWithValue("allocation", RefundAllocationId.value request.AllocationId)
                        |> ignore

                        insert.Parameters.AddWithValue("refund", RefundId.value request.RefundId)
                        |> ignore

                        insert.Parameters.AddWithValue("order", RefundOrigin.orderId request.Origin)
                        |> ignore

                        insert.Parameters.AddWithValue("amount", Money.amount request.Amount) |> ignore

                        insert.Parameters.AddWithValue("currency", Money.currencyCode request.Amount)
                        |> ignore

                        let! _ = insert.ExecuteNonQueryAsync ct

                        use verify =
                            new NpgsqlCommand(
                                "SELECT refund_id,order_id,amount,currency,status FROM fsnix.refund_allocations WHERE allocation_id=@allocation",
                                connection,
                                tx
                            )

                        verify.Parameters.AddWithValue("allocation", RefundAllocationId.value request.AllocationId)
                        |> ignore

                        use! reader = verify.ExecuteReaderAsync ct
                        let! found = reader.ReadAsync ct

                        let valid =
                            found
                            && reader.GetGuid 0 = RefundId.value request.RefundId
                            && reader.GetString 1 = RefundOrigin.orderId request.Origin
                            && reader.GetDecimal 2 = Money.amount request.Amount
                            && reader.GetString 3 = Money.currencyCode request.Amount
                            && reader.GetString 4 = "pending"

                        reader.Dispose()

                        if not valid then
                            invalidOp "Refund allocation binding mismatch."

                    match RefundCodec.event.Encode event with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error PaymentActionError.CallbackEncodingFailed
                    | Ok eventJson ->
                        do!
                            callback
                                connection
                                tx
                                (key record purpose)
                                Refunds.MachineKey
                                (EntityId.value (Refunds.refundEntityId request.RefundId))
                                eventJson
                                ct

                        do! tx.CommitAsync ct
                        return Ok()
        }
