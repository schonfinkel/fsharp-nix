namespace App.Database

open System
open System.Text.Json
open System.Text.Json.Serialization
open App.Auth
open App.Domain
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging

/// <summary>
/// Wire records for the <c>flows</c> machine. These plain records are the only persisted
/// shapes: Automata's default F#-union serialization is explicitly not used, because these
/// bytes are the long-term schema. Every tag ends in its wire version; decoding peeks the
/// <c>tag</c> first, then deserializes that tag's record through the validating domain
/// constructors and rejects unknown tags.
///
/// Version discipline: a semantic change to any record bumps its <c>-v1</c> suffix, and the
/// decoder keeps accepting every retained version. The chart version must be bumped in the
/// same change even when chart structure is unchanged, because the fingerprint cannot see
/// classifier, guard, action, or transition-function edits.
/// </summary>
module AccountFlowWire =

    type TaggedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string }

    type FreshStateDto =
        { [<JsonPropertyName("tag")>]
          Tag: string }

    type ActiveStateDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("kind")>]
          Kind: string
          [<JsonPropertyName("userId")>]
          UserId: string
          [<JsonPropertyName("generation")>]
          Generation: int
          [<JsonPropertyName("resendCount")>]
          ResendCount: int }

    type CompletedStateDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("kind")>]
          Kind: string
          [<JsonPropertyName("userId")>]
          UserId: string
          [<JsonPropertyName("completedAt")>]
          CompletedAt: int64 }

    type ManualReviewStateDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("kind")>]
          Kind: string
          [<JsonPropertyName("userId")>]
          UserId: string
          [<JsonPropertyName("reason")>]
          Reason: string }

    type StartRequestedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("kind")>]
          Kind: string
          [<JsonPropertyName("userId")>]
          UserId: string }

    type CompletionSucceededDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("kind")>]
          Kind: string
          [<JsonPropertyName("userId")>]
          UserId: string
          [<JsonPropertyName("completedAt")>]
          CompletedAt: int64 }

    type ExpiryTimerFiredDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("generation")>]
          Generation: int
          [<JsonPropertyName("deadline")>]
          Deadline: int64 }

    type MarkedForManualReviewDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("reason")>]
          Reason: string }

    type SendNotificationDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("generation")>]
          Generation: int }

    type NotificationCallbackDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("generation")>]
          Generation: int }

