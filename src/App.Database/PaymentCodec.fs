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
    type CaptureDto =
        { CaptureId: string
          OperationId: string
          OrderId: string
          Amount: string
          Currency: string
          ProviderReference: string }

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
          CancelRequested: bool
          CaptureId: string
          CaptureOperationId: string
          CaptureOrderId: string
          CaptureAmount: string
          CaptureCurrency: string
          Captures: CaptureDto array }

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
          CancelRequested = false
          CaptureId = ""
          CaptureOperationId = ""
          CaptureOrderId = ""
          CaptureAmount = ""
          CaptureCurrency = ""
          Captures = [||] }

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

    let private captureRequestDto tag (request: CaptureRequest) : PaymentWire.WireDto =
        { (empty tag) with
            CaptureId = CaptureId.wireString request.CaptureId
            OperationId = PaymentOperationId.value request.OperationId
            OrderId = request.OrderId
            Amount = Money.wireAmount request.Amount
            Currency = Money.currencyCode request.Amount }

    let private captureRequestOfDto name (dto: PaymentWire.WireDto) : Result<CaptureRequest, CodecError> =
        CaptureId.tryParse dto.CaptureId
        |> Result.mapError (fun m -> codecError name m)
        |> Result.bind (fun captureId ->
            PaymentOperationId.tryParse dto.OperationId
            |> Result.mapError (fun m -> codecError name m)
            |> Result.bind (fun operationId ->
                NonEmptyString.create 100 dto.OrderId
                |> Result.mapError (fun m -> codecError name m)
                |> Result.bind (fun orderId ->
                    Money.tryOfWire dto.Amount dto.Currency
                    |> Result.mapError (fun m -> codecError name m)
                    |> Result.map (fun amount ->
                        { OrderId = NonEmptyString.value orderId
                          CaptureId = captureId
                          OperationId = operationId
                          Amount = amount }))))

    let private captureDto (capture: CaptureRecord) : PaymentWire.CaptureDto =
        { CaptureId = CaptureId.wireString capture.Request.CaptureId
          OperationId = PaymentOperationId.value capture.Request.OperationId
          OrderId = capture.Request.OrderId
          Amount = Money.wireAmount capture.Request.Amount
          Currency = Money.currencyCode capture.Request.Amount
          ProviderReference = capture.ProviderReference }

    let private captureOfDto name (dto: PaymentWire.CaptureDto) : Result<CaptureRecord, CodecError> =
        CaptureId.tryParse dto.CaptureId
        |> Result.mapError (fun m -> codecError name m)
        |> Result.bind (fun captureId ->
            PaymentOperationId.tryParse dto.OperationId
            |> Result.mapError (fun m -> codecError name m)
            |> Result.bind (fun operationId ->
                NonEmptyString.create 100 dto.OrderId
                |> Result.mapError (fun m -> codecError name m)
                |> Result.bind (fun orderId ->
                    Money.tryOfWire dto.Amount dto.Currency
                    |> Result.mapError (fun m -> codecError name m)
                    |> Result.bind (fun amount ->
                        NonEmptyString.create 200 dto.ProviderReference
                        |> Result.mapError (fun m -> codecError name m)
                        |> Result.map (fun reference ->
                            { Request =
                                { OrderId = NonEmptyString.value orderId
                                  CaptureId = captureId
                                  OperationId = operationId
                                  Amount = amount }
                              ProviderReference = NonEmptyString.value reference })))))

    let private capturedDto tag (payment: CapturedPayment) =
        { (authorizedDto tag payment.Authorization) with
            Captures = payment.Captures |> List.map captureDto |> List.toArray }

    let private capturedOfDto name (dto: PaymentWire.WireDto) =
        authorizedOfDto name dto
        |> Result.bind (fun authorization ->
            let captures = if isNull dto.Captures then [||] else dto.Captures

            captures
            |> Array.fold
                (fun result item ->
                    result
                    |> Result.bind (fun decoded ->
                        captureOfDto name item |> Result.map (fun capture -> capture :: decoded)))
                (Ok [])
            |> Result.bind (fun captures ->
                let captures = List.rev captures
                let orderId = authorization.Attempt.OrderId
                let currency = Money.currencyCode authorization.Attempt.Amount
                let captureIds = captures |> List.map (fun capture -> capture.Request.CaptureId)
                let operationIds = captures |> List.map (fun capture -> capture.Request.OperationId)

                let valid =
                    captures
                    |> List.forall (fun capture ->
                        capture.Request.OrderId = orderId
                        && Money.currencyCode capture.Request.Amount = currency
                        && Money.amount capture.Request.Amount > 0m
                        && capture.Request.OperationId <> authorization.Attempt.OperationId)
                    && Set.count (Set.ofList captureIds) = List.length captureIds
                    && Set.count (Set.ofList operationIds) = List.length operationIds
                    && (captures |> List.sumBy (fun capture -> Money.amount capture.Request.Amount))
                       <= Money.amount authorization.Attempt.Amount

                if valid then
                    Ok
                        { Authorization = authorization
                          Captures = captures }
                else
                    Error(codecError name "Invalid capture ledger.")))

    let private pendingCaptureDto tag (pending: PendingCapture) =
        { (capturedDto tag pending.Payment) with
            CaptureId = CaptureId.wireString pending.Request.CaptureId
            CaptureOperationId = PaymentOperationId.value pending.Request.OperationId
            CaptureOrderId = pending.Request.OrderId
            CaptureAmount = Money.wireAmount pending.Request.Amount
            CaptureCurrency = Money.currencyCode pending.Request.Amount }

    let private nestedCaptureRequestOfDto name (dto: PaymentWire.WireDto) =
        captureRequestOfDto
            name
            { dto with
                OperationId = dto.CaptureOperationId
                OrderId = dto.CaptureOrderId
                Amount = dto.CaptureAmount
                Currency = dto.CaptureCurrency }

    let private pendingCaptureOfDto name dto =
        capturedOfDto name dto
        |> Result.bind (fun payment ->
            nestedCaptureRequestOfDto name dto
            |> Result.bind (fun request ->
                let valid =
                    request.OrderId = payment.Authorization.Attempt.OrderId
                    && Money.currencyCode request.Amount = Money.currencyCode payment.Authorization.Attempt.Amount
                    && Money.amount request.Amount > 0m
                    && request.OperationId <> payment.Authorization.Attempt.OperationId
                    && payment.Captures
                       |> List.forall (fun capture ->
                           capture.Request.CaptureId <> request.CaptureId
                           && capture.Request.OperationId <> request.OperationId)
                    && (payment.Captures
                        |> List.sumBy (fun capture -> Money.amount capture.Request.Amount))
                       + Money.amount request.Amount
                       <= Money.amount payment.Authorization.Attempt.Amount

                if valid then
                    Ok { Payment = payment; Request = request }
                else
                    Error(codecError name "Invalid pending capture.")))

    let private captureActionDto tag authorized request =
        { (authorizedDto tag authorized) with
            CaptureId = CaptureId.wireString request.CaptureId
            CaptureOperationId = PaymentOperationId.value request.OperationId
            CaptureOrderId = request.OrderId
            CaptureAmount = Money.wireAmount request.Amount
            CaptureCurrency = Money.currencyCode request.Amount }

    let private captureActionOfDto name dto =
        authorizedOfDto name dto
        |> Result.bind (fun authorized ->
            nestedCaptureRequestOfDto name dto
            |> Result.map (fun request -> authorized, request))

    let state: Codec<PaymentState> =
        Codec.create
            (fun state ->
                let dto =
                    match state with
                    | Initial -> empty "initial-v2"
                    | AuthorizationPending pending -> pendingDto "authorization-pending-v2" pending
                    | AuthorizationUnknown pending -> pendingDto "authorization-unknown-v2" pending
                    | Authorized authorized -> authorizedDto "authorized-v2" authorized
                    | CapturePending pending -> pendingCaptureDto "capture-pending-v2" pending
                    | CaptureUnknown pending -> pendingCaptureDto "capture-unknown-v2" pending
                    | PartiallyCaptured payment -> capturedDto "partially-captured-v2" payment
                    | Captured payment -> capturedDto "captured-v2" payment
                    | VoidPending authorized -> authorizedDto "void-pending-v2" authorized
                    | VoidUnknown authorized -> authorizedDto "void-unknown-v2" authorized
                    | Voided authorized -> authorizedDto "voided-v2" authorized
                    | Declined reason ->
                        { empty "declined-v2" with
                            Reason = reason }
                    | CancelledWithoutCharge -> empty "cancelled-without-charge-v2"
                    | ManualReview reason ->
                        { empty "manual-review-v2" with
                            Reason = reason }

                encode "PaymentState" dto)
            (fun json ->
                decode "PaymentState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial-v1"
                    | "initial-v2" -> Ok Initial
                    | "cancelled-without-charge-v1"
                    | "cancelled-without-charge-v2" -> Ok CancelledWithoutCharge
                    | "declined-v1"
                    | "declined-v2" when not (String.IsNullOrWhiteSpace dto.Reason) -> Ok(Declined dto.Reason)
                    | "manual-review-v1"
                    | "manual-review-v2" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        Ok(ManualReview dto.Reason)
                    | "authorization-pending-v1"
                    | "authorization-pending-v2" -> pendingOfDto "PaymentState" dto |> Result.map AuthorizationPending
                    | "authorization-unknown-v1"
                    | "authorization-unknown-v2" -> pendingOfDto "PaymentState" dto |> Result.map AuthorizationUnknown
                    | "authorized-v1"
                    | "authorized-v2" -> authorizedOfDto "PaymentState" dto |> Result.map Authorized
                    | "capture-pending-v2" -> pendingCaptureOfDto "PaymentState" dto |> Result.map CapturePending
                    | "capture-unknown-v2" -> pendingCaptureOfDto "PaymentState" dto |> Result.map CaptureUnknown
                    | "partially-captured-v2" ->
                        capturedOfDto "PaymentState" dto
                        |> Result.bind (fun payment ->
                            let total =
                                payment.Captures
                                |> List.sumBy (fun capture -> Money.amount capture.Request.Amount)

                            if total > 0m && total < Money.amount payment.Authorization.Attempt.Amount then
                                Ok(PartiallyCaptured payment)
                            else
                                Error(codecError "PaymentState" "Invalid partially captured total."))
                    | "captured-v2" ->
                        capturedOfDto "PaymentState" dto
                        |> Result.bind (fun payment ->
                            let total =
                                payment.Captures
                                |> List.sumBy (fun capture -> Money.amount capture.Request.Amount)

                            if total = Money.amount payment.Authorization.Attempt.Amount then
                                Ok(Captured payment)
                            else
                                Error(codecError "PaymentState" "Invalid captured total."))
                    | "void-pending-v1"
                    | "void-pending-v2" -> authorizedOfDto "PaymentState" dto |> Result.map VoidPending
                    | "void-unknown-v1"
                    | "void-unknown-v2" -> authorizedOfDto "PaymentState" dto |> Result.map VoidUnknown
                    | "voided-v1"
                    | "voided-v2" -> authorizedOfDto "PaymentState" dto |> Result.map Voided
                    | tag -> Error(codecError "PaymentState" $"Unknown or invalid tag '{tag}'.")))

    let event: Codec<PaymentEvent> =
        Codec.create
            (fun event ->
                let dto =
                    match event with
                    | AuthorizeRequested attempt -> attemptDto "authorize-requested-v2" attempt
                    | AuthorizationSucceeded(attempt, reference, expiresAt) ->
                        { (attemptDto "authorization-succeeded-v2" attempt) with
                            ProviderReference = reference
                            ExpiresAt = expiresAt.ToUnixTimeMilliseconds() }
                    | AuthorizationDeclined(attempt, reason) ->
                        { (attemptDto "authorization-declined-v2" attempt) with
                            Reason = reason }
                    | AuthorizationOutcomeUnknown attempt -> attemptDto "authorization-outcome-unknown-v2" attempt
                    | CaptureRequested request -> captureRequestDto "capture-requested-v2" request
                    | CaptureSucceeded(request, reference) ->
                        { (captureRequestDto "capture-succeeded-v2" request) with
                            ProviderReference = reference }
                    | CaptureOutcomeUnknown request -> captureRequestDto "capture-outcome-unknown-v2" request
                    | CaptureDeclined(request, reason) ->
                        { (captureRequestDto "capture-declined-v2" request) with
                            Reason = reason }
                    | PaymentCancellationRequested(orderId, reason) ->
                        { empty "cancel-requested-v2" with
                            OrderId = orderId
                            Reason = reason }
                    | VoidSucceeded authorized -> authorizedDto "void-succeeded-v2" authorized
                    | VoidOutcomeUnknown authorized -> authorizedDto "void-outcome-unknown-v2" authorized
                    | MarkManualReview reason ->
                        { empty "mark-manual-review-v2" with
                            Reason = reason }

                encode "PaymentEvent" dto)
            (fun json ->
                decode "PaymentEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "authorize-requested-v1"
                    | "authorize-requested-v2" -> attemptOfDto "PaymentEvent" dto |> Result.map AuthorizeRequested
                    | "authorization-succeeded-v1"
                    | "authorization-succeeded-v2" ->
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
                    | "authorization-declined-v1"
                    | "authorization-declined-v2" ->
                        attemptOfDto "PaymentEvent" dto
                        |> Result.bind (fun attempt ->
                            NonEmptyString.create 200 dto.Reason
                            |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                            |> Result.map (fun reason -> AuthorizationDeclined(attempt, NonEmptyString.value reason)))
                    | "authorization-outcome-unknown-v1"
                    | "authorization-outcome-unknown-v2" ->
                        attemptOfDto "PaymentEvent" dto |> Result.map AuthorizationOutcomeUnknown
                    | "capture-requested-v2" -> captureRequestOfDto "PaymentEvent" dto |> Result.map CaptureRequested
                    | "capture-succeeded-v2" ->
                        captureRequestOfDto "PaymentEvent" dto
                        |> Result.bind (fun request ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                            |> Result.map (fun reference ->
                                CaptureSucceeded(request, NonEmptyString.value reference)))
                    | "capture-outcome-unknown-v2" ->
                        captureRequestOfDto "PaymentEvent" dto |> Result.map CaptureOutcomeUnknown
                    | "capture-declined-v2" ->
                        captureRequestOfDto "PaymentEvent" dto
                        |> Result.bind (fun request ->
                            NonEmptyString.create 200 dto.Reason
                            |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                            |> Result.map (fun reason -> CaptureDeclined(request, NonEmptyString.value reason)))
                    | "cancel-requested-v1"
                    | "cancel-requested-v2" ->
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
                    | "void-succeeded-v1"
                    | "void-succeeded-v2" ->
                        authorizedOfDto "PaymentEvent" dto
                        |> Result.map (fun authorized -> VoidSucceeded authorized)
                    | "void-outcome-unknown-v1"
                    | "void-outcome-unknown-v2" ->
                        authorizedOfDto "PaymentEvent" dto
                        |> Result.map (fun authorized -> VoidOutcomeUnknown authorized)
                    | "mark-manual-review-v1"
                    | "mark-manual-review-v2" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        Ok(MarkManualReview dto.Reason)
                    | tag -> Error(codecError "PaymentEvent" $"Unknown tag '{tag}'.")))

    let action: Codec<PaymentAction> =
        Codec.create
            (fun action ->
                let dto =
                    match action with
                    | CallGatewayAuthorize attempt -> attemptDto "call-gateway-authorize-v2" attempt
                    | QueryGatewayAuthorization attempt -> attemptDto "query-gateway-authorization-v2" attempt
                    | CallGatewayCapture(authorized, request) ->
                        captureActionDto "call-gateway-capture-v2" authorized request
                    | QueryGatewayCapture(authorized, request) ->
                        captureActionDto "query-gateway-capture-v2" authorized request
                    | CallGatewayVoid authorized -> authorizedDto "call-gateway-void-v2" authorized
                    | NotifyOrderAuthorized authorized -> authorizedDto "notify-order-authorized-v2" authorized
                    | NotifyOrderDeclined(attempt, reason) ->
                        { (attemptDto "notify-order-declined-v2" attempt) with
                            Reason = reason }
                    | NotifyOrderCancelled orderId ->
                        { empty "notify-order-cancelled-v2" with
                            OrderId = orderId }
                    | NotifyOrderVoided authorized -> authorizedDto "notify-order-voided-v2" authorized
                    | NotifyOrderCaptured capture ->
                        { (captureRequestDto "notify-order-captured-v2" capture.Request) with
                            ProviderReference = capture.ProviderReference }

                encode "PaymentAction" dto)
            (fun json ->
                decode "PaymentAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "call-gateway-authorize-v1"
                    | "call-gateway-authorize-v2" ->
                        attemptOfDto "PaymentAction" dto |> Result.map CallGatewayAuthorize
                    | "query-gateway-authorization-v1"
                    | "query-gateway-authorization-v2" ->
                        attemptOfDto "PaymentAction" dto |> Result.map QueryGatewayAuthorization
                    | "call-gateway-capture-v2" ->
                        captureActionOfDto "PaymentAction" dto
                        |> Result.map (fun (authorized, request) -> CallGatewayCapture(authorized, request))
                    | "query-gateway-capture-v2" ->
                        captureActionOfDto "PaymentAction" dto
                        |> Result.map (fun (authorized, request) -> QueryGatewayCapture(authorized, request))
                    | "call-gateway-void-v1"
                    | "call-gateway-void-v2" -> authorizedOfDto "PaymentAction" dto |> Result.map CallGatewayVoid
                    | "notify-order-authorized-v1"
                    | "notify-order-authorized-v2" ->
                        authorizedOfDto "PaymentAction" dto |> Result.map NotifyOrderAuthorized
                    | "notify-order-declined-v1"
                    | "notify-order-declined-v2" ->
                        attemptOfDto "PaymentAction" dto
                        |> Result.bind (fun attempt ->
                            NonEmptyString.create 200 dto.Reason
                            |> Result.mapError (fun m -> codecError "PaymentAction" m)
                            |> Result.map (fun reason -> NotifyOrderDeclined(attempt, NonEmptyString.value reason)))
                    | "notify-order-cancelled-v1"
                    | "notify-order-cancelled-v2" ->
                        NonEmptyString.create 100 dto.OrderId
                        |> Result.mapError (fun m -> codecError "PaymentAction" m)
                        |> Result.map (fun orderId -> NotifyOrderCancelled(NonEmptyString.value orderId))
                    | "notify-order-voided-v1"
                    | "notify-order-voided-v2" -> authorizedOfDto "PaymentAction" dto |> Result.map NotifyOrderVoided
                    | "notify-order-captured-v2" ->
                        captureRequestOfDto "PaymentAction" dto
                        |> Result.bind (fun request ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "PaymentAction" m)
                            |> Result.map (fun reference ->
                                NotifyOrderCaptured
                                    { Request = request
                                      ProviderReference = NonEmptyString.value reference }))
                    | tag -> Error(codecError "PaymentAction" $"Unknown tag '{tag}'.")))

    let error: Codec<PaymentActionError> =
        Codec.create
            (fun errorValue ->
                let tag =
                    match errorValue with
                    | PaymentActionError.InvalidPaymentEntityId -> "invalid-payment-entity-id-v2"
                    | PaymentActionError.CallbackEncodingFailed -> "callback-encoding-failed-v2"
                    | PaymentActionError.ActionReceiptMismatch -> "action-receipt-mismatch-v2"
                    | PaymentActionError.OperationConflict -> "operation-conflict-v2"
                    | PaymentActionError.InvalidAction -> "invalid-action-v2"

                encode "PaymentActionError" (empty tag))
            (fun json ->
                decode "PaymentActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "invalid-payment-entity-id-v1"
                    | "invalid-payment-entity-id-v2" -> Ok PaymentActionError.InvalidPaymentEntityId
                    | "callback-encoding-failed-v1"
                    | "callback-encoding-failed-v2" -> Ok PaymentActionError.CallbackEncodingFailed
                    | "action-receipt-mismatch-v1"
                    | "action-receipt-mismatch-v2" -> Ok PaymentActionError.ActionReceiptMismatch
                    | "operation-conflict-v1"
                    | "operation-conflict-v2" -> Ok PaymentActionError.OperationConflict
                    | "invalid-action-v1"
                    | "invalid-action-v2" -> Ok PaymentActionError.InvalidAction
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
            chartVersion 2
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
