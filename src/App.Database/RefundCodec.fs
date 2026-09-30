namespace App.Database

open System
open System.Text.Json
open System.Text.Json.Serialization
open App.Domain
open App.Refunds
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging

module RefundWire =
    [<CLIMutable>]
    type WireDto =
        { Tag: string
          Refund: PaymentWire.RefundDto
          PaymentReference: string
          ProviderRefundReference: string
          Reason: string
          AllocationId: string
          [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
          OperationId: string }

[<RequireQualifiedAccess>]
module RefundCodec =
    let private options =
        JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

    do options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let private empty tag : RefundWire.WireDto =
        { Tag = tag
          Refund = Unchecked.defaultof<_>
          PaymentReference = ""
          ProviderRefundReference = ""
          Reason = ""
          AllocationId = ""
          OperationId = null }

    let private withRequest tag request =
        { empty tag with
            Refund = PaymentCodec.refundDto request }

    let private withApproval tag approved =
        { withRequest tag approved.Request with
            PaymentReference = approved.PaymentReference }

    let private error name message =
        CodecError.DecodeError(name, FormatException message)

    let private encode name dto =
        try
            Ok(JsonSerializer.Serialize(dto, options))
        with ex ->
            Error(CodecError.EncodeError(name, ex))

    let private decode name (json: string) : Result<RefundWire.WireDto, CodecError> =
        try
            let dto = JsonSerializer.Deserialize<RefundWire.WireDto>(json, options)

            if isNull (box dto) || String.IsNullOrWhiteSpace dto.Tag then
                Error(error name "Missing refund tag.")
            else
                Ok dto
        with ex ->
            Error(CodecError.DecodeError(name, ex))

    let private request name (dto: RefundWire.WireDto) =
        PaymentCodec.refundOfDto name dto.Refund

    let private approved name (dto: RefundWire.WireDto) =
        request name dto
        |> Result.bind (fun request ->
            if
                String.IsNullOrWhiteSpace dto.PaymentReference
                || dto.PaymentReference.Length > 256
            then
                Error(error name "Missing or invalid payment reference.")
            else
                Ok
                    { Request = request
                      PaymentReference = dto.PaymentReference })

    let state: Codec<RefundState> =
        Codec.create
            (fun (state: RefundState) ->
                let dto =
                    match state with
                    | Initial -> empty "initial-v1"
                    | AllocationPending r -> withRequest "allocation-pending-v1" r
                    | PendingGateway a -> withApproval "pending-gateway-v1" a
                    | OutcomeUnknown a -> withApproval "outcome-unknown-v1" a
                    | SettlementPending(a, reference) ->
                        { withApproval "settlement-pending-v1" a with
                            ProviderRefundReference = reference }
                    | Succeeded r -> withRequest "succeeded-v1" r
                    | Failed(r, reason) ->
                        { withRequest "failed-v1" r with
                            Reason = ReasonCode.value reason }
                    | ManualReview(r, reason) ->
                        { withRequest "manual-review-v1" r with
                            Reason = ReasonCode.value reason }
                    | Closed r -> withRequest "closed-v1" r

                encode "RefundState" dto)
            (fun json ->
                decode "RefundState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial-v1" -> Ok Initial
                    | "allocation-pending-v1" -> request "RefundState" dto |> Result.map AllocationPending
                    | "pending-gateway-v1" -> approved "RefundState" dto |> Result.map PendingGateway
                    | "outcome-unknown-v1" -> approved "RefundState" dto |> Result.map OutcomeUnknown
                    | "settlement-pending-v1" ->
                        approved "RefundState" dto
                        |> Result.bind (fun a ->
                            if String.IsNullOrWhiteSpace dto.ProviderRefundReference then
                                Error(error "RefundState" "Missing refund reference.")
                            else
                                Ok(SettlementPending(a, dto.ProviderRefundReference)))
                    | "succeeded-v1" -> request "RefundState" dto |> Result.map Succeeded
                    | "failed-v1" ->
                        request "RefundState" dto
                        |> Result.bind (fun r ->
                            CodecSupport.reason dto.Reason |> Result.map (fun reason -> Failed(r, reason)))
                    | "manual-review-v1" ->
                        request "RefundState" dto
                        |> Result.bind (fun r ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> ManualReview(r, reason)))
                    | "closed-v1" -> request "RefundState" dto |> Result.map Closed
                    | _ -> Error(error "RefundState" "Unknown refund state.")))

    let event: Codec<RefundEvent> =
        Codec.create
            (fun (value: RefundEvent) ->
                let dto =
                    match value with
                    | RefundRequested r -> withRequest "requested-v1" r
                    | AllocationApproved a -> withApproval "allocation-approved-v1" a
                    | AllocationDenied(r, reason) ->
                        { withRequest "allocation-denied-v1" r with
                            Reason = ReasonCode.value reason }
                    | GatewayRefunded(a, reference) ->
                        { withApproval "gateway-refunded-v1" a with
                            ProviderRefundReference = reference }
                    | GatewayDeclined(a, reason) ->
                        { withApproval "gateway-declined-v1" a with
                            Reason = ReasonCode.value reason }
                    | GatewayUnknown a -> withApproval "gateway-unknown-v1" a
                    | AllocationSettled id ->
                        { empty "allocation-settled-v1" with
                            AllocationId = RefundAllocationId.wireString id }
                    | ManualReviewRequested reason ->
                        { empty "manual-review-requested-v1" with
                            Reason = ReasonCode.value reason }
                    | ReconcileRequested operation ->
                        { empty "reconcile-requested-v1" with
                            OperationId = PaymentOperationId.value operation }
                    | ReconciliationExhausted operation ->
                        { empty "reconciliation-exhausted-v1" with
                            OperationId = PaymentOperationId.value operation }
                    | CloseRequested -> empty "close-requested-v1"

                encode "RefundEvent" dto)
            (fun json ->
                decode "RefundEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "requested-v1" -> request "RefundEvent" dto |> Result.map RefundRequested
                    | "allocation-approved-v1" -> approved "RefundEvent" dto |> Result.map AllocationApproved
                    | "allocation-denied-v1" ->
                        request "RefundEvent" dto
                        |> Result.bind (fun r ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> AllocationDenied(r, reason)))
                    | "gateway-refunded-v1" ->
                        approved "RefundEvent" dto
                        |> Result.map (fun a -> GatewayRefunded(a, dto.ProviderRefundReference))
                    | "gateway-declined-v1" ->
                        approved "RefundEvent" dto
                        |> Result.bind (fun a ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> GatewayDeclined(a, reason)))
                    | "gateway-unknown-v1" -> approved "RefundEvent" dto |> Result.map GatewayUnknown
                    | "allocation-settled-v1" ->
                        RefundAllocationId.tryParse dto.AllocationId
                        |> Result.mapError (error "RefundEvent")
                        |> Result.map AllocationSettled
                    | "manual-review-requested-v1" ->
                        (CodecSupport.reason dto.Reason |> Result.map ManualReviewRequested)
                    | "reconcile-requested-v1" ->
                        PaymentOperationId.tryParse dto.OperationId
                        |> Result.mapError (error "RefundEvent")
                        |> Result.map ReconcileRequested
                    | "reconciliation-exhausted-v1" ->
                        PaymentOperationId.tryParse dto.OperationId
                        |> Result.mapError (error "RefundEvent")
                        |> Result.map ReconciliationExhausted
                    | "close-requested-v1" -> Ok CloseRequested
                    | _ -> Error(error "RefundEvent" "Unknown refund event.")))

    let action: Codec<RefundAction> =
        Codec.create
            (fun (value: RefundAction) ->
                let dto =
                    match value with
                    | RequestAllocation r -> withRequest "request-allocation-v1" r
                    | CallGatewayRefund a -> withApproval "call-gateway-refund-v1" a
                    | QueryGatewayRefund a -> withApproval "query-gateway-refund-v1" a
                    | SettleAllocation(a, reference) ->
                        { withApproval "settle-allocation-v1" a with
                            ProviderRefundReference = reference }
                    | ReleaseAllocation r -> withRequest "release-allocation-v1" r
                    | NotifyOriginSucceeded r -> withRequest "notify-origin-succeeded-v1" r
                    | NotifyOriginFailed(r, reason) ->
                        { withRequest "notify-origin-failed-v1" r with
                            Reason = ReasonCode.value reason }

                encode "RefundAction" dto)
            (fun json ->
                decode "RefundAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "request-allocation-v1" -> request "RefundAction" dto |> Result.map RequestAllocation
                    | "call-gateway-refund-v1" -> approved "RefundAction" dto |> Result.map CallGatewayRefund
                    | "query-gateway-refund-v1" -> approved "RefundAction" dto |> Result.map QueryGatewayRefund
                    | "settle-allocation-v1" ->
                        approved "RefundAction" dto
                        |> Result.map (fun a -> SettleAllocation(a, dto.ProviderRefundReference))
                    | "release-allocation-v1" -> request "RefundAction" dto |> Result.map ReleaseAllocation
                    | "notify-origin-succeeded-v1" -> request "RefundAction" dto |> Result.map NotifyOriginSucceeded
                    | "notify-origin-failed-v1" ->
                        request "RefundAction" dto
                        |> Result.bind (fun r ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> NotifyOriginFailed(r, reason)))
                    | _ -> Error(error "RefundAction" "Unknown refund action.")))

    let actionError: Codec<RefundActionError> =
        Codec.create
            (fun (value: RefundActionError) ->
                let tag =
                    match value with
                    | RefundActionError.CallbackEncodingFailed -> "callback-encoding-v1"
                    | RefundActionError.ActionReceiptMismatch -> "receipt-mismatch-v1"
                    | RefundActionError.OperationConflict -> "operation-conflict-v1"
                    | RefundActionError.InvalidAction -> "invalid-action-v1"

                encode "RefundActionError" (empty tag))
            (fun json ->
                decode "RefundActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "callback-encoding-v1" -> Ok RefundActionError.CallbackEncodingFailed
                    | "receipt-mismatch-v1" -> Ok RefundActionError.ActionReceiptMismatch
                    | "operation-conflict-v1" -> Ok RefundActionError.OperationConflict
                    | "invalid-action-v1" -> Ok RefundActionError.InvalidAction
                    | _ -> Error(error "RefundActionError" "Unknown refund action error.")))

    let storeOptions
        (context: PostgresContext)
        : MachineStoreOptions<RefundEntityId, RefundState, RefundEvent, RefundAction, RefundActionError> =
        { MachineStoreOptions.forEntityId<Refund, RefundState, RefundEvent, RefundAction, RefundActionError>
              context
              Refunds.ActionQueue with
            StateCodec = state
            EventCodec = event
            ActionCodec = action
            ErrorCodec = actionError }

    let private build (log: ILogger) storeArg =
        machine<RefundEntityId, RefundState, RefundEvent, RefundAction, RefundActionError> (
            machineId Refunds.MachineKey
        ) {
            chart Refunds.chartValue
            chartVersion Refunds.ChartVersion
            initialState Refunds.initialState
            store storeArg
            logger log
        }

    let buildWorker log context =
        build
            log
            (PostgresMachineStore(storeOptions context)
            :> IMachineStore<RefundEntityId, RefundState, RefundEvent, RefundAction, RefundActionError>)

    let buildClient log context =
        build
            log
            (PostgresMachineStore(
                { storeOptions context with
                    Listener = ListenerConnection.Off }
            )
            :> IMachineStore<RefundEntityId, RefundState, RefundEvent, RefundAction, RefundActionError>)