[<RequireQualifiedAccess>]
module AccountFlowCodec =

    module Tags =

        [<Literal>]
        let FreshState = "fresh-v1"

        [<Literal>]
        let NotificationPendingState = "notification-pending-v1"

        [<Literal>]
        let AwaitingCompletionState = "awaiting-completion-v1"

        [<Literal>]
        let CompletedState = "completed-v1"

        [<Literal>]
        let ExpiredState = "expired-v1"

        [<Literal>]
        let DeliveryFailedState = "delivery-failed-v1"

        [<Literal>]
        let ManualReviewState = "manual-review-v1"

        [<Literal>]
        let StartRequestedEvent = "start-requested-v1"

        [<Literal>]
        let NotificationQueuedEvent = "notification-queued-v2"

        [<Literal>]
        let NotificationSentEvent = "notification-sent-v2"

        [<Literal>]
        let NotificationSendFailedEvent = "notification-send-failed-v2"

        [<Literal>]
        let CompletionSucceededEvent = "completion-succeeded-v1"

        [<Literal>]
        let ExpiryTimerFiredEvent = "expiry-timer-fired-v1"

        [<Literal>]
        let ResendRequestedEvent = "resend-requested-v1"

        [<Literal>]
        let MarkedForManualReviewEvent = "marked-for-manual-review-v1"

        [<Literal>]
        let SendNotificationAction = "send-notification-v2"

        [<Literal>]
        let InvalidFlowEntityIdError = "invalid-flow-entity-id-v1"

        [<Literal>]
        let FlowRequestNotActiveError = "flow-request-not-active-v1"

        [<Literal>]
        let FlowUserNotFoundError = "flow-user-not-found-v1"

        [<Literal>]
        let IdentityTokenEmptyError = "identity-token-empty-v1"

        [<Literal>]
        let CallbackEncodingFailedError = "callback-encoding-failed-v1"

        [<Literal>]
        let ActionReceiptMismatchError = "action-receipt-mismatch-v1"

    let private manualReviewReasonName =
        function
        | ManualReviewReason.OperatorRequested -> "operator-requested"
        | ManualReviewReason.ReconciliationExhausted -> "reconciliation-exhausted"

    let private tryManualReviewReason =
        function
        | "operator-requested" -> Some ManualReviewReason.OperatorRequested
        | "reconciliation-exhausted" -> Some ManualReviewReason.ReconciliationExhausted
        | _ -> None

    let private options =
        let configured =
            JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

        configured.RespectRequiredConstructorParameters <- true
        configured

    let private encodeValue (typeName: string) (value: obj) : Result<string, CodecError> =
        try
            Ok(JsonSerializer.Serialize(value, options))
        with error ->
            Error(CodecError.EncodeError(typeName, error))

    let private decodeExact<'T> (typeName: string) (json: string) : Result<'T, CodecError> =
        try
            let value = JsonSerializer.Deserialize<'T>(json, options)

            if isNull (box value) then
                Error(CodecError.DecodeError(typeName, FormatException "JSON deserialized to null."))
            else
                Ok value
        with error ->
            Error(CodecError.DecodeError(typeName, error))

    let private readTag (typeName: string) (json: string) : Result<string, CodecError> =
        try
            use document = JsonDocument.Parse json

            match document.RootElement.TryGetProperty "tag" with
            | false, _ -> Error(CodecError.DecodeError(typeName, FormatException "Missing 'tag'."))
            | true, element ->
                match element.GetString() with
                | null -> Error(CodecError.DecodeError(typeName, FormatException "Missing 'tag'."))
                | tag -> Ok tag
        with error ->
            Error(CodecError.DecodeError(typeName, error))

    let private decodeFailure (typeName: string) (message: string) : CodecError =
        CodecError.DecodeError(typeName, FormatException message)

    let private unknownTag (typeName: string) (tag: string) : CodecError =
        decodeFailure typeName $"Unknown tag '{tag}'."

    let private kindOf (typeName: string) (wire: string) : Result<FlowKind, CodecError> =
        match FlowKind.tryParseWireName wire with
        | Some kind -> Ok kind
        | None -> Error(unknownTag typeName wire)

    let private userIdOf (typeName: string) (wire: string) : Result<UserId, CodecError> =
        UserId.tryParse wire
        |> Result.mapError (fun message -> decodeFailure typeName message)

    let private tryEpoch (typeName: string) (field: string) (milliseconds: int64) : Result<DateTimeOffset, CodecError> =
        try
            Ok(DateTimeOffset.FromUnixTimeMilliseconds milliseconds)
        with error ->
            Error(decodeFailure typeName $"Invalid '{field}' epoch.")

    let private activeOfDto (typeName: string) (dto: AccountFlowWire.ActiveStateDto) : Result<ActiveFlow, CodecError> =
        if dto.Generation < 1 then
            Error(decodeFailure typeName "'generation' must be at least 1.")
        elif dto.ResendCount < 0 then
            Error(decodeFailure typeName "'resendCount' must not be negative.")
        elif dto.ResendCount > AccountFlow.MaxResendCount then
            Error(decodeFailure typeName "'resendCount' exceeds the supported maximum.")
        elif dto.Generation <> dto.ResendCount + 1 then
            Error(decodeFailure typeName "'generation' must equal 'resendCount' plus one.")
        else
            kindOf typeName dto.Kind
            |> Result.bind (fun kind ->
                userIdOf typeName dto.UserId
                |> Result.map (fun user ->
                    let flow: ActiveFlow =
                        { Kind = kind
                          UserId = user
                          Generation = dto.Generation
                          ResendCount = dto.ResendCount }

                    flow))

    let private activeDto (tag: string) (flow: ActiveFlow) : AccountFlowWire.ActiveStateDto =
        { Tag = tag
          Kind = FlowKind.wireName flow.Kind
          UserId = UserId.wireString flow.UserId
          Generation = flow.Generation
          ResendCount = flow.ResendCount }

    let private stateTypeName = "FlowState"

    let private encodeState (state: FlowState) : Result<string, CodecError> =
        let dto =
            match state with
            | Fresh ->
                let fresh: AccountFlowWire.FreshStateDto = { Tag = Tags.FreshState }
                box fresh
            | NotificationPending flow -> box (activeDto Tags.NotificationPendingState flow)
            | AwaitingCompletion flow -> box (activeDto Tags.AwaitingCompletionState flow)
            | Expired flow -> box (activeDto Tags.ExpiredState flow)
            | DeliveryFailed flow -> box (activeDto Tags.DeliveryFailedState flow)
            | Completed flow ->
                let completed: AccountFlowWire.CompletedStateDto =
                    { Tag = Tags.CompletedState
                      Kind = FlowKind.wireName flow.Kind
                      UserId = UserId.wireString flow.UserId
                      CompletedAt = flow.CompletedAt.ToUnixTimeMilliseconds() }

                box completed
            | ManualReview flow ->
                let review: AccountFlowWire.ManualReviewStateDto =
                    { Tag = Tags.ManualReviewState
                      Kind = FlowKind.wireName flow.Kind
                      UserId = UserId.wireString flow.UserId
                      Reason = manualReviewReasonName flow.Reason }

                box review

        encodeValue stateTypeName dto

    let private decodeState (json: string) : Result<FlowState, CodecError> =
        readTag stateTypeName json
        |> Result.bind (fun tag ->
            let activeState (wrap: ActiveFlow -> FlowState) : Result<FlowState, CodecError> =
                decodeExact<AccountFlowWire.ActiveStateDto> stateTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag stateTypeName dto.Tag)
                    else
                        activeOfDto stateTypeName dto |> Result.map wrap)

            match tag with
            | t when t = Tags.FreshState ->
                decodeExact<AccountFlowWire.FreshStateDto> stateTypeName json
                |> Result.map (fun _ -> Fresh)
            | t when t = Tags.NotificationPendingState -> activeState NotificationPending
            | t when t = Tags.AwaitingCompletionState -> activeState AwaitingCompletion
            | t when t = Tags.ExpiredState -> activeState Expired
            | t when t = Tags.DeliveryFailedState -> activeState DeliveryFailed
            | t when t = Tags.CompletedState ->
                decodeExact<AccountFlowWire.CompletedStateDto> stateTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag stateTypeName dto.Tag)
                    else
                        kindOf stateTypeName dto.Kind
                        |> Result.bind (fun kind ->
                            userIdOf stateTypeName dto.UserId
                            |> Result.bind (fun user ->
                                tryEpoch stateTypeName "completedAt" dto.CompletedAt
                                |> Result.map (fun completedAt ->
                                    let completed: CompletedFlow =
                                        { Kind = kind
                                          UserId = user
                                          CompletedAt = completedAt }

                                    Completed completed))))
            | t when t = Tags.ManualReviewState ->
                decodeExact<AccountFlowWire.ManualReviewStateDto> stateTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag stateTypeName dto.Tag)
                    else
                        kindOf stateTypeName dto.Kind
                        |> Result.bind (fun kind ->
                            userIdOf stateTypeName dto.UserId
                            |> Result.bind (fun user ->
                                match tryManualReviewReason dto.Reason with
                                | None -> Error(decodeFailure stateTypeName "'reason' was unknown.")
                                | Some reason ->
                                    Ok(
                                        ManualReview
                                            { Kind = kind
                                              UserId = user
                                              Reason = reason }
                                    ))))
            | _ -> Error(unknownTag stateTypeName tag))

    let private eventTypeName = "FlowEvent"

    let private encodeEvent (event: FlowEvent) : Result<string, CodecError> =
        let dto =
            match event with
            | StartRequested(kind, user) ->
                let start: AccountFlowWire.StartRequestedDto =
                    { Tag = Tags.StartRequestedEvent
                      Kind = FlowKind.wireName kind
                      UserId = UserId.wireString user }

                box start
            | NotificationQueued generation ->
                let callback: AccountFlowWire.NotificationCallbackDto =
                    { Tag = Tags.NotificationQueuedEvent
                      Generation = generation }

                box callback
            | NotificationSent generation ->
                let callback: AccountFlowWire.NotificationCallbackDto =
                    { Tag = Tags.NotificationSentEvent
                      Generation = generation }

                box callback
            | NotificationSendFailed generation ->
                let callback: AccountFlowWire.NotificationCallbackDto =
                    { Tag = Tags.NotificationSendFailedEvent
                      Generation = generation }

                box callback
            | CompletionSucceeded(kind, user, completedAt) ->
                let completion: AccountFlowWire.CompletionSucceededDto =
                    { Tag = Tags.CompletionSucceededEvent
                      Kind = FlowKind.wireName kind
                      UserId = UserId.wireString user
                      CompletedAt = completedAt.ToUnixTimeMilliseconds() }

                box completion
            | ExpiryTimerFired(generation, deadline) ->
                let timer: AccountFlowWire.ExpiryTimerFiredDto =
                    { Tag = Tags.ExpiryTimerFiredEvent
                      Generation = generation
                      Deadline = deadline.ToUnixTimeMilliseconds() }

                box timer
            | ResendRequested ->
                let marker: AccountFlowWire.TaggedDto = { Tag = Tags.ResendRequestedEvent }
                box marker
            | MarkedForManualReview reason ->
                let review: AccountFlowWire.MarkedForManualReviewDto =
                    { Tag = Tags.MarkedForManualReviewEvent
                      Reason = manualReviewReasonName reason }

                box review

        encodeValue eventTypeName dto

    let private decodeEvent (json: string) : Result<FlowEvent, CodecError> =
        readTag eventTypeName json
        |> Result.bind (fun tag ->
            let decodeCallback (wrap: int -> FlowEvent) : Result<FlowEvent, CodecError> =
                decodeExact<AccountFlowWire.NotificationCallbackDto> eventTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag eventTypeName dto.Tag)
                    elif dto.Generation < 1 then
                        Error(decodeFailure eventTypeName "'generation' must be at least 1.")
                    else
                        Ok(wrap dto.Generation))

            match tag with
            | t when t = Tags.NotificationQueuedEvent -> decodeCallback NotificationQueued
            | t when t = Tags.NotificationSentEvent -> decodeCallback NotificationSent
            | t when t = Tags.NotificationSendFailedEvent -> decodeCallback NotificationSendFailed
            | t when t = Tags.ResendRequestedEvent ->
                decodeExact<AccountFlowWire.TaggedDto> eventTypeName json
                |> Result.map (fun _ -> ResendRequested)
            | t when t = Tags.StartRequestedEvent ->
                decodeExact<AccountFlowWire.StartRequestedDto> eventTypeName json
                |> Result.bind (fun dto ->
                    kindOf eventTypeName dto.Kind
                    |> Result.bind (fun kind ->
                        userIdOf eventTypeName dto.UserId
                        |> Result.map (fun user -> StartRequested(kind, user))))
            | t when t = Tags.CompletionSucceededEvent ->
                decodeExact<AccountFlowWire.CompletionSucceededDto> eventTypeName json
                |> Result.bind (fun dto ->
                    kindOf eventTypeName dto.Kind
                    |> Result.bind (fun kind ->
                        userIdOf eventTypeName dto.UserId
                        |> Result.bind (fun user ->
                            tryEpoch eventTypeName "completedAt" dto.CompletedAt
                            |> Result.map (fun completedAt -> CompletionSucceeded(kind, user, completedAt)))))
            | t when t = Tags.ExpiryTimerFiredEvent ->
                decodeExact<AccountFlowWire.ExpiryTimerFiredDto> eventTypeName json
                |> Result.bind (fun dto ->
                    if dto.Generation < 1 then
                        Error(decodeFailure eventTypeName "'generation' must be at least 1.")
                    else
                        tryEpoch eventTypeName "deadline" dto.Deadline
                        |> Result.map (fun deadline -> ExpiryTimerFired(dto.Generation, deadline)))
            | t when t = Tags.MarkedForManualReviewEvent ->
                decodeExact<AccountFlowWire.MarkedForManualReviewDto> eventTypeName json
                |> Result.bind (fun dto ->
                    match tryManualReviewReason dto.Reason with
                    | Some reason -> Ok(MarkedForManualReview reason)
                    | None -> Error(decodeFailure eventTypeName "'reason' was unknown."))
            | _ -> Error(unknownTag eventTypeName tag))

    let private actionTypeName = "FlowAction"

    let private encodeAction (action: FlowAction) : Result<string, CodecError> =
        match action with
        | SendNotification generation ->
            let dto: AccountFlowWire.SendNotificationDto =
                { Tag = Tags.SendNotificationAction
                  Generation = generation }

            encodeValue actionTypeName dto

    let private decodeAction (json: string) : Result<FlowAction, CodecError> =
        readTag actionTypeName json
        |> Result.bind (fun tag ->
            if tag <> Tags.SendNotificationAction then
                Error(unknownTag actionTypeName tag)
            else
                decodeExact<AccountFlowWire.SendNotificationDto> actionTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag actionTypeName dto.Tag)
                    elif dto.Generation < 1 then
                        Error(decodeFailure actionTypeName "'generation' must be at least 1.")
                    else
                        Ok(SendNotification dto.Generation)))

    let private errorTypeName = "FlowEffectError"

    let private errorTag =
        function
        | FlowActionError.InvalidFlowEntityId -> Tags.InvalidFlowEntityIdError
        | FlowActionError.FlowRequestNotActive -> Tags.FlowRequestNotActiveError
        | FlowActionError.FlowUserNotFound -> Tags.FlowUserNotFoundError
        | FlowActionError.IdentityTokenEmpty -> Tags.IdentityTokenEmptyError
        | FlowActionError.CallbackEncodingFailed -> Tags.CallbackEncodingFailedError
        | FlowActionError.ActionReceiptMismatch -> Tags.ActionReceiptMismatchError

    let private encodeError (reason: FlowActionError) : Result<string, CodecError> =
        let dto: AccountFlowWire.TaggedDto = { Tag = errorTag reason }
        encodeValue errorTypeName dto

    let private decodeError (json: string) : Result<FlowActionError, CodecError> =
        decodeExact<AccountFlowWire.TaggedDto> errorTypeName json
        |> Result.bind (fun dto ->
            match dto.Tag with
            | Tags.InvalidFlowEntityIdError -> Ok FlowActionError.InvalidFlowEntityId
            | Tags.FlowRequestNotActiveError -> Ok FlowActionError.FlowRequestNotActive
            | Tags.FlowUserNotFoundError -> Ok FlowActionError.FlowUserNotFound
            | Tags.IdentityTokenEmptyError -> Ok FlowActionError.IdentityTokenEmpty
            | Tags.CallbackEncodingFailedError -> Ok FlowActionError.CallbackEncodingFailed
            | Tags.ActionReceiptMismatchError -> Ok FlowActionError.ActionReceiptMismatch
            | tag -> Error(unknownTag errorTypeName tag))

    let state: Codec<FlowState> = Codec.create encodeState decodeState

    let event: Codec<FlowEvent> = Codec.create encodeEvent decodeEvent

    let action: Codec<FlowAction> = Codec.create encodeAction decodeAction

    let error: Codec<FlowActionError> = Codec.create encodeError decodeError

    /// <summary>Store options with every codec overridden: these JSON bytes are the durable
    /// schema, so none of Automata's defaults are allowed to decide it.</summary>
    let storeOptions
        (context: PostgresContext)
        : MachineStoreOptions<FlowId, FlowState, FlowEvent, FlowAction, FlowActionError> =
        { MachineStoreOptions.forEntityId<AccountFlow, FlowState, FlowEvent, FlowAction, FlowActionError>
              context
              AccountFlow.ActionQueue with
            StateCodec = state
            EventCodec = event
            ActionCodec = action
            ErrorCodec = error }

    let workerStore (context: PostgresContext) =
        storeOptions context |> PostgresMachineStore

    let clientStore (context: PostgresContext) =
        { storeOptions context with
            Listener = ListenerConnection.Off }
        |> PostgresMachineStore

    let private build (log: ILogger) storeArg =
        machine<FlowId, FlowState, FlowEvent, FlowAction, FlowActionError> (machineId AccountFlow.MachineKey) {
            chart AccountFlow.chartValue
            chartVersion 5
            initialState AccountFlow.initialState
            store storeArg
            logger log
        }

    let buildWorker (log: ILogger) (context: PostgresContext) =
        build log (workerStore context :> IMachineStore<FlowId, FlowState, FlowEvent, FlowAction, FlowActionError>)

    let buildClient (log: ILogger) (context: PostgresContext) =
        build log (clientStore context :> IMachineStore<FlowId, FlowState, FlowEvent, FlowAction, FlowActionError>)
