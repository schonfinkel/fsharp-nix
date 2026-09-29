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

/// <summary>One immutable request to capture part of an authorization.</summary>
type CaptureRequest =
    { OrderId: string
      CaptureId: CaptureId
      OperationId: PaymentOperationId
      Amount: Money }

/// <summary>A capture confirmed by the gateway.</summary>
type CaptureRecord =
    { Request: CaptureRequest
      ProviderReference: string }

type RefundReservation =
    { Request: RefundRequest
      Status: string }

/// <summary>The authorization and all captures confirmed against it.</summary>
type CapturedPayment =
    { Authorization: AuthorizedPayment
      Captures: CaptureRecord list
      Refunds: RefundReservation list }

/// <summary>A capture whose provider outcome has not yet been settled.</summary>
type PendingCapture =
    { Payment: CapturedPayment
      Request: CaptureRequest }

type PaymentState =
    | Initial
    | AuthorizationPending of PendingAuthorization
    | AuthorizationUnknown of PendingAuthorization
    | Authorized of AuthorizedPayment
    | CapturePending of PendingCapture
    | CaptureUnknown of PendingCapture
    | PartiallyCaptured of CapturedPayment
    | Captured of CapturedPayment
    | RefundAllocationPending of CapturedPayment
    | VoidPending of AuthorizedPayment
    | VoidUnknown of AuthorizedPayment
    | Voided of AuthorizedPayment
    | Declined of reasonCode: ReasonCode
    | CancelledWithoutCharge
    | ManualReview of reasonCode: ReasonCode

type PaymentEvent =
    | AuthorizeRequested of AuthorizationAttempt
    | AuthorizationSucceeded of AuthorizationAttempt * providerReference: string * expiresAt: DateTimeOffset
    | AuthorizationDeclined of AuthorizationAttempt * reasonCode: ReasonCode
    | AuthorizationOutcomeUnknown of AuthorizationAttempt
    | CaptureRequested of CaptureRequest
    | CaptureSucceeded of CaptureRequest * providerReference: string
    | CaptureOutcomeUnknown of CaptureRequest
    | CaptureDeclined of CaptureRequest * reasonCode: ReasonCode
    | RefundAllocationRequested of RefundRequest
    | RefundAllocationSettled of RefundAllocationId
    | RefundAllocationReleased of RefundAllocationId
    | PaymentCancellationRequested of orderId: string * reason: ReasonCode
    | VoidSucceeded of AuthorizedPayment
    | VoidOutcomeUnknown of AuthorizedPayment
    | MarkManualReview of reasonCode: ReasonCode
    /// <summary>The reconciliation scanner asks for another gateway check of a provider call
    /// whose outcome is unknown. Carries the operation id so a check aimed at an older call is
    /// absorbed.</summary>
    | ReconcileRequested of operationId: PaymentOperationId
    /// <summary>The reconciliation scanner ran out of checks for that operation.</summary>
    | ReconciliationExhausted of operationId: PaymentOperationId
    /// <summary>The expiry scanner reports that the authorization's hold has lapsed.</summary>
    | AuthorizationExpired of operationId: PaymentOperationId * expiresAt: DateTimeOffset

