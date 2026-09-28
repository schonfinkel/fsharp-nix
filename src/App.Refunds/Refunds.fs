namespace App.Refunds

open System
open App.Domain
open ByzantineSystems.Automata.Core

type RefundState =
    | Initial
    | AllocationPending of RefundRequest
    | PendingGateway of ApprovedRefund
    | OutcomeUnknown of ApprovedRefund
    | SettlementPending of ApprovedRefund * providerRefundReference: string
    | Succeeded of RefundRequest
    | Failed of RefundRequest * reasonCode: string
    | ManualReview of RefundRequest * reasonCode: string
    | Closed of RefundRequest

type RefundEvent =
    | RefundRequested of RefundRequest
    | AllocationApproved of ApprovedRefund
    | AllocationDenied of RefundRequest * reasonCode: string
    | GatewayRefunded of ApprovedRefund * providerRefundReference: string
    | GatewayDeclined of ApprovedRefund * reasonCode: string
    | GatewayUnknown of ApprovedRefund
    | AllocationSettled of RefundAllocationId
    | ManualReviewRequested of reasonCode: string
    | CloseRequested

type RefundAction =
    | RequestAllocation of RefundRequest
    | CallGatewayRefund of ApprovedRefund
    | QueryGatewayRefund of ApprovedRefund
    | SettleAllocation of ApprovedRefund * providerRefundReference: string
    | ReleaseAllocation of RefundRequest
    | NotifyOriginSucceeded of RefundRequest
    | NotifyOriginFailed of RefundRequest * reasonCode: string

[<RequireQualifiedAccess>]
type RefundActionError =
    | CallbackEncodingFailed
    | ActionReceiptMismatch
    | OperationConflict
    | InvalidAction

type Refund = class end
type RefundEntityId = EntityId<Refund>

[<RequireQualifiedAccess>]
module Refunds =
    [<Literal>]
    let MachineKey = "refunds"

    [<Literal>]
    let ActionQueue = "refund_actions"

    let initialState = Initial

    let refundEntityId (id: RefundId) : RefundEntityId =
        entityId $"refund:{RefundId.wireString id}"

    let classifyState =
        function
        | Initial -> stateId "initial"
        | AllocationPending _ -> stateId "allocation-pending"
        | PendingGateway _ -> stateId "pending-gateway"
        | OutcomeUnknown _ -> stateId "outcome-unknown"
        | SettlementPending _ -> stateId "settlement-pending"
        | Succeeded _ -> stateId "succeeded"
        | Failed _ -> stateId "failed"
        | ManualReview _ -> stateId "manual-review"
        | Closed _ -> stateId "closed"

    let private start state event =
        match state, event with
        | Initial, RefundRequested request ->
            not (String.IsNullOrWhiteSpace(RefundOrigin.orderId request.Origin))
            && Money.amount request.Amount > 0m
        | _ -> false

    let private allocationResult state event =
        match state, event with
        | AllocationPending request, AllocationApproved approved -> approved.Request = request
        | AllocationPending request, AllocationDenied(denied, _) -> denied = request
        | _ -> false

    let private gatewayResult state event =
        match state, event with
        | (PendingGateway approved | OutcomeUnknown approved), GatewayRefunded(result, _)
        | (PendingGateway approved | OutcomeUnknown approved), GatewayDeclined(result, _)
        | (PendingGateway approved | OutcomeUnknown approved), GatewayUnknown result -> result = approved
        | _ -> false

    let private settled state event =
        match state, event with
        | SettlementPending(approved, _), AllocationSettled id -> id = approved.Request.AllocationId
        | _ -> false

    let private callback _ =
        function
        | AllocationApproved _
        | AllocationDenied _
        | GatewayRefunded _
        | GatewayDeclined _
        | GatewayUnknown _
        | AllocationSettled _ -> true
        | _ -> false

    let chartResult =
        statechart<RefundState, RefundEvent, RefundAction, RefundActionError> {
            root MachineKey
            classify classifyState

            state "initial" {
                on start (fun _ event ->
                    match event with
                    | RefundRequested request -> [ RequestAllocation request ], AllocationPending request
                    | _ -> [], Initial)

                internalOn callback (fun _ _ -> [])
            }

            state "allocation-pending" {
                on allocationResult (fun state event ->
                    match event with
                    | AllocationApproved approved -> [ CallGatewayRefund approved ], PendingGateway approved
                    | AllocationDenied(request, reason) ->
                        [ NotifyOriginFailed(request, reason) ], Failed(request, reason)
                    | _ -> [], state)

                internalOn callback (fun _ _ -> [])
            }

            state "pending-gateway" {
                on gatewayResult (fun state event ->
                    match state, event with
                    | PendingGateway approved, GatewayRefunded(_, reference) ->
                        [ SettleAllocation(approved, reference) ], SettlementPending(approved, reference)
                    | PendingGateway approved, GatewayDeclined(_, reason) ->
                        [ ReleaseAllocation approved.Request
                          NotifyOriginFailed(approved.Request, reason) ],
                        Failed(approved.Request, reason)
                    | PendingGateway approved, GatewayUnknown _ -> [], OutcomeUnknown approved
                    | _ -> [], state)

                internalOn callback (fun _ _ -> [])
            }

            state "outcome-unknown" {
                on gatewayResult (fun state event ->
                    match state, event with
                    | OutcomeUnknown approved, GatewayRefunded(_, reference) ->
                        [ SettleAllocation(approved, reference) ], SettlementPending(approved, reference)
                    | OutcomeUnknown approved, GatewayDeclined(_, reason) ->
                        [ ReleaseAllocation approved.Request
                          NotifyOriginFailed(approved.Request, reason) ],
                        Failed(approved.Request, reason)
                    | _ -> [], state)

                internalOn callback (fun _ _ -> [])
            }

            state "settlement-pending" {
                on settled (fun state _ ->
                    match state with
                    | SettlementPending(approved, _) ->
                        [ NotifyOriginSucceeded approved.Request ], Succeeded approved.Request
                    | _ -> [], state)

                internalOn callback (fun _ _ -> [])
            }

            state "succeeded" {
                on (fun _ event -> event = CloseRequested) (fun state _ ->
                    match state with
                    | Succeeded request -> [], Closed request
                    | _ -> [], state)

                internalOn callback (fun _ _ -> [])
            }

            state "failed" {
                on (fun _ event -> event = CloseRequested) (fun state _ ->
                    match state with
                    | Failed(request, _) -> [], Closed request
                    | _ -> [], state)

                internalOn callback (fun _ _ -> [])
            }

            state "manual-review" { internalOn callback (fun _ _ -> []) }
            state "closed" { internalOn callback (fun _ _ -> []) }
        }

    let chartValue =
        match chartResult with
        | Ok chart -> chart
        | Error errors -> invalidOp $"refunds chart is invalid: %A{errors}"
