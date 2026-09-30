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
                    | Initial -> empty "initial-v1"
                    | AuthorizationPending p -> progressDto "authorization-pending-v1" p
                    | Approved p -> progressDto "approved-v1" p
                    | ReturnState.Rejected(p, reason) ->
                        { progressDto "rejected-v1" p with
                            Reason = ReasonCode.value reason }
                    | LabelPending p -> progressDto "label-pending-v1" p
                    | LabelIssued(p, reference) ->
                        { progressDto "label-issued-v1" p with
                            Reference = CarrierReference.value reference }
                    | InTransit(p, reference) ->
                        { progressDto "in-transit-v1" p with
                            Reference = CarrierReference.value reference }
                    | Received(p, reference) ->
                        { progressDto "received-v1" p with
                            Reference = CarrierReference.value reference }
                    | Inspected(p, reference) ->
                        { progressDto "inspected-v1" p with
                            Reference = CarrierReference.value reference }
                    | RefundPending(p, refund) ->
                        { progressDto "refund-pending-v1" p with
                            Refund = PaymentCodec.refundDto refund }
                    | Refunded(p, refund) ->
                        { progressDto "refunded-v1" p with
                            Refund = PaymentCodec.refundDto refund }
                    | RejectedAfterInspection(p, reason) ->
                        { progressDto "rejected-after-inspection-v1" p with
                            Reason = ReasonCode.value reason }
                    | ManualReview(p, reason) ->
                        { progressDto "manual-review-v1" p with
                            Reason = ReasonCode.value reason }
                    | Closed p -> progressDto "closed-v1" p

                encode "ReturnState" dto)
            (fun json ->
                decode "ReturnState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial-v1" -> Ok Initial
                    | "authorization-pending-v1" -> progressOfDto "ReturnState" dto |> Result.map AuthorizationPending
                    | "approved-v1" -> progressOfDto "ReturnState" dto |> Result.map Approved
                    | "rejected-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> ReturnState.Rejected(p, reason)))
                    | "label-pending-v1" -> progressOfDto "ReturnState" dto |> Result.map LabelPending
                    | "label-issued-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            reference "ReturnState" dto |> Result.map (fun r -> LabelIssued(p, r)))
                    | "in-transit-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p -> reference "ReturnState" dto |> Result.map (fun r -> InTransit(p, r)))
                    | "received-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p -> reference "ReturnState" dto |> Result.map (fun r -> Received(p, r)))
                    | "inspected-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p -> reference "ReturnState" dto |> Result.map (fun r -> Inspected(p, r)))
                    | "refund-pending-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            PaymentCodec.refundOfDto "ReturnState" dto.Refund
                            |> Result.map (fun r -> RefundPending(p, r)))
                    | "refunded-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            PaymentCodec.refundOfDto "ReturnState" dto.Refund
                            |> Result.map (fun r -> Refunded(p, r)))
                    | "rejected-after-inspection-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> RejectedAfterInspection(p, reason)))
                    | "manual-review-v1" ->
                        progressOfDto "ReturnState" dto
                        |> Result.bind (fun p ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> ManualReview(p, reason)))
                    | "closed-v1" -> progressOfDto "ReturnState" dto |> Result.map Closed
                    | _ -> Error(error "ReturnState" "Unknown return state.")))

    let event: Codec<ReturnEvent> =
        Codec.create
            (fun (value: ReturnEvent) ->
                let dto =
                    match value with
                    | ReturnRequested r -> requestDto "requested-v1" r
                    | AuthorizationApproved id ->
                        { empty "authorization-approved-v1" with
                            AuthorizationId = ReturnAuthorizationId.wireString id }
                    | AuthorizationRejected(id, reason) ->
                        { empty "authorization-rejected-v1" with
                            AuthorizationId = ReturnAuthorizationId.wireString id
                            Reason = ReasonCode.value reason }
                    | LabelCreated reference ->
                        { empty "label-created-v1" with
                            Reference = CarrierReference.value reference }
                    | LabelFailed reason ->
                        { empty "label-failed-v1" with
                            Reason = ReasonCode.value reason }
                    | CarrierScanReceived id ->
                        { empty "carrier-scan-v1" with
                            ScanId = ReturnTrackingEventId.value id }
                    | ItemsReceived quantities ->
                        { empty "items-received-v1" with
                            Quantities = quantitiesDto quantities }
                    | InspectionApproved quantities ->
                        { empty "inspection-approved-v1" with
                            Quantities = quantitiesDto quantities }
                    | RestockCompleted id ->
                        { empty "restock-completed-v1" with
                            RefundId = ReturnId.wireString id }
                    | InspectionRejected reason ->
                        { empty "inspection-rejected-v1" with
                            Reason = ReasonCode.value reason }
                    | RefundSucceeded id ->
                        { empty "refund-succeeded-v1" with
                            RefundId = RefundId.wireString id }
                    | RefundFailed(id, reason) ->
                        { empty "refund-failed-v1" with
                            RefundId = RefundId.wireString id
                            Reason = ReasonCode.value reason }
                    | ReturnWindowExpired(id, deadline) ->
                        { empty "window-expired-v1" with
                            AuthorizationId = ReturnAuthorizationId.wireString id
                            WindowEndsAt = deadline.ToUnixTimeMilliseconds() }
                    | RefundStartRequested -> empty "refund-start-requested-v1"
                    | CloseRequested -> empty "close-requested-v1"

                encode "ReturnEvent" dto)
            (fun json ->
                decode "ReturnEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "requested-v1" -> requestOfDto "ReturnEvent" dto |> Result.map ReturnRequested
                    | "authorization-approved-v1" ->
                        ReturnAuthorizationId.tryParse dto.AuthorizationId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map AuthorizationApproved
                    | "authorization-rejected-v1" ->
                        ReturnAuthorizationId.tryParse dto.AuthorizationId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.bind (fun id ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> AuthorizationRejected(id, reason)))
                    | "label-created-v1" -> reference "ReturnEvent" dto |> Result.map LabelCreated
                    | "label-failed-v1" -> (CodecSupport.reason dto.Reason |> Result.map LabelFailed)
                    | "carrier-scan-v1" ->
                        ReturnTrackingEventId.tryParse dto.ScanId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map CarrierScanReceived
                    | "items-received-v1" -> quantitiesOfDto "ReturnEvent" dto.Quantities |> Result.map ItemsReceived
                    | "inspection-approved-v1" ->
                        quantitiesOfDto "ReturnEvent" dto.Quantities |> Result.map InspectionApproved
                    | "restock-completed-v1" ->
                        ReturnId.tryParse dto.RefundId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map RestockCompleted
                    | "inspection-rejected-v1" -> (CodecSupport.reason dto.Reason |> Result.map InspectionRejected)
                    | "refund-succeeded-v1" ->
                        RefundId.tryParse dto.RefundId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.map RefundSucceeded
                    | "refund-failed-v1" ->
                        RefundId.tryParse dto.RefundId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.bind (fun id ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> RefundFailed(id, reason)))
                    | "window-expired-v1" ->
                        ReturnAuthorizationId.tryParse dto.AuthorizationId
                        |> Result.mapError (error "ReturnEvent")
                        |> Result.bind (fun id ->
                            try
                                Ok(ReturnWindowExpired(id, DateTimeOffset.FromUnixTimeMilliseconds dto.WindowEndsAt))
                            with _ ->
                                Error(error "ReturnEvent" "Invalid deadline."))
                    | "refund-start-requested-v1" -> Ok RefundStartRequested
                    | "close-requested-v1" -> Ok CloseRequested
                    | _ -> Error(error "ReturnEvent" "Unknown return event.")))

    let action: Codec<ReturnAction> =
        Codec.create
            (fun (value: ReturnAction) ->
                let dto =
                    match value with
                    | VerifyAuthorization r -> requestDto "verify-authorization-v1" r
                    | IssueLabel r -> requestDto "issue-label-v1" r
                    | RestockItems(r, quantities) ->
                        { requestDto "restock-items-v1" r with
                            Quantities = quantitiesDto quantities }
                    | RequestRefund r ->
                        { empty "request-refund-v1" with
                            Refund = PaymentCodec.refundDto r }
                    | NotifyOrderRefunded r -> requestDto "notify-order-refunded-v1" r
                    | NotifyOrderRejected r -> requestDto "notify-order-rejected-v1" r

                encode "ReturnAction" dto)
            (fun json ->
                decode "ReturnAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "verify-authorization-v1" -> requestOfDto "ReturnAction" dto |> Result.map VerifyAuthorization
                    | "issue-label-v1" -> requestOfDto "ReturnAction" dto |> Result.map IssueLabel
                    | "restock-items-v1" ->
                        requestOfDto "ReturnAction" dto
                        |> Result.bind (fun r ->
                            quantitiesOfDto "ReturnAction" dto.Quantities
                            |> Result.map (fun q -> RestockItems(r, q)))
                    | "request-refund-v1" ->
                        PaymentCodec.refundOfDto "ReturnAction" dto.Refund |> Result.map RequestRefund
                    | "notify-order-refunded-v1" -> requestOfDto "ReturnAction" dto |> Result.map NotifyOrderRefunded
                    | "notify-order-rejected-v1" -> requestOfDto "ReturnAction" dto |> Result.map NotifyOrderRejected
                    | _ -> Error(error "ReturnAction" "Unknown return action.")))

    let actionError: Codec<ReturnActionError> =
        Codec.create
            (fun (value: ReturnActionError) ->
                let tag =
                    match value with
                    | ReturnActionError.CallbackEncodingFailed -> "callback-encoding-v1"
                    | ReturnActionError.ActionReceiptMismatch -> "receipt-mismatch-v1"
                    | ReturnActionError.InvalidAction -> "invalid-action-v1"

                encode "ReturnActionError" (empty tag))
            (fun json ->
                decode "ReturnActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "callback-encoding-v1" -> Ok ReturnActionError.CallbackEncodingFailed
                    | "receipt-mismatch-v1" -> Ok ReturnActionError.ActionReceiptMismatch
                    | "invalid-action-v1" -> Ok ReturnActionError.InvalidAction
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
            chartVersion Returns.ChartVersion
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
