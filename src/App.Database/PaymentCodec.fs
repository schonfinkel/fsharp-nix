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
    type RefundDto =
        { RefundId: string
          AllocationId: string
          OperationId: string
          Origin: string
          ReturnId: string
          OrderId: string
          Amount: string
          Currency: string
          Status: string
          PaymentReference: string }

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
          Captures: CaptureDto array
          Refund: RefundDto
          Refunds: RefundDto array
          RefundAllocationId: string }

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
          Captures = [||]
          Refund = Unchecked.defaultof<PaymentWire.RefundDto>
          Refunds = [||]
          RefundAllocationId = "" }

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

    let refundDto (request: RefundRequest) : PaymentWire.RefundDto =
        let origin, returnId =
            match request.Origin with
            | OrderCancellation _ -> "cancellation", ""
            | InspectedReturn(_, id) -> "return", id.ToString("D")

        { RefundId = RefundId.wireString request.RefundId
          AllocationId = RefundAllocationId.wireString request.AllocationId
          OperationId = PaymentOperationId.value request.OperationId
          Origin = origin
          ReturnId = returnId
          OrderId = RefundOrigin.orderId request.Origin
          Amount = Money.wireAmount request.Amount
          Currency = Money.currencyCode request.Amount
          Status = ""
          PaymentReference = "" }

    let refundOfDto name (dto: PaymentWire.RefundDto) : Result<RefundRequest, CodecError> =
        if isNull (box dto) then
            Error(codecError name "A refund payload is required.")
        else
            RefundId.tryParse dto.RefundId
            |> Result.mapError (codecError name)
            |> Result.bind (fun id ->
                RefundAllocationId.tryParse dto.AllocationId
                |> Result.mapError (codecError name)
                |> Result.bind (fun allocationId ->
                    PaymentOperationId.tryParse dto.OperationId
                    |> Result.mapError (codecError name)
                    |> Result.bind (fun operationId ->
                        let origin =
                            match dto.Origin with
                            | "cancellation" -> Ok(OrderCancellation dto.OrderId)
                            | "return" ->
                                match Guid.TryParseExact(dto.ReturnId, "D") with
                                | true, guid when guid <> Guid.Empty -> Ok(InspectedReturn(dto.OrderId, guid))
                                | _ -> Error(codecError name "Invalid return origin.")
                            | _ -> Error(codecError name "Invalid refund origin.")

                        origin
                        |> Result.bind (fun origin ->
                            Money.tryOfWire dto.Amount dto.Currency
                            |> Result.mapError (codecError name)
                            |> Result.bind (fun amount ->
                                if String.IsNullOrWhiteSpace dto.OrderId || Money.amount amount <= 0m then
                                    Error(codecError name "Invalid refund order or amount.")
                                else
                                    Ok
                                        { RefundId = id
                                          AllocationId = allocationId
                                          OperationId = operationId
                                          Origin = origin
                                          Amount = amount })))))

    let private capturedDto tag (payment: CapturedPayment) =
        { (authorizedDto tag payment.Authorization) with
            Captures = payment.Captures |> List.map captureDto |> List.toArray
            Refunds =
                payment.Refunds
                |> List.map (fun row ->
                    { refundDto row.Request with
                        Status = row.Status })
                |> List.toArray }

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

                let refundRows = if isNull dto.Refunds then [||] else dto.Refunds

                let refunds =
                    refundRows
                    |> Array.toList
                    |> List.map (fun row ->
                        refundOfDto name row
                        |> Result.bind (fun request ->
                            if row.Status = "pending" || row.Status = "settled" || row.Status = "released" then
                                Ok
                                    { Request = request
                                      Status = row.Status }
                            else
                                Error(codecError name "Invalid refund status.")))
                    |> List.fold
                        (fun acc row -> acc |> Result.bind (fun rows -> row |> Result.map (fun r -> r :: rows)))
                        (Ok [])
                    |> Result.map List.rev

                if not valid then
                    Error(codecError name "Invalid capture ledger.")
                else
                    refunds
                    |> Result.bind (fun refunds ->
                        let ids = refunds |> List.map (fun row -> row.Request.AllocationId)

                        let total =
                            refunds
                            |> List.filter (fun row -> row.Status <> "released")
                            |> List.sumBy (fun row -> Money.amount row.Request.Amount)

                        if
                            Set.count (Set.ofList ids) <> ids.Length
                            || refunds
                               |> List.exists (fun row ->
                                   RefundOrigin.orderId row.Request.Origin <> orderId
                                   || Money.currencyCode row.Request.Amount <> currency)
                            || total > (captures |> List.sumBy (fun row -> Money.amount row.Request.Amount))
                        then
                            Error(codecError name "Invalid refund ledger.")
                        else
                            Ok
                                { Authorization = authorization
                                  Captures = captures
                                  Refunds = refunds })))

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
                    | Initial -> empty "initial-v1"
                    | AuthorizationPending pending -> pendingDto "authorization-pending-v1" pending
                    | AuthorizationUnknown pending -> pendingDto "authorization-unknown-v1" pending
                    | Authorized authorized -> authorizedDto "authorized-v1" authorized
                    | CapturePending pending -> pendingCaptureDto "capture-pending-v1" pending
                    | CaptureUnknown pending -> pendingCaptureDto "capture-unknown-v1" pending
                    | PartiallyCaptured payment -> capturedDto "partially-captured-v1" payment
                    | Captured payment -> capturedDto "captured-v1" payment
                    | RefundAllocationPending payment -> capturedDto "refund-allocation-pending-v1" payment
                    | VoidPending authorized -> authorizedDto "void-pending-v1" authorized
                    | VoidUnknown authorized -> authorizedDto "void-unknown-v1" authorized
                    | Voided authorized -> authorizedDto "voided-v1" authorized
                    | Declined reason ->
                        { empty "declined-v1" with
                            Reason = ReasonCode.value reason }
                    | CancelledWithoutCharge -> empty "cancelled-without-charge-v1"
                    | ManualReview reason ->
                        { empty "manual-review-v1" with
                            Reason = ReasonCode.value reason }

                encode "PaymentState" dto)
            (fun json ->
                decode "PaymentState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial-v1" -> Ok Initial
                    | "cancelled-without-charge-v1" -> Ok CancelledWithoutCharge
                    | "declined-v1" -> (CodecSupport.reason dto.Reason |> Result.map Declined)
                    | "manual-review-v1" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        (CodecSupport.reason dto.Reason |> Result.map ManualReview)
                    | "authorization-pending-v1" -> pendingOfDto "PaymentState" dto |> Result.map AuthorizationPending
                    | "authorization-unknown-v1" -> pendingOfDto "PaymentState" dto |> Result.map AuthorizationUnknown
                    | "authorized-v1" -> authorizedOfDto "PaymentState" dto |> Result.map Authorized
                    | "capture-pending-v1" -> pendingCaptureOfDto "PaymentState" dto |> Result.map CapturePending
                    | "capture-unknown-v1" -> pendingCaptureOfDto "PaymentState" dto |> Result.map CaptureUnknown
                    | "partially-captured-v1" ->
                        capturedOfDto "PaymentState" dto
                        |> Result.bind (fun payment ->
                            let total =
                                payment.Captures
                                |> List.sumBy (fun capture -> Money.amount capture.Request.Amount)

                            if total > 0m && total < Money.amount payment.Authorization.Attempt.Amount then
                                Ok(PartiallyCaptured payment)
                            else
                                Error(codecError "PaymentState" "Invalid partially captured total."))
                    | "captured-v1" ->
                        capturedOfDto "PaymentState" dto
                        |> Result.bind (fun payment ->
                            let total =
                                payment.Captures
                                |> List.sumBy (fun capture -> Money.amount capture.Request.Amount)

                            if total = Money.amount payment.Authorization.Attempt.Amount then
                                Ok(Captured payment)
                            else
                                Error(codecError "PaymentState" "Invalid captured total."))
                    | "refund-allocation-pending-v1" ->
                        capturedOfDto "PaymentState" dto
                        |> Result.bind (fun payment ->
                            if payment.Refunds |> List.exists (fun row -> row.Status = "pending") then
                                Ok(RefundAllocationPending payment)
                            else
                                Error(codecError "PaymentState" "No pending refund allocations."))
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
                            Reason = ReasonCode.value reason }
                    | AuthorizationOutcomeUnknown attempt -> attemptDto "authorization-outcome-unknown-v1" attempt
                    | CaptureRequested request -> captureRequestDto "capture-requested-v1" request
                    | CaptureSucceeded(request, reference) ->
                        { (captureRequestDto "capture-succeeded-v1" request) with
                            ProviderReference = reference }
                    | CaptureOutcomeUnknown request -> captureRequestDto "capture-outcome-unknown-v1" request
                    | CaptureDeclined(request, reason) ->
                        { (captureRequestDto "capture-declined-v1" request) with
                            Reason = ReasonCode.value reason }
                    | RefundAllocationRequested request ->
                        { empty "refund-allocation-requested-v1" with
                            Refund = refundDto request }
                    | RefundAllocationSettled id ->
                        { empty "refund-allocation-settled-v1" with
                            RefundAllocationId = RefundAllocationId.wireString id }
                    | RefundAllocationReleased id ->
                        { empty "refund-allocation-released-v1" with
                            RefundAllocationId = RefundAllocationId.wireString id }
                    | PaymentCancellationRequested(orderId, reason) ->
                        { empty "cancel-requested-v1" with
                            OrderId = orderId
                            Reason = ReasonCode.value reason }
                    | VoidSucceeded authorized -> authorizedDto "void-succeeded-v1" authorized
                    | VoidOutcomeUnknown authorized -> authorizedDto "void-outcome-unknown-v1" authorized
                    | MarkManualReview reason ->
                        { empty "mark-manual-review-v1" with
                            Reason = ReasonCode.value reason }
                    | ReconcileRequested operation ->
                        { empty "reconcile-requested-v1" with
                            OperationId = PaymentOperationId.value operation }
                    | ReconciliationExhausted operation ->
                        { empty "reconciliation-exhausted-v1" with
                            OperationId = PaymentOperationId.value operation }

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
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> AuthorizationDeclined(attempt, reason)))
                    | "authorization-outcome-unknown-v1" ->
                        attemptOfDto "PaymentEvent" dto |> Result.map AuthorizationOutcomeUnknown
                    | "capture-requested-v1" -> captureRequestOfDto "PaymentEvent" dto |> Result.map CaptureRequested
                    | "capture-succeeded-v1" ->
                        captureRequestOfDto "PaymentEvent" dto
                        |> Result.bind (fun request ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                            |> Result.map (fun reference ->
                                CaptureSucceeded(request, NonEmptyString.value reference)))
                    | "capture-outcome-unknown-v1" ->
                        captureRequestOfDto "PaymentEvent" dto |> Result.map CaptureOutcomeUnknown
                    | "capture-declined-v1" ->
                        captureRequestOfDto "PaymentEvent" dto
                        |> Result.bind (fun request ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> CaptureDeclined(request, reason)))
                    | "refund-allocation-requested-v1" ->
                        refundOfDto "PaymentEvent" dto.Refund |> Result.map RefundAllocationRequested
                    | "refund-allocation-settled-v1" ->
                        RefundAllocationId.tryParse dto.RefundAllocationId
                        |> Result.mapError (codecError "PaymentEvent")
                        |> Result.map RefundAllocationSettled
                    | "refund-allocation-released-v1" ->
                        RefundAllocationId.tryParse dto.RefundAllocationId
                        |> Result.mapError (codecError "PaymentEvent")
                        |> Result.map RefundAllocationReleased
                    | "cancel-requested-v1" ->
                        NonEmptyString.create 100 dto.OrderId
                        |> Result.mapError (fun m -> codecError "PaymentEvent" m)
                        |> Result.bind (fun orderId ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason ->
                                PaymentCancellationRequested(NonEmptyString.value orderId, reason)))
                    | "void-succeeded-v1" ->
                        authorizedOfDto "PaymentEvent" dto
                        |> Result.map (fun authorized -> VoidSucceeded authorized)
                    | "void-outcome-unknown-v1" ->
                        authorizedOfDto "PaymentEvent" dto
                        |> Result.map (fun authorized -> VoidOutcomeUnknown authorized)
                    | "mark-manual-review-v1" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        (CodecSupport.reason dto.Reason |> Result.map MarkManualReview)
                    | "reconcile-requested-v1" ->
                        PaymentOperationId.tryParse dto.OperationId
                        |> Result.mapError (codecError "PaymentEvent")
                        |> Result.map ReconcileRequested
                    | "reconciliation-exhausted-v1" ->
                        PaymentOperationId.tryParse dto.OperationId
                        |> Result.mapError (codecError "PaymentEvent")
                        |> Result.map ReconciliationExhausted
                    | tag -> Error(codecError "PaymentEvent" $"Unknown tag '{tag}'.")))

    let action: Codec<PaymentAction> =
        Codec.create
            (fun action ->
                let dto =
                    match action with
                    | CallGatewayAuthorize attempt -> attemptDto "call-gateway-authorize-v1" attempt
                    | QueryGatewayAuthorization attempt -> attemptDto "query-gateway-authorization-v1" attempt
                    | CallGatewayCapture(authorized, request) ->
                        captureActionDto "call-gateway-capture-v1" authorized request
                    | QueryGatewayCapture(authorized, request) ->
                        captureActionDto "query-gateway-capture-v1" authorized request
                    | CallGatewayVoid authorized -> authorizedDto "call-gateway-void-v1" authorized
                    | NotifyOrderAuthorized authorized -> authorizedDto "notify-order-authorized-v1" authorized
                    | NotifyOrderDeclined(attempt, reason) ->
                        { (attemptDto "notify-order-declined-v1" attempt) with
                            Reason = ReasonCode.value reason }
                    | NotifyOrderCancelled orderId ->
                        { empty "notify-order-cancelled-v1" with
                            OrderId = orderId }
                    | NotifyOrderVoided authorized -> authorizedDto "notify-order-voided-v1" authorized
                    | NotifyOrderCaptured capture ->
                        { (captureRequestDto "notify-order-captured-v1" capture.Request) with
                            ProviderReference = capture.ProviderReference }
                    | NotifyRefundApproved approved ->
                        { empty "notify-refund-approved-v1" with
                            Refund =
                                { refundDto approved.Request with
                                    PaymentReference = approved.PaymentReference } }
                    | NotifyRefundDenied(request, reason) ->
                        { empty "notify-refund-denied-v1" with
                            Refund = refundDto request
                            Reason = ReasonCode.value reason }
                    | NotifyRefundSettled request ->
                        { empty "notify-refund-settled-v1" with
                            Refund = refundDto request }

                encode "PaymentAction" dto)
            (fun json ->
                decode "PaymentAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "call-gateway-authorize-v1" ->
                        attemptOfDto "PaymentAction" dto |> Result.map CallGatewayAuthorize
                    | "query-gateway-authorization-v1" ->
                        attemptOfDto "PaymentAction" dto |> Result.map QueryGatewayAuthorization
                    | "call-gateway-capture-v1" ->
                        captureActionOfDto "PaymentAction" dto
                        |> Result.map (fun (authorized, request) -> CallGatewayCapture(authorized, request))
                    | "query-gateway-capture-v1" ->
                        captureActionOfDto "PaymentAction" dto
                        |> Result.map (fun (authorized, request) -> QueryGatewayCapture(authorized, request))
                    | "call-gateway-void-v1" -> authorizedOfDto "PaymentAction" dto |> Result.map CallGatewayVoid
                    | "notify-order-authorized-v1" ->
                        authorizedOfDto "PaymentAction" dto |> Result.map NotifyOrderAuthorized
                    | "notify-order-declined-v1" ->
                        attemptOfDto "PaymentAction" dto
                        |> Result.bind (fun attempt ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> NotifyOrderDeclined(attempt, reason)))
                    | "notify-order-cancelled-v1" ->
                        NonEmptyString.create 100 dto.OrderId
                        |> Result.mapError (fun m -> codecError "PaymentAction" m)
                        |> Result.map (fun orderId -> NotifyOrderCancelled(NonEmptyString.value orderId))
                    | "notify-order-voided-v1" -> authorizedOfDto "PaymentAction" dto |> Result.map NotifyOrderVoided
                    | "notify-order-captured-v1" ->
                        captureRequestOfDto "PaymentAction" dto
                        |> Result.bind (fun request ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "PaymentAction" m)
                            |> Result.map (fun reference ->
                                NotifyOrderCaptured(
                                    { Request = request
                                      ProviderReference = NonEmptyString.value reference }
                                    : CaptureRecord
                                )))
                    | "notify-refund-approved-v1" ->
                        refundOfDto "PaymentAction" dto.Refund
                        |> Result.bind (fun request ->
                            if String.IsNullOrWhiteSpace dto.Refund.PaymentReference then
                                Error(codecError "PaymentAction" "Missing payment reference.")
                            else
                                Ok(
                                    NotifyRefundApproved
                                        { Request = request
                                          PaymentReference = dto.Refund.PaymentReference }
                                ))
                    | "notify-refund-denied-v1" ->
                        refundOfDto "PaymentAction" dto.Refund
                        |> Result.bind (fun request ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> NotifyRefundDenied(request, reason)))
                    | "notify-refund-settled-v1" ->
                        refundOfDto "PaymentAction" dto.Refund |> Result.map NotifyRefundSettled
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
            chartVersion Payments.ChartVersion
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