type PaymentAction =
    | CallGatewayAuthorize of AuthorizationAttempt
    | QueryGatewayAuthorization of AuthorizationAttempt
    | CallGatewayCapture of AuthorizedPayment * CaptureRequest
    | QueryGatewayCapture of AuthorizedPayment * CaptureRequest
    | CallGatewayVoid of AuthorizedPayment
    | NotifyOrderAuthorized of AuthorizedPayment
    | NotifyOrderDeclined of AuthorizationAttempt * reasonCode: ReasonCode
    | NotifyOrderCancelled of orderId: string
    | NotifyOrderVoided of AuthorizedPayment
    | NotifyOrderCaptured of CaptureRecord
    | NotifyOrderAuthorizationExpired of AuthorizedPayment
    | NotifyRefundApproved of ApprovedRefund
    | NotifyRefundDenied of RefundRequest * reasonCode: ReasonCode
    | NotifyRefundSettled of RefundRequest

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

    /// Bump on every semantic chart change (guards, transitions, codecs), even when the
    /// structure is unchanged; Automata's fingerprint cannot see inside functions.
    [<Literal>]
    let ChartVersion = 1

    let initialState = Initial

    let paymentId (id: Guid) : PaymentId = entityId $"payment:{id:D}"

    let classifyState =
        function
        | Initial -> stateId "initial"
        | AuthorizationPending _ -> stateId "authorization-pending"
        | AuthorizationUnknown _ -> stateId "authorization-unknown"
        | Authorized _ -> stateId "authorized"
        | CapturePending _ -> stateId "capture-pending"
        | CaptureUnknown _ -> stateId "capture-unknown"
        | PartiallyCaptured _ -> stateId "partially-captured"
        | Captured _ -> stateId "captured"
        | RefundAllocationPending _ -> stateId "refund-allocation-pending"
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
        | (VoidPending authorized | VoidUnknown authorized), VoidSucceeded confirmed
        | VoidPending authorized, VoidOutcomeUnknown confirmed ->
            confirmed.Attempt.OperationId = authorized.Attempt.OperationId
        | _ -> false

    /// <summary>The provider call whose outcome an unknown state is waiting for.</summary>
    let private unknownOperation =
        function
        | AuthorizationUnknown pending -> Some pending.Attempt.OperationId
        | CaptureUnknown pending -> Some pending.Request.OperationId
        | VoidUnknown authorized -> Some(PaymentOperationId.voidOf authorized.Attempt.OperationId)
        | _ -> None

    let private reconcile state event =
        match unknownOperation state, event with
        | Some operation, ReconcileRequested requested -> requested = operation
        | _ -> false

    let private reconciliationExhausted state event =
        match unknownOperation state, event with
        | Some operation, ReconciliationExhausted requested -> requested = operation
        | _ -> false

    let private applyReconcile state _ =
        match state with
        | AuthorizationUnknown pending -> [ QueryGatewayAuthorization pending.Attempt ], state
        | CaptureUnknown pending -> [ QueryGatewayCapture(pending.Payment.Authorization, pending.Request) ], state
        // Voids have no query operation; repeating the call under the same operation id is
        // idempotent at the provider and returns the settled result.
        | VoidUnknown authorized -> [ CallGatewayVoid authorized ], state
        | _ -> [], state

    /// <summary>An uncaptured authorization lapses. Only the open states hold uncaptured funds;
    /// a capture in flight is settled (or reconciled) by its own result.</summary>
    let private authorizationExpired state event =
        match state, event with
        | Authorized authorized, AuthorizationExpired(operation, _) -> operation = authorized.Attempt.OperationId
        | PartiallyCaptured payment, AuthorizationExpired(operation, _) ->
            operation = payment.Authorization.Attempt.OperationId
        | _ -> false

    let private applyAuthorizationExpired state _ =
        let reason = ReasonCode.ofLiteral "authorization-expired"

        match state with
        | Authorized authorized -> [ NotifyOrderAuthorizationExpired authorized ], ManualReview reason
        | PartiallyCaptured payment -> [ NotifyOrderAuthorizationExpired payment.Authorization ], ManualReview reason
        | _ -> [], state

    let private applyExhausted state _ =
        [], ManualReview(ReasonCode.ofLiteral "gateway-outcome-unknown")

    let private captureTotal (payment: CapturedPayment) =
        payment.Captures
        |> List.fold
            (fun total capture -> Money.add total capture.Request.Amount)
            (Money.zero (Money.currencyCode payment.Authorization.Attempt.Amount)
             |> Result.defaultWith invalidOp)

    let authorizedTotal (payment: CapturedPayment) = payment.Authorization.Attempt.Amount

    let capturedTotal (payment: CapturedPayment) = captureTotal payment

    let refundedTotal (payment: CapturedPayment) =
        let zero =
            Money.zero (Money.currencyCode payment.Authorization.Attempt.Amount)
            |> Result.defaultWith invalidOp

        payment.Refunds
        |> List.filter (fun refund -> refund.Status = "settled")
        |> List.fold (fun sum refund -> sum + refund.Request.Amount) zero

    let refundableTotal (payment: CapturedPayment) =
        let zero =
            Money.zero (Money.currencyCode payment.Authorization.Attempt.Amount)
            |> Result.defaultWith invalidOp

        let pending =
            payment.Refunds
            |> List.filter (fun refund -> refund.Status = "pending")
            |> List.fold (fun sum refund -> sum + refund.Request.Amount) zero

        RefundAllocation.available
            { Captured = captureTotal payment
              Refunded = refundedTotal payment
              Pending = pending }

    let private captureAllowed payment request =
        request.OrderId = payment.Authorization.Attempt.OrderId
        && Money.currencyCode request.Amount = Money.currencyCode payment.Authorization.Attempt.Amount
        && Money.amount request.Amount > 0m
        && request.OperationId <> payment.Authorization.Attempt.OperationId
        && payment.Captures
           |> List.forall (fun capture ->
               capture.Request.CaptureId <> request.CaptureId
               && capture.Request.OperationId <> request.OperationId)
        && Money.amount (Money.add (captureTotal payment) request.Amount)
           <= Money.amount payment.Authorization.Attempt.Amount

    let private captureStart state event =
        match state, event with
        | Authorized authorized, CaptureRequested request ->
            captureAllowed
                { Authorization = authorized
                  Captures = []
                  Refunds = [] }
                request
        | PartiallyCaptured payment, CaptureRequested request -> captureAllowed payment request
        | RefundAllocationPending payment, CaptureRequested request -> captureAllowed payment request
        | _ -> false

    let private captureSettled state event =
        let matches pending request = pending.Request = request

        match state, event with
        | (CapturePending pending | CaptureUnknown pending), CaptureSucceeded(request, _)
        | (CapturePending pending | CaptureUnknown pending), CaptureDeclined(request, _)
        | (CapturePending pending | CaptureUnknown pending), CaptureOutcomeUnknown request -> matches pending request
        | _ -> false

    let private readyState payment =
        if payment.Refunds |> List.exists (fun refund -> refund.Status = "pending") then
            RefundAllocationPending payment
        elif List.isEmpty payment.Captures then
            Authorized payment.Authorization
        elif captureTotal payment = payment.Authorization.Attempt.Amount then
            Captured payment
        else
            PartiallyCaptured payment

    let private settleCapture queryOnUnknown pending event =
        match event with
        | CaptureSucceeded(request, reference) ->
            let capture =
                { Request = request
                  ProviderReference = reference }

            let payment =
                { pending.Payment with
                    Captures = pending.Payment.Captures @ [ capture ] }

            [ NotifyOrderCaptured capture ], readyState payment
        | CaptureDeclined _ -> [], readyState pending.Payment
        | CaptureOutcomeUnknown _ ->
            (if queryOnUnknown then
                 [ QueryGatewayCapture(pending.Payment.Authorization, pending.Request) ]
             else
                 []),
            CaptureUnknown pending
        | _ -> [], CapturePending pending

    let private cancelAfterCapture state event =
        match state, event with
        | (CapturePending _ | CaptureUnknown _ | PartiallyCaptured _ | Captured _ | RefundAllocationPending _),
          PaymentCancellationRequested _ -> true
        | _ -> false

    let private refundPayment =
        function
        | PartiallyCaptured payment
        | Captured payment
        | RefundAllocationPending payment -> Some payment
        | _ -> None

    let private refundRequest state event =
        match refundPayment state, event with
        | Some _, RefundAllocationRequested _ -> true
        | _ -> false

    let private refundSettlement state event =
        match refundPayment state, event with
        | Some payment, (RefundAllocationSettled id | RefundAllocationReleased id) ->
            payment.Refunds
            |> List.exists (fun row -> row.Request.AllocationId = id && row.Status = "pending")
        | _ -> false

    let private applyRefundRequest state event =
        match refundPayment state, event with
        | Some payment, RefundAllocationRequested request ->
            let existing =
                payment.Refunds
                |> List.tryFind (fun row -> row.Request.AllocationId = request.AllocationId)

            let expectedOrder = payment.Authorization.Attempt.OrderId

            match existing with
            | Some row when row.Request = request && row.Status = "pending" ->
                [ NotifyRefundApproved
                      { Request = request
                        PaymentReference = payment.Authorization.ProviderReference } ],
                state
            | Some _ -> [ NotifyRefundDenied(request, (ReasonCode.ofLiteral "allocation-conflict")) ], state
            | None ->
                match refundableTotal payment with
                | Error _ -> [ NotifyRefundDenied(request, (ReasonCode.ofLiteral "balance-invalid")) ], state
                | Ok remaining when
                    RefundOrigin.orderId request.Origin <> expectedOrder
                    || Money.currencyCode request.Amount <> Money.currencyCode remaining
                    || Money.amount request.Amount <= 0m
                    || request.Amount > remaining
                    ->
                    [ NotifyRefundDenied(request, (ReasonCode.ofLiteral "insufficient-captured-balance")) ], state
                | Ok _ ->
                    let updated =
                        { payment with
                            Refunds =
                                payment.Refunds
                                @ [ { Request = request
                                      Status = "pending" } ] }

                    [ NotifyRefundApproved
                          { Request = request
                            PaymentReference = payment.Authorization.ProviderReference } ],
                    RefundAllocationPending updated
        | _ -> [], state

    let private applyRefundSettlement state event =
        match refundPayment state, event with
        | Some payment, (RefundAllocationSettled id | RefundAllocationReleased id) ->
            let row = payment.Refunds |> List.find (fun row -> row.Request.AllocationId = id)

            let status =
                match event with
                | RefundAllocationSettled _ -> "settled"
                | _ -> "released"

            let updated =
                { payment with
                    Refunds =
                        payment.Refunds
                        |> List.map (fun entry ->
                            if entry.Request.AllocationId = id then
                                { entry with Status = status }
                            else
                                entry) }

            let actions =
                if status = "settled" then
                    [ NotifyRefundSettled row.Request ]
                else
                    []

            actions, readyState updated
        | _ -> [], state

    let private absorb _ =
        function
        | AuthorizationSucceeded _
        | AuthorizationDeclined _
        | AuthorizationOutcomeUnknown _
        | CaptureSucceeded _
        | CaptureOutcomeUnknown _
        | CaptureDeclined _
        | VoidSucceeded _
        | VoidOutcomeUnknown _
        | ReconcileRequested _
        | ReconciliationExhausted _
        | AuthorizationExpired _ -> true
        | RefundAllocationSettled _
        | RefundAllocationReleased _ -> true
        | AuthorizeRequested _
        | CaptureRequested _
        | RefundAllocationRequested _
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

                on reconcile applyReconcile
                on reconciliationExhausted applyExhausted

                on cancelInFlight (fun state _ ->
                    match state with
                    | AuthorizationUnknown pending ->
                        [ QueryGatewayAuthorization pending.Attempt ],
                        AuthorizationUnknown { pending with CancelRequested = true }
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "authorized" {
                on captureStart (fun state event ->
                    match state, event with
                    | Authorized authorized, CaptureRequested request ->
                        [ CallGatewayCapture(authorized, request) ],
                        CapturePending
                            { Payment =
                                { Authorization = authorized
                                  Captures = []
                                  Refunds = [] }
                              Request = request }
                    | _ -> [], state)

                on cancelInFlight (fun state _ ->
                    match state with
                    | Authorized authorized -> [ CallGatewayVoid authorized ], VoidPending authorized
                    | _ -> [], state)

                on authorizationExpired applyAuthorizationExpired

                internalOn absorb (fun _ _ -> [])
            }

            state "capture-pending" {
                on captureSettled (fun state event ->
                    match state with
                    | CapturePending pending -> settleCapture true pending event
                    | _ -> [], state)

                on cancelAfterCapture (fun _ _ -> [], ManualReview(ReasonCode.ofLiteral "cancellation-during-capture"))
                internalOn absorb (fun _ _ -> [])
            }

            state "capture-unknown" {
                on captureSettled (fun state event ->
                    match state with
                    | CaptureUnknown pending -> settleCapture false pending event
                    | _ -> [], state)

                on cancelAfterCapture (fun _ _ ->
                    [], ManualReview(ReasonCode.ofLiteral "cancellation-during-unknown-capture"))

                on reconcile applyReconcile
                on reconciliationExhausted applyExhausted

                internalOn absorb (fun _ _ -> [])
            }

            state "partially-captured" {
                on captureStart (fun state event ->
                    match state, event with
                    | PartiallyCaptured payment, CaptureRequested request ->
                        [ CallGatewayCapture(payment.Authorization, request) ],
                        CapturePending { Payment = payment; Request = request }
                    | _ -> [], state)

                on cancelAfterCapture (fun _ _ -> [], ManualReview(ReasonCode.ofLiteral "void-requested-after-capture"))
                on refundRequest applyRefundRequest
                on refundSettlement applyRefundSettlement
                on authorizationExpired applyAuthorizationExpired
                internalOn absorb (fun _ _ -> [])
            }

            state "captured" {
                on cancelAfterCapture (fun _ _ -> [], ManualReview(ReasonCode.ofLiteral "void-requested-after-capture"))
                on refundRequest applyRefundRequest
                on refundSettlement applyRefundSettlement
                internalOn absorb (fun _ _ -> [])
            }

            state "refund-allocation-pending" {
                on refundRequest applyRefundRequest
                on refundSettlement applyRefundSettlement

                on captureStart (fun state event ->
                    match state, event with
                    | RefundAllocationPending payment, CaptureRequested request ->
                        [ CallGatewayCapture(payment.Authorization, request) ],
                        CapturePending { Payment = payment; Request = request }
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

            state "void-unknown" {
                on voidSettled (fun state event ->
                    match state, event with
                    | VoidUnknown authorized, VoidSucceeded _ -> [ NotifyOrderVoided authorized ], Voided authorized
                    | _ -> [], state)

                on reconcile applyReconcile
                on reconciliationExhausted applyExhausted
                internalOn absorb (fun _ _ -> [])
            }

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
