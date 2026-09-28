namespace App.Payments

open System
open App.Domain
open ByzantineSystems.Automata.Core
open NodaMoney

/// <summary>One authorization request handed from an order to its payment entity.</summary>
type AuthorizationAttempt =
    { OperationId: PaymentOperationId
      OrderId: string
      Amount: Money
      Method: PaymentMethodReference }

/// <summary>An authorization the gateway has confirmed.</summary>
type AuthorizedPayment =
    { Attempt: AuthorizationAttempt
      ProviderReference: string
      ExpiresAt: DateTimeOffset }

/// <summary>An authorization in flight at the gateway; <c>CancelRequested</c> records that the
/// order already cancelled, so the terminal result must unwind instead of notify.</summary>
type PendingAuthorization =
    { Attempt: AuthorizationAttempt
      CancelRequested: bool }

type PaymentState =
    | Initial
    | AuthorizationPending of PendingAuthorization
    | AuthorizationUnknown of PendingAuthorization
    | Authorized of AuthorizedPayment
    | VoidPending of AuthorizedPayment
    | VoidUnknown of AuthorizedPayment
    | Voided of AuthorizedPayment
    | Declined of reasonCode: string
    | CancelledWithoutCharge
    | ManualReview of reasonCode: string

type PaymentEvent =
    | AuthorizeRequested of AuthorizationAttempt
    | AuthorizationSucceeded of AuthorizationAttempt * providerReference: string * expiresAt: DateTimeOffset
    | AuthorizationDeclined of AuthorizationAttempt * reasonCode: string
    | AuthorizationOutcomeUnknown of AuthorizationAttempt
    | PaymentCancellationRequested of orderId: string * reason: string
    | VoidSucceeded of AuthorizedPayment
    | VoidOutcomeUnknown of AuthorizedPayment
    | MarkManualReview of reasonCode: string

type PaymentAction =
    | CallGatewayAuthorize of AuthorizationAttempt
    | QueryGatewayAuthorization of AuthorizationAttempt
    | CallGatewayVoid of AuthorizedPayment
    | NotifyOrderAuthorized of AuthorizedPayment
    | NotifyOrderDeclined of AuthorizationAttempt * reasonCode: string
    | NotifyOrderCancelled of orderId: string
    | NotifyOrderVoided of AuthorizedPayment

[<RequireQualifiedAccess>]
type PaymentActionError =
    | InvalidPaymentEntityId
    | CallbackEncodingFailed
    | ActionReceiptMismatch
    | OperationConflict
    | InvalidAction

type Payment = class end
type PaymentId = EntityId<Payment>

