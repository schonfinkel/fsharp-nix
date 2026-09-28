namespace App.Database

open System
open System.Text.Json
open System.Text.Json.Serialization
open App.Domain
open App.Payments
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging

module PaymentWire =
    [<CLIMutable>]
    type WireDto =
        { Tag: string
          OperationId: string
          OrderId: string
          Amount: string
          Currency: string
          Method: string
          ProviderReference: string
          ExpiresAt: int64
          Reason: string
          CancelRequested: bool }

[<RequireQualifiedAccess>]
module PaymentCodec =
    let private options =
        JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

    do options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let private codecError name message =
        CodecError.DecodeError(name, FormatException message)

    let private empty tag : PaymentWire.WireDto =
        { Tag = tag
          OperationId = ""
          OrderId = ""
          Amount = ""
          Currency = ""
          Method = ""
          ProviderReference = ""
          ExpiresAt = 0L
          Reason = ""
          CancelRequested = false }

    let private encode name (dto: PaymentWire.WireDto) =
        try
            Ok(JsonSerializer.Serialize(dto, options))
        with ex ->
            Error(CodecError.EncodeError(name, ex))

    let private decode name (json: string) : Result<PaymentWire.WireDto, CodecError> =
        try
            let dto = JsonSerializer.Deserialize<PaymentWire.WireDto>(json, options)

            if isNull (box dto) then
                Error(codecError name "JSON decoded to null.")
            elif String.IsNullOrWhiteSpace dto.Tag then
                Error(codecError name "Missing tag.")
            else
                Ok dto
        with ex ->
            Error(CodecError.DecodeError(name, ex))

    let private attemptDto tag (attempt: AuthorizationAttempt) : PaymentWire.WireDto =
        { (empty tag) with
            OperationId = PaymentOperationId.value attempt.OperationId
            OrderId = attempt.OrderId
            Amount = Money.wireAmount attempt.Amount
            Currency = Money.currencyCode attempt.Amount
            Method = PaymentMethodReference.value attempt.Method }

    let private attemptOfDto name (dto: PaymentWire.WireDto) : Result<AuthorizationAttempt, CodecError> =
        PaymentOperationId.tryParse dto.OperationId
        |> Result.mapError (fun m -> codecError name m)
        |> Result.bind (fun operationId ->
            NonEmptyString.create 100 dto.OrderId
            |> Result.mapError (fun m -> codecError name m)
            |> Result.bind (fun orderId ->
                PaymentMethodReference.tryParse dto.Method
                |> Result.mapError (fun m -> codecError name m)
                |> Result.bind (fun method ->
                    Money.tryOfWire dto.Amount dto.Currency
                    |> Result.mapError (fun m -> codecError name m)
                    |> Result.map (fun amount ->
                        { OperationId = operationId
                          OrderId = NonEmptyString.value orderId
                          Amount = amount
                          Method = method }))))

    let private authorizedDto tag (authorized: AuthorizedPayment) : PaymentWire.WireDto =
        { (attemptDto tag authorized.Attempt) with
            ProviderReference = authorized.ProviderReference
            ExpiresAt = authorized.ExpiresAt.ToUnixTimeMilliseconds() }

    let private authorizedOfDto name (dto: PaymentWire.WireDto) : Result<AuthorizedPayment, CodecError> =
        attemptOfDto name dto
        |> Result.bind (fun attempt ->
            NonEmptyString.create 200 dto.ProviderReference
            |> Result.mapError (fun m -> codecError name m)
            |> Result.bind (fun reference ->
                try
                    Ok
                        { Attempt = attempt
                          ProviderReference = NonEmptyString.value reference
                          ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds dto.ExpiresAt }
                with _ ->
                    Error(codecError name "Invalid authorization expiry.")))

    let private pendingDto tag (pending: PendingAuthorization) : PaymentWire.WireDto =
        { (attemptDto tag pending.Attempt) with
            CancelRequested = pending.CancelRequested }

    let private pendingOfDto name (dto: PaymentWire.WireDto) : Result<PendingAuthorization, CodecError> =
        attemptOfDto name dto
        |> Result.map (fun attempt ->
            { Attempt = attempt
              CancelRequested = dto.CancelRequested })

    let state: Codec<PaymentState> =
        Codec.create
            (fun state ->
                let dto =
                    match state with
                    | Initial -> empty "initial-v1"
                    | AuthorizationPending pending -> pendingDto "authorization-pending-v1" pending
                    | AuthorizationUnknown pending -> pendingDto "authorization-unknown-v1" pending
                    | Authorized authorized -> authorizedDto "authorized-v1" authorized
                    | VoidPending authorized -> authorizedDto "void-pending-v1" authorized
                    | VoidUnknown authorized -> authorizedDto "void-unknown-v1" authorized
                    | Voided authorized -> authorizedDto "voided-v1" authorized
                    | Declined reason ->
                        { empty "declined-v1" with
                            Reason = reason }
                    | CancelledWithoutCharge -> empty "cancelled-without-charge-v1"
                    | ManualReview reason ->
                        { empty "manual-review-v1" with
                            Reason = reason }

                encode "PaymentState" dto)
            (fun json ->
                decode "PaymentState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial-v1" -> Ok Initial
                    | "cancelled-without-charge-v1" -> Ok CancelledWithoutCharge
                    | "declined-v1" when not (String.IsNullOrWhiteSpace dto.Reason) -> Ok(Declined dto.Reason)
                    | "manual-review-v1" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        Ok(ManualReview dto.Reason)
                    | "authorization-pending-v1" -> pendingOfDto "PaymentState" dto |> Result.map AuthorizationPending
                    | "authorization-unknown-v1" -> pendingOfDto "PaymentState" dto |> Result.map AuthorizationUnknown
                    | "authorized-v1" -> authorizedOfDto "PaymentState" dto |> Result.map Authorized
                    | "void-pending-v1" -> authorizedOfDto "PaymentState" dto |> Result.map VoidPending
                    | "void-unknown-v1" -> authorizedOfDto "PaymentState" dto |> Result.map VoidUnknown
                    | "voided-v1" -> authorizedOfDto "PaymentState" dto |> Result.map Voided
                    | tag -> Error(codecError "PaymentState" $"Unknown or invalid tag '{tag}'.")))

    let event: Codec<PaymentEvent> =
        Codec.create
            (fun event ->
                let dto =
                    match event with
                    | AuthorizeRequested attempt -> attemptDto "authorize-requested-v1" attempt
                    | AuthorizationSucceeded(attempt, reference, expiresAt) ->
                        { (attemptDto "authorization-succeeded-v1" attempt) with
                            ProviderReference = reference
                            ExpiresAt = expiresAt.ToUnixTimeMilliseconds() }
                    | AuthorizationDeclined(attempt, reason) ->
                        { (attemptDto "authorization-declined-v1" attempt) with
                            Reason = reason }
                    | AuthorizationOutcomeUnknown attempt -> attemptDto "authorization-outcome-unknown-v1" attempt
                    | PaymentCancellationRequested(orderId, reason) ->
                        { empty "cancel-requested-v1" with
                            OrderId = orderId
                            Reason = reason }
                    | VoidSucceeded authorized -> authorizedDto "void-succeeded-v1" authorized
                    | VoidOutcomeUnknown authorized -> authorizedDto "void-outcome-unknown-v1" authorized
                    | MarkManualReview reason ->
                        { empty "mark-manual-review-v1" with
                            Reason = reason }

                encode "PaymentEvent" dto)
            (fun json ->
                decode "PaymentEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "authorize-requested-v1" -> attemptOfDto "PaymentEvent" dto |> Result.map AuthorizeRequested
                    | "authorization-succeeded-v1" ->
                        attemptOfDto "PaymentEvent" dto
                        |> Result.bind (fun attempt ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                            |> Result.bind (fun reference ->
                                try
                                    AuthorizationSucceeded(
                                        attempt,
                                        NonEmptyString.value reference,
                                        DateTimeOffset.FromUnixTimeMilliseconds dto.ExpiresAt
                                    )
                                    |> Ok
                                with _ ->
                                    Error(codecError "PaymentEvent" "Invalid authorization expiry.")))
                    | "authorization-declined-v1" ->
                        attemptOfDto "PaymentEvent" dto
                        |> Result.bind (fun attempt ->
                            NonEmptyString.create 200 dto.Reason
                            |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                            |> Result.map (fun reason -> AuthorizationDeclined(attempt, NonEmptyString.value reason)))
                    | "authorization-outcome-unknown-v1" ->
                        attemptOfDto "PaymentEvent" dto |> Result.map AuthorizationOutcomeUnknown
                    | "cancel-requested-v1" ->
                        NonEmptyString.create 100 dto.OrderId
                        |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                        |> Result.bind (fun orderId ->
                            NonEmptyString.create 200 dto.Reason
                            |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                            |> Result.map (fun reason ->
                                PaymentCancellationRequested(
                                    NonEmptyString.value orderId,
                                    NonEmptyString.value reason
                                )))
                    | "void-succeeded-v1" ->
                        authorizedOfDto "PaymentEvent" dto
                        |> Result.map (fun authorized -> VoidSucceeded authorized)
                    | "void-outcome-unknown-v1" ->
                        authorizedOfDto "PaymentEvent" dto
                        |> Result.map (fun authorized -> VoidOutcomeUnknown authorized)
                    | "mark-manual-review-v1" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        Ok(MarkManualReview dto.Reason)
                    | tag -> Error(codecError "PaymentEvent" $"Unknown tag '{tag}'.")))

    let action: Codec<PaymentAction> =
        Codec.create
            (fun action ->
                let dto =
                    match action with
                    | CallGatewayAuthorize attempt -> attemptDto "call-gateway-authorize-v1" attempt
                    | QueryGatewayAuthorization attempt -> attemptDto "query-gateway-authorization-v1" attempt
                    | CallGatewayVoid authorized -> authorizedDto "call-gateway-void-v1" authorized
                    | NotifyOrderAuthorized authorized -> authorizedDto "notify-order-authorized-v1" authorized
                    | NotifyOrderDeclined(attempt, reason) ->
                        { (attemptDto "notify-order-declined-v1" attempt) with
                            Reason = reason }
                    | NotifyOrderCancelled orderId ->
                        { empty "notify-order-cancelled-v1" with
                            OrderId = orderId }
                    | NotifyOrderVoided authorized -> authorizedDto "notify-order-voided-v1" authorized

                encode "PaymentAction" dto)
            (fun json ->
                decode "PaymentAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "call-gateway-authorize-v1" ->
                        attemptOfDto "PaymentAction" dto |> Result.map CallGatewayAuthorize
                    | "query-gateway-authorization-v1" ->
                        attemptOfDto "PaymentAction" dto |> Result.map QueryGatewayAuthorization
                    | "call-gateway-void-v1" -> authorizedOfDto "PaymentAction" dto |> Result.map CallGatewayVoid
                    | "notify-order-authorized-v1" ->
                        authorizedOfDto "PaymentAction" dto |> Result.map NotifyOrderAuthorized
                    | "notify-order-declined-v1" ->
                        attemptOfDto "PaymentAction" dto
                        |> Result.bind (fun attempt ->
                            NonEmptyString.create 200 dto.Reason
                            |> Result.mapError (fun m -> codecError "PaymentAction" m)
                            |> Result.map (fun reason -> NotifyOrderDeclined(attempt, NonEmptyString.value reason)))
                    | "notify-order-cancelled-v1" ->
                        NonEmptyString.create 100 dto.OrderId
                        |> Result.mapError (fun m -> codecError "PaymentAction" m)
                        |> Result.map (fun orderId -> NotifyOrderCancelled(NonEmptyString.value orderId))
                    | "notify-order-voided-v1" -> authorizedOfDto "PaymentAction" dto |> Result.map NotifyOrderVoided
                    | tag -> Error(codecError "PaymentAction" $"Unknown tag '{tag}'.")))

    let error: Codec<PaymentActionError> =
        Codec.create
            (fun errorValue ->
                let tag =
                    match errorValue with
                    | PaymentActionError.InvalidPaymentEntityId -> "invalid-payment-entity-id-v1"
                    | PaymentActionError.CallbackEncodingFailed -> "callback-encoding-failed-v1"
                    | PaymentActionError.ActionReceiptMismatch -> "action-receipt-mismatch-v1"
                    | PaymentActionError.OperationConflict -> "operation-conflict-v1"
                    | PaymentActionError.InvalidAction -> "invalid-action-v1"

                encode "PaymentActionError" (empty tag))
            (fun json ->
                decode "PaymentActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "invalid-payment-entity-id-v1" -> Ok PaymentActionError.InvalidPaymentEntityId
                    | "callback-encoding-failed-v1" -> Ok PaymentActionError.CallbackEncodingFailed
                    | "action-receipt-mismatch-v1" -> Ok PaymentActionError.ActionReceiptMismatch
                    | "operation-conflict-v1" -> Ok PaymentActionError.OperationConflict
                    | "invalid-action-v1" -> Ok PaymentActionError.InvalidAction
                    | tag -> Error(codecError "PaymentActionError" $"Unknown tag '{tag}'.")))

    let storeOptions
        (context: PostgresContext)
        : MachineStoreOptions<PaymentId, PaymentState, PaymentEvent, PaymentAction, PaymentActionError> =
        { MachineStoreOptions.forEntityId<Payment, PaymentState, PaymentEvent, PaymentAction, PaymentActionError>
              context
              Payments.ActionQueue with
            StateCodec = state
            EventCodec = event
            ActionCodec = action
            ErrorCodec = error }

    let workerStore context =
        storeOptions context |> PostgresMachineStore

    let clientStore context =
        { storeOptions context with
            Listener = ListenerConnection.Off }
        |> PostgresMachineStore

    let private build (log: ILogger) storeArg =
        machine<PaymentId, PaymentState, PaymentEvent, PaymentAction, PaymentActionError> (
            machineId Payments.MachineKey
        ) {
            chart Payments.chartValue
            chartVersion 1
            initialState Payments.initialState
            store storeArg
            logger log
        }

    let buildWorker log context =
        build
            log
            (workerStore context
            :> IMachineStore<PaymentId, PaymentState, PaymentEvent, PaymentAction, PaymentActionError>)

    let buildClient log context =
        build
            log
            (clientStore context
            :> IMachineStore<PaymentId, PaymentState, PaymentEvent, PaymentAction, PaymentActionError>)
