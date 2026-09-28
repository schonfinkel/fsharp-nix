namespace App.Database

open System
open System.Text.Json
open System.Text.Json.Serialization
open App.Domain
open App.Returns
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging

module ReturnWire =
    [<CLIMutable>]
    type QuantityDto = { LineId: string; Quantity: int }

    [<CLIMutable>]
    type WireDto =
        { Tag: string
          Request: OrderWire.ReturnDto
          Refund: PaymentWire.RefundDto
          Reference: string
          Reason: string
          AuthorizationId: string
          RefundId: string
          ScanId: string
          WindowEndsAt: int64
          Received: QuantityDto array
          Restocked: QuantityDto array
          RestockSettled: bool
          Quantities: QuantityDto array }

[<RequireQualifiedAccess>]
module ReturnCodec =
    let private options =
        JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

    do options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let private error name message =
        CodecError.DecodeError(name, FormatException message)

    let private empty tag : ReturnWire.WireDto =
        { Tag = tag
          Request = Unchecked.defaultof<_>
          Refund = Unchecked.defaultof<_>
          Reference = ""
          Reason = ""
          AuthorizationId = ""
          RefundId = ""
          ScanId = ""
          WindowEndsAt = 0L
          Received = [||]
          Restocked = [||]
          RestockSettled = false
          Quantities = [||] }

    let private encode name dto =
        try
            Ok(JsonSerializer.Serialize(dto, options))
        with ex ->
            Error(CodecError.EncodeError(name, ex))

    let private decode name (json: string) : Result<ReturnWire.WireDto, CodecError> =
        try
            let dto = JsonSerializer.Deserialize<ReturnWire.WireDto>(json, options)

            if isNull (box dto) || String.IsNullOrWhiteSpace dto.Tag then
                Error(error name "Missing return tag.")
            else
                Ok dto
        with ex ->
            Error(CodecError.DecodeError(name, ex))

    let private requestDto tag request =
        { empty tag with
            Request =
                OrderCodec.returnDto
                    { Request = request
                      Status = "pending" } }

    let private requestOfDto name (dto: ReturnWire.WireDto) =
        OrderCodec.returnOfDto name dto.Request |> Result.map _.Request

    let private quantitiesDto (quantities: (OrderLineId * int) list) : ReturnWire.QuantityDto array =
        quantities
        |> List.map (fun (lineId, quantity) ->
            ({ LineId = OrderLineId.wireString lineId
               Quantity = quantity }
            : ReturnWire.QuantityDto))
        |> List.toArray

    let private quantitiesOfDto name (values: ReturnWire.QuantityDto array) =
        (if isNull values then [||] else values)
        |> Array.toList
        |> List.map (fun value ->
            match Guid.TryParseExact(value.LineId, "D") with
            | true, id when value.Quantity > 0 ->
                OrderLineId.create id
                |> Result.mapError (error name)
                |> Result.map (fun line -> line, value.Quantity)
            | _ -> Error(error name "Invalid return quantity."))
        |> List.fold
            (fun acc value ->
                acc
                |> Result.bind (fun items -> value |> Result.map (fun item -> item :: items)))
            (Ok [])
        |> Result.map List.rev

    let private progressDto tag (p: ReturnProgress) =
        { requestDto tag p.Request with
            Received = quantitiesDto p.Received
            Restocked = quantitiesDto p.Restocked
            RestockSettled = p.RestockSettled
            ScanId = p.LastScan |> Option.map ReturnTrackingEventId.value |> Option.defaultValue "" }

    let private progressOfDto name (dto: ReturnWire.WireDto) =
        requestOfDto name dto
        |> Result.bind (fun request ->
            quantitiesOfDto name dto.Received
            |> Result.bind (fun received ->
                quantitiesOfDto name dto.Restocked
                |> Result.bind (fun restocked ->
                    let scan =
                        if String.IsNullOrWhiteSpace dto.ScanId then
                            Ok None
                        else
                            ReturnTrackingEventId.tryParse dto.ScanId
                            |> Result.mapError (error name)
                            |> Result.map Some

                    scan
                    |> Result.map (fun lastScan ->
                        { Request = request
                          Received = received
                          Restocked = restocked
                          RestockSettled = dto.RestockSettled
                          LastScan = lastScan }))))

    let private reference name (dto: ReturnWire.WireDto) =
        CarrierReference.tryParse dto.Reference |> Result.mapError (error name)

    let state: Codec<ReturnState> =
        Codec.create
            (fun (value: ReturnState) ->
                let dto =
                    match value with
                    | Initial -> empty "initial"
                    | AuthorizationPending p -> progressDto "authorization-pending" p
                    | Approved p -> progressDto "approved" p
                    | ReturnState.Rejected(p, reason) ->
                        { progressDto "rejected" p with
                            Reason = reason }
                    | LabelPending p -> progressDto "label-pending" p
                    | LabelIssued(p, reference) ->
                        { progressDto "label-issued" p with
                            Reference = CarrierReference.value reference }
                    | InTransit(p, reference) ->
                        { progressDto "in-transit" p with
                            Reference = CarrierReference.value reference }
                    | Received(p, reference) ->
                        { progressDto "received" p with
                            Reference = CarrierReference.value reference }
                    | Inspected(p, reference) ->
                        { progressDto "inspected" p with
                            Reference = CarrierReference.value reference }
                    | RefundPending(p, refund) ->
                        { progressDto "refund-pending" p with
                            Refund = PaymentCodec.refundDto refund }
                    | Refunded(p, refund) ->
                        { progressDto "refunded" p with
                            Refund = PaymentCodec.refundDto refund }
                    | RejectedAfterInspection(p, reason) ->
                        { progressDto "rejected-after-inspection" p with
                            Reason = reason }
                    | ManualReview(p, reason) ->
                        { progressDto "manual-review" p with
                            Reason = reason }
                    | Closed p -> progressDto "closed" p

                encode "ReturnState" dto)
            (fun json ->
                decode "ReturnState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial" -> Ok Initial
                    | "authorization-pending" -> progressOfDto "ReturnState" dto |> Result.map AuthorizationPending
                    | "approved" -> progressOfDto "ReturnState" dto |> Result.map Approved
                    | "rejected" ->
                        progressOfDto "ReturnState" dto
                        |> Result.map (fun p -> ReturnState.Rejected(p, dto.Reason))
                    | "label-pending" -> progressOfDto "ReturnState" dto |> Result.map LabelPending
                    | "label-issued" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            reference "ReturnState" dto |> Result.map (fun r -> LabelIssued(p, r)))
                    | "in-transit" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p -> reference "ReturnState" dto |> Result.map (fun r -> InTransit(p, r)))
                    | "received" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p -> reference "ReturnState" dto |> Result.map (fun r -> Received(p, r)))
                    | "inspected" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p -> reference "ReturnState" dto |> Result.map (fun r -> Inspected(p, r)))
                    | "refund-pending" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            PaymentCodec.refundOfDto "ReturnState" dto.Refund
                            |> Result.map (fun r -> RefundPending(p, r)))
                    | "refunded" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            PaymentCodec.refundOfDto "ReturnState" dto.Refund
                            |> Result.map (fun r -> Refunded(p, r)))
                    | "rejected-after-inspection" ->
                        progressOfDto "ReturnState" dto
                        |> Result.map (fun p -> RejectedAfterInspection(p, dto.Reason))
                    | "manual-review" ->
                        progressOfDto "ReturnState" dto
                        |> Result.map (fun p -> ManualReview(p, dto.Reason))
                    | "closed" -> progressOfDto "ReturnState" dto |> Result.map Closed
                    | _ -> Error(error "ReturnState" "Unknown return state.")))

    let event: Codec<ReturnEvent> =
        Codec.create
            (fun (value: ReturnEvent) ->
                let dto =
                    match value with
                    | ReturnRequested r -> requestDto "requested" r
                    | AuthorizationApproved id ->
                        { empty "authorization-approved" with
                            AuthorizationId = ReturnAuthorizationId.wireString id }
                    | AuthorizationRejected(id, reason) ->
                        { empty "authorization-rejected" with
                            AuthorizationId = ReturnAuthorizationId.wireString id
                            Reason = reason }
                    | LabelCreated reference ->
                        { empty "label-created" with
                            Reference = CarrierReference.value reference }
                    | LabelFailed reason ->
                        { empty "label-failed" with
                            Reason = reason }
                    | CarrierScanReceived id ->
                        { empty "carrier-scan" with
                            ScanId = ReturnTrackingEventId.value id }
                    | ItemsReceived quantities ->
                        { empty "items-received" with
                            Quantities = quantitiesDto quantities }
                    | InspectionApproved quantities ->
                        { empty "inspection-approved" with
                            Quantities = quantitiesDto quantities }
                    | RestockCompleted id ->
                        { empty "restock-completed" with
                            RefundId = ReturnId.wireString id }
                    | InspectionRejected reason ->
                        { empty "inspection-rejected" with
                            Reason = reason }
                    | RefundSucceeded id ->
                        { empty "refund-succeeded" with
                            RefundId = RefundId.wireString id }
                    | RefundFailed(id, reason) ->
                        { empty "refund-failed" with
                            RefundId = RefundId.wireString id
                            Reason = reason }
                    | ReturnWindowExpired(id, deadline) ->
                        { empty "window-expired" with
                            AuthorizationId = ReturnAuthorizationId.wireString id
                            WindowEndsAt = deadline.ToUnixTimeMilliseconds() }
                    | CloseRequested -> empty "close-requested"

                encode "ReturnEvent" dto)
            (fun json ->
                decode "ReturnEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "requested" -> requestOfDto "ReturnEvent" dto |> Result.map ReturnRequested
                    | "authorization-approved" ->
                        ReturnAuthorizationId.tryParse dto.AuthorizationId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map AuthorizationApproved
                    | "authorization-rejected" ->
                        ReturnAuthorizationId.tryParse dto.AuthorizationId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map (fun id -> AuthorizationRejected(id, dto.Reason))
                    | "label-created" -> reference "ReturnEvent" dto |> Result.map LabelCreated
                    | "label-failed" -> Ok(LabelFailed dto.Reason)
                    | "carrier-scan" ->
                        ReturnTrackingEventId.tryParse dto.ScanId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map CarrierScanReceived
                    | "items-received" -> quantitiesOfDto "ReturnEvent" dto.Quantities |> Result.map ItemsReceived
                    | "inspection-approved" ->
                        quantitiesOfDto "ReturnEvent" dto.Quantities |> Result.map InspectionApproved
                    | "restock-completed" ->
                        ReturnId.tryParse dto.RefundId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map RestockCompleted
                    | "inspection-rejected" -> Ok(InspectionRejected dto.Reason)
                    | "refund-succeeded" ->
                        RefundId.tryParse dto.RefundId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map RefundSucceeded
                    | "refund-failed" ->
                        RefundId.tryParse dto.RefundId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map (fun id -> RefundFailed(id, dto.Reason))
                    | "window-expired" ->
                        ReturnAuthorizationId.tryParse dto.AuthorizationId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.bind (fun id ->
                            try
                                Ok(ReturnWindowExpired(id, DateTimeOffset.FromUnixTimeMilliseconds dto.WindowEndsAt))
                            with _ ->
                                Error(error "ReturnEvent" "Invalid deadline."))
                    | "close-requested" -> Ok CloseRequested
                    | _ -> Error(error "ReturnEvent" "Unknown return event.")))

    let action: Codec<ReturnAction> =
        Codec.create
            (fun (value: ReturnAction) ->
                let dto =
                    match value with
                    | VerifyAuthorization r -> requestDto "verify-authorization" r
                    | IssueLabel r -> requestDto "issue-label" r
                    | RestockItems(r, quantities) ->
                        { requestDto "restock-items" r with
                            Quantities = quantitiesDto quantities }
                    | RequestRefund r ->
                        { empty "request-refund" with
                            Refund = PaymentCodec.refundDto r }
                    | NotifyOrderRefunded r -> requestDto "notify-order-refunded" r
                    | NotifyOrderRejected r -> requestDto "notify-order-rejected" r

                encode "ReturnAction" dto)
            (fun json ->
                decode "ReturnAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "verify-authorization" -> requestOfDto "ReturnAction" dto |> Result.map VerifyAuthorization
                    | "issue-label" -> requestOfDto "ReturnAction" dto |> Result.map IssueLabel
                    | "restock-items" ->
                        requestOfDto "ReturnAction" dto
                        |> Result.bind (fun r ->
                            quantitiesOfDto "ReturnAction" dto.Quantities
                            |> Result.map (fun q -> RestockItems(r, q)))
                    | "request-refund" ->
                        PaymentCodec.refundOfDto "ReturnAction" dto.Refund |> Result.map RequestRefund
                    | "notify-order-refunded" -> requestOfDto "ReturnAction" dto |> Result.map NotifyOrderRefunded
                    | "notify-order-rejected" -> requestOfDto "ReturnAction" dto |> Result.map NotifyOrderRejected
                    | _ -> Error(error "ReturnAction" "Unknown return action.")))

    let actionError: Codec<ReturnActionError> =
        Codec.create
            (fun (value: ReturnActionError) ->
                let tag =
                    match value with
                    | ReturnActionError.CallbackEncodingFailed -> "callback-encoding"
                    | ReturnActionError.ActionReceiptMismatch -> "receipt-mismatch"
                    | ReturnActionError.InvalidAction -> "invalid-action"

                encode "ReturnActionError" (empty tag))
            (fun json ->
                decode "ReturnActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "callback-encoding" -> Ok ReturnActionError.CallbackEncodingFailed
                    | "receipt-mismatch" -> Ok ReturnActionError.ActionReceiptMismatch
                    | "invalid-action" -> Ok ReturnActionError.InvalidAction
                    | _ -> Error(error "ReturnActionError" "Unknown return action error.")))

    let storeOptions
        (context: PostgresContext)
        : MachineStoreOptions<ReturnEntityId, ReturnState, ReturnEvent, ReturnAction, ReturnActionError> =
        { MachineStoreOptions.forEntityId<Return, ReturnState, ReturnEvent, ReturnAction, ReturnActionError>
              context
              Returns.ActionQueue with
            StateCodec = state
            EventCodec = event
            ActionCodec = action
            ErrorCodec = actionError }

    let private build (log: ILogger) storeArg =
        machine<ReturnEntityId, ReturnState, ReturnEvent, ReturnAction, ReturnActionError> (
            machineId Returns.MachineKey
        ) {
            chart Returns.chartValue
            chartVersion 1
            initialState Returns.initialState
            store storeArg
            logger log
        }

    let buildWorker log context =
        build
            log
            (PostgresMachineStore(storeOptions context)
            :> IMachineStore<ReturnEntityId, ReturnState, ReturnEvent, ReturnAction, ReturnActionError>)

    let buildClient log context =
        build
            log
            (PostgresMachineStore(
                { storeOptions context with
                    Listener = ListenerConnection.Off }
            )
            :> IMachineStore<ReturnEntityId, ReturnState, ReturnEvent, ReturnAction, ReturnActionError>)