[<RequireQualifiedAccess>]
module Payments =
    [<Literal>]
    let MachineKey = "payments"

    [<Literal>]
    let ActionQueue = "payment_actions"

    let initialState = Initial

    let paymentId (id: Guid) : PaymentId = entityId $"payment:{id:D}"

    let classifyState =
        function
        | Initial -> stateId "initial"
        | AuthorizationPending _ -> stateId "authorization-pending"
        | AuthorizationUnknown _ -> stateId "authorization-unknown"
        | Authorized _ -> stateId "authorized"
        | VoidPending _ -> stateId "void-pending"
        | VoidUnknown _ -> stateId "void-unknown"
        | Voided _ -> stateId "voided"
        | Declined _ -> stateId "declined"
        | CancelledWithoutCharge -> stateId "cancelled-without-charge"
        | ManualReview _ -> stateId "manual-review"

    let private authorizeStart _ event =
        match event with
        | AuthorizeRequested _ -> true
        | _ -> false

    let private cancelBeforeAuthorization _ event =
        match event with
        | PaymentCancellationRequested _ -> true
        | _ -> false

    let private authorizeRetry _ event =
        match event with
        | AuthorizeRequested _ -> true
        | _ -> false

    let private authorizeSettled state event =
        match state, event with
        | (AuthorizationPending pending | AuthorizationUnknown pending), AuthorizationSucceeded(attempt, _, _)
        | (AuthorizationPending pending | AuthorizationUnknown pending), AuthorizationDeclined(attempt, _)
        | (AuthorizationPending pending | AuthorizationUnknown pending), AuthorizationOutcomeUnknown attempt ->
            attempt.OperationId = pending.Attempt.OperationId
        | _ -> false

    let private cancelInFlight state event =
        match state, event with
        | AuthorizationPending pending, PaymentCancellationRequested _ -> not pending.CancelRequested
        | AuthorizationUnknown pending, PaymentCancellationRequested _ -> not pending.CancelRequested
        | Authorized _, PaymentCancellationRequested _ -> true
        | Declined _, PaymentCancellationRequested _ -> true
        | _ -> false

    let private voidSettled state event =
        match state, event with
        | VoidPending authorized, VoidSucceeded confirmed
        | VoidPending authorized, VoidOutcomeUnknown confirmed ->
            confirmed.Attempt.OperationId = authorized.Attempt.OperationId
        | _ -> false

    let private absorb _ =
        function
        | AuthorizationSucceeded _
        | AuthorizationDeclined _
        | AuthorizationOutcomeUnknown _
        | VoidSucceeded _
        | VoidOutcomeUnknown _ -> true
        | AuthorizeRequested _
        | PaymentCancellationRequested _
        | MarkManualReview _ -> false

    let private pendingActions (pending: PendingAuthorization) authorized actionsWhenOpen =
        if pending.CancelRequested then
            [ CallGatewayVoid authorized ]
        else
            actionsWhenOpen

    let private declineTransition (pending: PendingAuthorization) reason =
        if pending.CancelRequested then
            [ NotifyOrderCancelled pending.Attempt.OrderId ], CancelledWithoutCharge
        else
            [ NotifyOrderDeclined(pending.Attempt, reason) ], Declined reason

    let chartResult =
        statechart<PaymentState, PaymentEvent, PaymentAction, PaymentActionError> {
            root MachineKey
            classify classifyState

            state "initial" {
                on authorizeStart (fun _ event ->
                    match event with
                    | AuthorizeRequested attempt ->
                        [ CallGatewayAuthorize attempt ],
                        AuthorizationPending
                            { Attempt = attempt
                              CancelRequested = false }
                    | _ -> [], Initial)

                on cancelBeforeAuthorization (fun _ event ->
                    match event with
                    | PaymentCancellationRequested(orderId, _) ->
                        [ NotifyOrderCancelled orderId ], CancelledWithoutCharge
                    | _ -> [], Initial)

                internalOn absorb (fun _ _ -> [])
            }

            state "authorization-pending" {
                on authorizeSettled (fun state event ->
                    match state, event with
                    | AuthorizationPending pending, AuthorizationSucceeded(attempt, reference, expiresAt) ->
                        let authorized =
                            { Attempt = attempt
                              ProviderReference = reference
                              ExpiresAt = expiresAt }

                        pendingActions pending authorized [ NotifyOrderAuthorized authorized ],
                        (if pending.CancelRequested then
                             VoidPending authorized
                         else
                             Authorized authorized)
                    | AuthorizationPending pending, AuthorizationDeclined(_, reason) ->
                        let actions, next = declineTransition pending reason
                        actions, next
                    | AuthorizationPending pending, AuthorizationOutcomeUnknown _ ->
                        (if pending.CancelRequested then
                             [ QueryGatewayAuthorization pending.Attempt ]
                         else
                             []),
                        AuthorizationUnknown pending
                    | _ -> [], state)

                on cancelInFlight (fun state _ ->
                    match state with
                    | AuthorizationPending pending -> [], AuthorizationPending { pending with CancelRequested = true }
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "authorization-unknown" {
                on authorizeSettled (fun state event ->
                    match state, event with
                    | AuthorizationUnknown pending, AuthorizationSucceeded(attempt, reference, expiresAt) ->
                        let authorized =
                            { Attempt = attempt
                              ProviderReference = reference
                              ExpiresAt = expiresAt }

                        pendingActions pending authorized [ NotifyOrderAuthorized authorized ],
                        (if pending.CancelRequested then
                             VoidPending authorized
                         else
                             Authorized authorized)
                    | AuthorizationUnknown pending, AuthorizationDeclined(_, reason) ->
                        let actions, next = declineTransition pending reason
                        actions, next
                    | _ -> [], state)

                on cancelInFlight (fun state _ ->
                    match state with
                    | AuthorizationUnknown pending ->
                        [ QueryGatewayAuthorization pending.Attempt ],
                        AuthorizationUnknown { pending with CancelRequested = true }
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "authorized" {
                on cancelInFlight (fun state _ ->
                    match state with
                    | Authorized authorized -> [ CallGatewayVoid authorized ], VoidPending authorized
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "void-pending" {
                on voidSettled (fun state event ->
                    match state, event with
                    | VoidPending authorized, VoidSucceeded _ -> [ NotifyOrderVoided authorized ], Voided authorized
                    | VoidPending authorized, VoidOutcomeUnknown _ -> [], VoidUnknown authorized
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "void-unknown" { internalOn absorb (fun _ _ -> []) }
            state "voided" { internalOn absorb (fun _ _ -> []) }

            state "declined" {
                on authorizeRetry (fun _ event ->
                    match event with
                    | AuthorizeRequested attempt ->
                        [ CallGatewayAuthorize attempt ],
                        AuthorizationPending
                            { Attempt = attempt
                              CancelRequested = false }
                    | _ -> [], Initial)

                on cancelBeforeAuthorization (fun _ event ->
                    match event with
                    | PaymentCancellationRequested(orderId, _) ->
                        [ NotifyOrderCancelled orderId ], CancelledWithoutCharge
                    | _ -> [], Initial)

                internalOn absorb (fun _ _ -> [])
            }

            state "cancelled-without-charge" { internalOn absorb (fun _ _ -> []) }
            state "manual-review" { internalOn absorb (fun _ _ -> []) }
        }

    let chartValue =
        match chartResult with
        | Ok chart -> chart
        | Error errors -> invalidOp $"payments chart is invalid: %A{errors}"
