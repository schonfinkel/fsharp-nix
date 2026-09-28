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
          AllocationId: string }

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
          AllocationId = "" }

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
                    | Initial -> empty "initial"
                    | AllocationPending r -> withRequest "allocation-pending" r
                    | PendingGateway a -> withApproval "pending-gateway" a
                    | OutcomeUnknown a -> withApproval "outcome-unknown" a
                    | SettlementPending(a, reference) ->
                        { withApproval "settlement-pending" a with
                            ProviderRefundReference = reference }
                    | Succeeded r -> withRequest "succeeded" r
                    | Failed(r, reason) ->
                        { withRequest "failed" r with
                            Reason = reason }
                    | ManualReview(r, reason) ->
                        { withRequest "manual-review" r with
                            Reason = reason }
                    | Closed r -> withRequest "closed" r

                encode "RefundState" dto)
            (fun json ->
                decode "RefundState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial" -> Ok Initial
                    | "allocation-pending" -> request "RefundState" dto |> Result.map AllocationPending
                    | "pending-gateway" -> approved "RefundState" dto |> Result.map PendingGateway
                    | "outcome-unknown" -> approved "RefundState" dto |> Result.map OutcomeUnknown
                    | "settlement-pending" ->
                        approved "RefundState" dto
                        |> Result.bind (fun a ->
                            if String.IsNullOrWhiteSpace dto.ProviderRefundReference then
                                Error(error "RefundState" "Missing refund reference.")
                            else
                                Ok(SettlementPending(a, dto.ProviderRefundReference)))
                    | "succeeded" -> request "RefundState" dto |> Result.map Succeeded
                    | "failed" -> request "RefundState" dto |> Result.map (fun r -> Failed(r, dto.Reason))
                    | "manual-review" ->
                        request "RefundState" dto |> Result.map (fun r -> ManualReview(r, dto.Reason))
                    | "closed" -> request "RefundState" dto |> Result.map Closed
                    | _ -> Error(error "RefundState" "Unknown refund state.")))

    let event: Codec<RefundEvent> =
        Codec.create
            (fun (value: RefundEvent) ->
                let dto =
                    match value with
                    | RefundRequested r -> withRequest "requested" r
                    | AllocationApproved a -> withApproval "allocation-approved" a
                    | AllocationDenied(r, reason) ->
                        { withRequest "allocation-denied" r with
                            Reason = reason }
                    | GatewayRefunded(a, reference) ->
                        { withApproval "gateway-refunded" a with
                            ProviderRefundReference = reference }
                    | GatewayDeclined(a, reason) ->
                        { withApproval "gateway-declined" a with
                            Reason = reason }
                    | GatewayUnknown a -> withApproval "gateway-unknown" a
                    | AllocationSettled id ->
                        { empty "allocation-settled" with
                            AllocationId = RefundAllocationId.wireString id }
                    | ManualReviewRequested reason ->
                        { empty "manual-review-requested" with
                            Reason = reason }
                    | CloseRequested -> empty "close-requested"

                encode "RefundEvent" dto)
            (fun json ->
                decode "RefundEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "requested" -> request "RefundEvent" dto |> Result.map RefundRequested
                    | "allocation-approved" -> approved "RefundEvent" dto |> Result.map AllocationApproved
                    | "allocation-denied" ->
                        request "RefundEvent" dto
                        |> Result.map (fun r -> AllocationDenied(r, dto.Reason))
                    | "gateway-refunded" ->
                        approved "RefundEvent" dto
                        |> Result.map (fun a -> GatewayRefunded(a, dto.ProviderRefundReference))
                    | "gateway-declined" ->
                        approved "RefundEvent" dto
                        |> Result.map (fun a -> GatewayDeclined(a, dto.Reason))
                    | "gateway-unknown" -> approved "RefundEvent" dto |> Result.map GatewayUnknown
                    | "allocation-settled" ->
                        RefundAllocationId.tryParse dto.AllocationId
                        |> Result.mapError (error "RefundEvent")
                        |> Result.map AllocationSettled
                    | "manual-review-requested" -> Ok(ManualReviewRequested dto.Reason)
                    | "close-requested" -> Ok CloseRequested
                    | _ -> Error(error "RefundEvent" "Unknown refund event.")))

    let action: Codec<RefundAction> =
        Codec.create
            (fun (value: RefundAction) ->
                let dto =
                    match value with
                    | RequestAllocation r -> withRequest "request-allocation" r
                    | CallGatewayRefund a -> withApproval "call-gateway-refund" a
                    | QueryGatewayRefund a -> withApproval "query-gateway-refund" a
                    | SettleAllocation(a, reference) ->
                        { withApproval "settle-allocation" a with
                            ProviderRefundReference = reference }
                    | ReleaseAllocation r -> withRequest "release-allocation" r
                    | NotifyOriginSucceeded r -> withRequest "notify-origin-succeeded" r
                    | NotifyOriginFailed(r, reason) ->
                        { withRequest "notify-origin-failed" r with
                            Reason = reason }

                encode "RefundAction" dto)
            (fun json ->
                decode "RefundAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "request-allocation" -> request "RefundAction" dto |> Result.map RequestAllocation
                    | "call-gateway-refund" -> approved "RefundAction" dto |> Result.map CallGatewayRefund
                    | "query-gateway-refund" -> approved "RefundAction" dto |> Result.map QueryGatewayRefund
                    | "settle-allocation" ->
                        approved "RefundAction" dto
                        |> Result.map (fun a -> SettleAllocation(a, dto.ProviderRefundReference))
                    | "release-allocation" -> request "RefundAction" dto |> Result.map ReleaseAllocation
                    | "notify-origin-succeeded" -> request "RefundAction" dto |> Result.map NotifyOriginSucceeded
                    | "notify-origin-failed" ->
                        request "RefundAction" dto
                        |> Result.map (fun r -> NotifyOriginFailed(r, dto.Reason))
                    | _ -> Error(error "RefundAction" "Unknown refund action.")))

    let actionError: Codec<RefundActionError> =
        Codec.create
            (fun (value: RefundActionError) ->
                let tag =
                    match value with
                    | RefundActionError.CallbackEncodingFailed -> "callback-encoding"
                    | RefundActionError.ActionReceiptMismatch -> "receipt-mismatch"
                    | RefundActionError.OperationConflict -> "operation-conflict"
                    | RefundActionError.InvalidAction -> "invalid-action"

                encode "RefundActionError" (empty tag))
            (fun json ->
                decode "RefundActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "callback-encoding" -> Ok RefundActionError.CallbackEncodingFailed
                    | "receipt-mismatch" -> Ok RefundActionError.ActionReceiptMismatch
                    | "operation-conflict" -> Ok RefundActionError.OperationConflict
                    | "invalid-action" -> Ok RefundActionError.InvalidAction
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
            chartVersion 1
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
