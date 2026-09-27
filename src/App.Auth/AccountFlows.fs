namespace App.Auth

open System
open App.Domain
open ByzantineSystems.Automata.Core

/// <summary>Fields shared by every active (non-<c>Fresh</c>, non-<c>Completed</c>) flow state.
/// <c>Generation</c> counts notification cycles: every resend increments it, and expiry timer
/// events must match it to count. It never resets.</summary>
type ActiveFlow =
    { Kind: FlowKind
      UserId: UserId
      Generation: int
      ResendCount: int }

/// <summary>Identity mutation confirmed committed. <c>CompletedAt</c> is the authority's
/// commit time, not the machine's observation time.</summary>
type CompletedFlow =
    { Kind: FlowKind
      UserId: UserId
      CompletedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type ManualReviewReason =
    | OperatorRequested
    | ReconciliationExhausted

/// <summary>A flow parked for an operator. The reason is a bounded internal code, never raw
/// provider or exception text.</summary>
type ManualReviewFlow =
    { Kind: FlowKind
      UserId: UserId
      Reason: ManualReviewReason }

/// <summary>
/// One account flow's belief. <c>Fresh</c> is the machine's initial state; the start event
/// carries the kind and user reference because the initial state cannot know them. Every case
/// maps to one chart state id through <c>AccountFlow.classify</c>.
///
/// Neither <c>Expired</c> nor <c>DeliveryFailed</c> is terminal: Identity, not this machine,
/// decides whether a token was valid, so every active-ish state accepts a late authoritative
/// <c>CompletionSucceeded</c>.
/// </summary>
type FlowState =
    | Fresh
    | NotificationPending of ActiveFlow
    | AwaitingCompletion of ActiveFlow
    | Completed of CompletedFlow
    | Expired of ActiveFlow
    | DeliveryFailed of ActiveFlow
    | ManualReview of ManualReviewFlow

/// <summary>
/// Events of the <c>flows</c> machine. No raw token, password, email address, or unrestricted
/// error text ever appears here; those live only in restricted relational rows.
///
/// Notification callbacks carry the notification generation they describe. The machine accepts
/// only callbacks for its current generation; stale ones are absorbed as no-ops, so a resend
/// can never be overtaken by the delivery result of a superseded email.
/// </summary>
type FlowEvent =
    | StartRequested of kind: FlowKind * user: UserId
    | NotificationQueued of generation: int
    | NotificationSent of generation: int
    | NotificationSendFailed of generation: int
    | CompletionSucceeded of kind: FlowKind * user: UserId * completedAt: DateTimeOffset
    | ExpiryTimerFired of generation: int * deadline: DateTimeOffset
    | ResendRequested
    | MarkedForManualReview of reason: ManualReviewReason

/// <summary>
/// The one consequential effect: send the flow's notification email. The payload carries only
/// the notification cycle's generation, which the machine alone owns; the handler resolves the
/// restricted relational row by flow id, so no secret or PII can leak into queue messages,
/// receipts, or FSM history.
/// </summary>
type FlowAction = SendNotification of generation: int

[<RequireQualifiedAccess>]
type FlowActionError =
    | InvalidFlowEntityId
    | FlowRequestNotActive
    | FlowUserNotFound
    | IdentityTokenEmpty
    | CallbackEncodingFailed
    | ActionReceiptMismatch

/// <summary>Marks entity ids of the <c>flows</c> machine. Declared beside its module so the
/// two names resolve together.</summary>
type AccountFlow = class end

/// <summary>The non-secret entity id of one account flow. A random value per account
/// operation; never derived from and never containing a secret.</summary>
type FlowId = EntityId<AccountFlow>

[<RequireQualifiedAccess>]
module AccountFlow =

    [<Literal>]
    let MachineKey = "flows"

    [<Literal>]
    let ActionQueue = "flow_actions"

    /// <summary>Upper bound on resends per flow before the next request must supersede it.</summary>
    [<Literal>]
    let MaxResendCount = 3

    let initialState = Fresh

    let classifyState (state: FlowState) =
        match state with
        | Fresh -> stateId "fresh"
        | NotificationPending _ -> stateId "notification-pending"
        | AwaitingCompletion _ -> stateId "awaiting-completion"
        | Completed _ -> stateId "completed"
        | Expired _ -> stateId "expired"
        | DeliveryFailed _ -> stateId "delivery-failed"
        | ManualReview _ -> stateId "manual-review"

    /// <summary>True when a generation-bearing event (notification callback or expiry timer)
    /// describes the flow's current notification cycle. Every other generation is stale and
    /// must resolve to a no-op through <c>internalOn</c>.</summary>
    let private isGenerationCurrent (state: FlowState) (generation: int) =
        match state with
        | NotificationPending flow
        | AwaitingCompletion flow
        | Expired flow
        | DeliveryFailed flow -> flow.Generation = generation
        | Fresh
        | Completed _
        | ManualReview _ -> false

    let private resendAllowed state =
        match state with
        | NotificationPending flow
        | AwaitingCompletion flow
        | DeliveryFailed flow -> flow.ResendCount < MaxResendCount
        | Fresh
        | Expired _
        | Completed _
        | ManualReview _ -> false

    let private startEvent =
        function
        | StartRequested _ -> true
        | _ -> false

    let private resendEvent =
        function
        | ResendRequested -> true
        | _ -> false

    let private completionMatches state event =
        match state, event with
        | Fresh, CompletionSucceeded _ -> true
        | NotificationPending flow, CompletionSucceeded(kind, user, _)
        | AwaitingCompletion flow, CompletionSucceeded(kind, user, _)
        | Expired flow, CompletionSucceeded(kind, user, _)
        | DeliveryFailed flow, CompletionSucceeded(kind, user, _) -> flow.Kind = kind && flow.UserId = user
        | ManualReview flow, CompletionSucceeded(kind, user, _) -> flow.Kind = kind && flow.UserId = user
        | Completed _, _
        | _, _ -> false

    let private manualReviewEvent =
        function
        | MarkedForManualReview _ -> true
        | _ -> false

    let private activeState state =
        match state with
        | NotificationPending flow
        | AwaitingCompletion flow
        | Expired flow
        | DeliveryFailed flow -> Some flow
        | Fresh
        | Completed _
        | ManualReview _ -> None

    let private withActive (state: FlowState) (update: ActiveFlow -> FlowState) =
        activeState state |> Option.map update |> Option.defaultValue state

    let private resendFlow (state: FlowState) : FlowState * FlowAction list =
        activeState state
        |> Option.map (fun flow ->
            NotificationPending
                { flow with
                    Generation = flow.Generation + 1
                    ResendCount = flow.ResendCount + 1 },
            [ SendNotification(flow.Generation + 1) ])
        |> Option.defaultValue (state, [])

    let private completeFlow
        (state: FlowState)
        (kind: FlowKind)
        (user: UserId)
        (completedAt: DateTimeOffset)
        : FlowState =
        match state with
        | Fresh ->
            Completed
                { Kind = kind
                  UserId = user
                  CompletedAt = completedAt }
        | NotificationPending flow
        | AwaitingCompletion flow
        | Expired flow
        | DeliveryFailed flow ->
            Completed
                { Kind = flow.Kind
                  UserId = flow.UserId
                  CompletedAt = completedAt }
        | ManualReview flow ->
            Completed
                { Kind = flow.Kind
                  UserId = flow.UserId
                  CompletedAt = completedAt }
        | Completed _ -> state

    let private reviewFlow (state: FlowState) (reason: ManualReviewReason) : FlowState =
        activeState state
        |> Option.map (fun flow ->
            ManualReview
                { Kind = flow.Kind
                  UserId = flow.UserId
                  Reason = reason })
        |> Option.defaultValue state

    let chartResult =
        statechart<FlowState, FlowEvent, FlowAction, FlowActionError> {
            root MachineKey

            classify classifyState

            state "fresh" {
                on completionMatches (fun state event ->
                    match event with
                    | CompletionSucceeded(kind, user, completedAt) -> [], completeFlow state kind user completedAt
                    | _ -> [], state)

                on (fun _ event -> startEvent event) (fun _ event ->
                    match event with
                    | StartRequested(kind, user) ->
                        [ SendNotification 1 ],
                        NotificationPending
                            { Kind = kind
                              UserId = user
                              Generation = 1
                              ResendCount = 0 }
                    | _ -> [], Fresh)

                // A timer for a flow that never started (start delivery was abandoned) is a
                // benign no-op: the deadline row is cancelled by the scanner's gate rule.
                internalOn (fun _ _ -> true) (fun _ _ -> [])
            }

            state "notification-pending" {
                on completionMatches (fun state event ->
                    match event with
                    | CompletionSucceeded(kind, user, completedAt) -> [], completeFlow state kind user completedAt
                    | _ -> [], state)

                // SMTP acceptance can overtake the queued callback when two relays race; both
                // mean "the current notification left the building" and advance the flow.
                on
                    (fun state event ->
                        match event with
                        | NotificationQueued generation
                        | NotificationSent generation -> isGenerationCurrent state generation
                        | _ -> false)
                    (fun state _ -> [], withActive state AwaitingCompletion)

                on
                    (fun state event ->
                        match event with
                        | NotificationSendFailed generation -> isGenerationCurrent state generation
                        | _ -> false)
                    (fun state _ -> [], withActive state DeliveryFailed)

                on
                    (fun state event ->
                        match event with
                        | ExpiryTimerFired(generation, _) -> isGenerationCurrent state generation
                        | _ -> false)
                    (fun state _ -> [], withActive state Expired)

                on (fun _ event -> manualReviewEvent event) (fun state event ->
                    match event with
                    | MarkedForManualReview reason -> [], reviewFlow state reason
                    | _ -> [], state)

                // Duplicate starts, late notification callbacks, and stale timers must not
                // fail the command.
                internalOn (fun _ _ -> true) (fun _ _ -> [])
            }

            state "awaiting-completion" {
                on completionMatches (fun state event ->
                    match event with
                    | CompletionSucceeded(kind, user, completedAt) -> [], completeFlow state kind user completedAt
                    | _ -> [], state)

                // The queued callback fires before the delivery attempt, so a permanently
                // rejected email still reaches the flow after it started awaiting completion.
                on
                    (fun state event ->
                        match event with
                        | NotificationSendFailed generation -> isGenerationCurrent state generation
                        | _ -> false)
                    (fun state _ -> [], withActive state DeliveryFailed)

                on (fun state event -> resendEvent event && resendAllowed state) (fun state _ ->
                    let next, actions = resendFlow state
                    actions, next)

                on
                    (fun state event ->
                        match event with
                        | ExpiryTimerFired(generation, _) -> isGenerationCurrent state generation
                        | _ -> false)
                    (fun state _ -> [], withActive state Expired)

                on (fun _ event -> manualReviewEvent event) (fun state event ->
                    match event with
                    | MarkedForManualReview reason -> [], reviewFlow state reason
                    | _ -> [], state)

                internalOn (fun _ _ -> true) (fun _ _ -> [])
            }

            state "completed" {
                // Duplicated completion callbacks are idempotent no-ops, and a notification
                // callback racing the completion is absorbed rather than rejected.
                internalOn (fun _ _ -> true) (fun _ _ -> [])
            }

            state "expired" {
                // Late authoritative success after expiry: Identity already committed.
                on completionMatches (fun state event ->
                    match event with
                    | CompletionSucceeded(kind, user, completedAt) -> [], completeFlow state kind user completedAt
                    | _ -> [], state)

                internalOn (fun _ _ -> true) (fun _ _ -> [])
            }

            state "delivery-failed" {
                on completionMatches (fun state event ->
                    match event with
                    | CompletionSucceeded(kind, user, completedAt) -> [], completeFlow state kind user completedAt
                    | _ -> [], state)

                on (fun state event -> resendEvent event && resendAllowed state) (fun state _ ->
                    let next, actions = resendFlow state
                    actions, next)

                internalOn (fun _ _ -> true) (fun _ _ -> [])
            }

            state "manual-review" {
                on completionMatches (fun state event ->
                    match event with
                    | CompletionSucceeded(kind, user, completedAt) -> [], completeFlow state kind user completedAt
                    | _ -> [], state)

                internalOn (fun _ _ -> true) (fun _ _ -> [])
            }
        }

    let chartValue =
        match chartResult with
        | Ok built -> built
        | Error errors -> invalidOp $"flows chart is invalid: %A{errors}"
