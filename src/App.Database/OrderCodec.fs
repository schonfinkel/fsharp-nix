namespace App.Database

open System
open System.Text.Json
open System.Text.Json.Serialization
open App.Domain
open App.Orders
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging

module OrderWire =
    [<CLIMutable>]
    type LineDto =
        { LineId: string
          ProductId: string
          Sku: string
          Name: string
          Amount: string
          Currency: string
          Quantity: int
          PriceVersion: int64 }

    [<CLIMutable>]
    type WireDto =
        { Tag: string
          SnapshotId: string
          CustomerId: string
          CartId: string
          Lines: LineDto array
          Subtotal: string
          Shipping: string
          Tax: string
          Total: string
          Currency: string
          Generation: int64
          ReservationIds: string array
          Failure: string
          Deadline: int64
          Reason: string
          Attempt: string
          Method: string
          ProviderReference: string
          ReservationsSettled: bool
          PaymentSettled: bool }

[<RequireQualifiedAccess>]
module OrderCodec =
    let private options =
        JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

    do options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let private codecError name message =
        CodecError.DecodeError(name, FormatException message)

    let private empty tag : OrderWire.WireDto =
        { Tag = tag
          SnapshotId = ""
          CustomerId = ""
          CartId = ""
          Lines = [||]
          Subtotal = ""
          Shipping = ""
          Tax = ""
          Total = ""
          Currency = ""
          Generation = 0L
          ReservationIds = [||]
          Failure = ""
          Deadline = 0L
          Reason = ""
          Attempt = ""
          Method = ""
          ProviderReference = ""
          ReservationsSettled = false
          PaymentSettled = false }

    let private encode name (dto: OrderWire.WireDto) =
        try
            Ok(JsonSerializer.Serialize(dto, options))
        with ex ->
            Error(CodecError.EncodeError(name, ex))

    let private decode name (json: string) : Result<OrderWire.WireDto, CodecError> =
        try
            let dto = JsonSerializer.Deserialize<OrderWire.WireDto>(json, options)

            if isNull (box dto) then
                Error(codecError name "JSON decoded to null.")
            elif String.IsNullOrWhiteSpace dto.Tag then
                Error(codecError name "Missing tag.")
            else
                Ok dto
        with ex ->
            Error(CodecError.DecodeError(name, ex))

    let private guid name value =
        match Guid.TryParseExact(value, "D") with
        | true, id when id <> Guid.Empty -> Ok id
        | _ -> Error(codecError name "Expected a non-empty GUID in D format.")

    let private idValue parse name value =
        parse value |> Result.mapError (fun message -> codecError name message)

    let private sequenceResults values =
        values
        |> List.fold
            (fun state item -> state |> Result.bind (fun acc -> item |> Result.map (fun value -> value :: acc)))
            (Ok [])
        |> Result.map List.rev

    let private lineDto (line: OrderLine) : OrderWire.LineDto =
        { LineId = OrderLineId.wireString line.LineId
          ProductId = ProductId.wireString line.ProductId
          Sku = Sku.value line.Sku
          Name = NonEmptyString.value line.Name
          Amount = Money.wireAmount line.UnitPrice
          Currency = Money.currencyCode line.UnitPrice
          Quantity = Quantity.value line.Quantity
          PriceVersion = PriceVersion.value line.PriceVersion }

    let private lineOfDto (dto: OrderWire.LineDto) : Result<OrderLine, CodecError> =
        guid "OrderLine" dto.LineId
        |> Result.bind (
            OrderLineId.create
            >> Result.mapError (fun message -> codecError "OrderLine" message)
        )
        |> Result.bind (fun lineId ->
            idValue ProductId.tryParse "OrderLine" dto.ProductId
            |> Result.bind (fun productId ->
                idValue Sku.tryParse "OrderLine" dto.Sku
                |> Result.bind (fun sku ->
                    NonEmptyString.create 200 dto.Name
                    |> Result.mapError (fun message -> codecError "OrderLine" message)
                    |> Result.bind (fun name ->
                        Money.tryOfWire dto.Amount dto.Currency
                        |> Result.mapError (fun message -> codecError "OrderLine" message)
                        |> Result.bind (fun unitPrice ->
                            Quantity.create dto.Quantity
                            |> Result.mapError (fun message -> codecError "OrderLine" message)
                            |> Result.bind (fun quantity ->
                                PriceVersion.create dto.PriceVersion
                                |> Result.mapError (fun message -> codecError "OrderLine" message)
                                |> Result.map (fun priceVersion ->
                                    { LineId = lineId
                                      ProductId = productId
                                      Sku = sku
                                      Name = name
                                      UnitPrice = unitPrice
                                      Quantity = quantity
                                      PriceVersion = priceVersion })))))))

    let private linesDto lines =
        lines |> List.map lineDto |> List.toArray

    let private linesOfDto (dtos: OrderWire.LineDto array) =
        dtos |> Array.toList |> List.map lineOfDto |> sequenceResults

    let private totalsDto (totals: OrderTotals) =
        { empty "" with
            Subtotal = Money.wireAmount totals.Subtotal
            Shipping = Money.wireAmount totals.Shipping
            Tax = Money.wireAmount totals.Tax
            Total = Money.wireAmount totals.Total
            Currency = Money.currencyCode totals.Total }

    let private totalsOfDto (dto: OrderWire.WireDto) =
        let money amount =
            Money.tryOfWire amount dto.Currency
            |> Result.mapError (fun msg -> codecError "OrderTotals" msg)

        money dto.Subtotal
        |> Result.bind (fun subtotal ->
            money dto.Shipping
            |> Result.bind (fun shipping ->
                money dto.Tax
                |> Result.bind (fun tax ->
                    money dto.Total
                    |> Result.map (fun total ->
                        { Subtotal = subtotal
                          Shipping = shipping
                          Tax = tax
                          Total = total }))))

    let private pendingDto tag (pending: ReservationPendingOrder) =
        { (totalsDto pending.Totals) with
            Tag = tag
            SnapshotId = OrderSnapshotId.wireString pending.SnapshotId
            CustomerId = pending.CustomerId
            CartId = pending.CartId
            Lines = linesDto pending.Lines
            Generation = pending.Generation }

    let private pendingOfDto (dto: OrderWire.WireDto) =
        guid "Order" dto.SnapshotId
        |> Result.bind (OrderSnapshotId.create >> Result.mapError (fun msg -> codecError "Order" msg))
        |> Result.bind (fun snapshotId ->
            linesOfDto dto.Lines
            |> Result.bind (fun lines ->
                totalsOfDto dto
                |> Result.bind (fun totals ->
                    if
                        String.IsNullOrWhiteSpace dto.CustomerId
                        || String.IsNullOrWhiteSpace dto.CartId
                        || dto.Generation < 1L
                        || lines.IsEmpty
                    then
                        Error(codecError "Order" "Invalid pending-order fields.")
                    else
                        Ok
                            { SnapshotId = snapshotId
                              CustomerId = dto.CustomerId
                              CartId = dto.CartId
                              Lines = lines
                              Totals = totals
                              Generation = dto.Generation })))

    let private reservationIdsOfDto name (dto: OrderWire.WireDto) =
        dto.ReservationIds
        |> Array.toList
        |> List.map (fun value ->
            guid name value
            |> Result.bind (ReservationId.create >> Result.mapError (fun m -> codecError name m)))
        |> sequenceResults

    let private reservedOfDto (dto: OrderWire.WireDto) =
        pendingOfDto dto
        |> Result.bind (fun pending ->
            reservationIdsOfDto "OrderState" dto
            |> Result.map (fun ids ->
                { Pending = pending
                  ReservationIds = ids }))

    let state: Codec<OrderState> =
        Codec.create
            (fun state ->
                let dto =
                    match state with
                    | Initial -> empty "initial-v2"
                    | ReservationPending order -> pendingDto "reservation-pending-v2" order
                    | AwaitingAuthorization order ->
                        { pendingDto "awaiting-authorization-v2" order.Pending with
                            ReservationIds = order.ReservationIds |> List.map ReservationId.wireString |> List.toArray }
                    | PaymentPending order ->
                        { pendingDto "payment-pending-v2" order.Reserved.Pending with
                            ReservationIds =
                                order.Reserved.ReservationIds
                                |> List.map ReservationId.wireString
                                |> List.toArray
                            Attempt = PaymentOperationId.value order.Attempt }
                    | StockCommitPending order ->
                        { pendingDto "stock-commit-pending-v2" order.Reserved.Pending with
                            ReservationIds =
                                order.Reserved.ReservationIds
                                |> List.map ReservationId.wireString
                                |> List.toArray
                            ProviderReference = order.ProviderReference }
                    | Placed order ->
                        { pendingDto "placed-v2" order.Reserved.Pending with
                            ReservationIds =
                                order.Reserved.ReservationIds
                                |> List.map ReservationId.wireString
                                |> List.toArray
                            ProviderReference = order.ProviderReference }
                    | CancellationPending order ->
                        { pendingDto "cancellation-pending-v2" order.Order with
                            ReservationIds = order.ReservationIds |> List.map ReservationId.wireString |> List.toArray
                            Reason = order.Reason
                            ReservationsSettled = order.ReservationsSettled
                            PaymentSettled = order.PaymentSettled }
                    | Cancelled -> empty "cancelled-v2"
                    | ReservationFailed code ->
                        { empty "reservation-failed-v2" with
                            Failure = code }
                    | ManualReview reason ->
                        { empty "manual-review-v2" with
                            Reason = reason }
                    | Closed -> empty "closed-v2"

                encode "OrderState" dto)
            (fun json ->
                decode "OrderState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial-v2" -> Ok Initial
                    | "cancelled-v2" -> Ok Cancelled
                    | "closed-v2" -> Ok Closed
                    | "reservation-failed-v2" when not (String.IsNullOrWhiteSpace dto.Failure) ->
                        Ok(ReservationFailed dto.Failure)
                    | "manual-review-v2" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        Ok(ManualReview dto.Reason)
                    | "reservation-pending-v2" -> pendingOfDto dto |> Result.map ReservationPending
                    | "awaiting-authorization-v2" -> reservedOfDto dto |> Result.map AwaitingAuthorization
                    | "payment-pending-v2" ->
                        reservedOfDto dto
                        |> Result.bind (fun reserved ->
                            PaymentOperationId.tryParse dto.Attempt
                            |> Result.mapError (fun m -> codecError "OrderState" m)
                            |> Result.map (fun attempt ->
                                PaymentPending
                                    { Reserved = reserved
                                      Attempt = attempt }))
                    | "stock-commit-pending-v2" ->
                        reservedOfDto dto
                        |> Result.bind (fun reserved ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "OrderState" m)
                            |> Result.map (fun reference ->
                                StockCommitPending
                                    { Reserved = reserved
                                      ProviderReference = NonEmptyString.value reference }))
                    | "placed-v2" ->
                        reservedOfDto dto
                        |> Result.bind (fun reserved ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "OrderState" m)
                            |> Result.map (fun reference ->
                                Placed
                                    { Reserved = reserved
                                      ProviderReference = NonEmptyString.value reference }))
                    | "cancellation-pending-v2" ->
                        pendingOfDto dto
                        |> Result.bind (fun pending ->
                            reservationIdsOfDto "OrderState" dto
                            |> Result.bind (fun ids ->
                                NonEmptyString.create 200 dto.Reason
                                |> Result.mapError (fun m -> codecError "OrderState" m)
                                |> Result.map (fun reason ->
                                    CancellationPending
                                        { Order = pending
                                          ReservationIds = ids
                                          Reason = NonEmptyString.value reason
                                          ReservationsSettled = dto.ReservationsSettled
                                          PaymentSettled = dto.PaymentSettled })))
                    | tag -> Error(codecError "OrderState" $"Unknown or invalid tag '{tag}'.")))

    let event: Codec<OrderEvent> =
        Codec.create
            (fun event ->
                let dto =
                    match event with
                    | OrderSubmitted order -> pendingDto "order-submitted-v2" order
                    | StockReserved(ids, generation) ->
                        { empty "stock-reserved-v2" with
                            ReservationIds = ids |> List.map ReservationId.wireString |> List.toArray
                            Generation = generation }
                    | StockReservationFailed failure ->
                        { empty "stock-reservation-failed-v2" with
                            Failure = string failure }
                    | ReservationExpired(generation, deadline) ->
                        { empty "reservation-expired-v2" with
                            Generation = generation
                            Deadline = deadline.ToUnixTimeMilliseconds() }
                    | CancelRequested -> empty "cancel-requested-v2"
                    | ReservationsReleased -> empty "reservations-released-v2"
                    | AuthorizePaymentRequested(method, attempt) ->
                        { empty "authorize-payment-requested-v2" with
                            Method = PaymentMethodReference.value method
                            Attempt = PaymentOperationId.value attempt }
                    | PaymentAuthorized(attempt, reference) ->
                        { empty "payment-authorized-v2" with
                            Attempt = PaymentOperationId.value attempt
                            ProviderReference = reference }
                    | PaymentDeclined(attempt, reason) ->
                        { empty "payment-declined-v2" with
                            Attempt = PaymentOperationId.value attempt
                            Failure = reason }
                    | PaymentSettled -> empty "payment-settled-v2"
                    | StockCommitted -> empty "stock-committed-v2"
                    | StockCommitFailed reason ->
                        { empty "stock-commit-failed-v2" with
                            Failure = reason }
                    | MarkManualReview reason ->
                        { empty "mark-manual-review-v2" with
                            Reason = reason }

                encode "OrderEvent" dto)
            (fun json ->
                decode "OrderEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "order-submitted-v2" -> pendingOfDto dto |> Result.map OrderSubmitted
                    | "cancel-requested-v2" -> Ok CancelRequested
                    | "reservations-released-v2" -> Ok ReservationsReleased
                    | "payment-settled-v2" -> Ok PaymentSettled
                    | "stock-committed-v2" -> Ok StockCommitted
                    | "mark-manual-review-v2" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        Ok(MarkManualReview dto.Reason)
                    | "stock-commit-failed-v2" when not (String.IsNullOrWhiteSpace dto.Failure) ->
                        Ok(StockCommitFailed dto.Failure)
                    | "stock-reservation-failed-v2" ->
                        match dto.Failure with
                        | "InsufficientStock" -> Ok(StockReservationFailed ReservationFailure.InsufficientStock)
                        | "ProductInactive" -> Ok(StockReservationFailed ReservationFailure.ProductInactive)
                        | "PriceVersionMismatch" -> Ok(StockReservationFailed ReservationFailure.PriceVersionMismatch)
                        | _ -> Ok(StockReservationFailed ReservationFailure.InvalidReservation)
                    | "authorize-payment-requested-v2" ->
                        PaymentMethodReference.tryParse dto.Method
                        |> Result.mapError (fun m -> codecError "OrderEvent" m)
                        |> Result.bind (fun method ->
                            PaymentOperationId.tryParse dto.Attempt
                            |> Result.mapError (fun m -> codecError "OrderEvent" m)
                            |> Result.map (fun attempt -> AuthorizePaymentRequested(method, attempt)))
                    | "payment-authorized-v2" ->
                        PaymentOperationId.tryParse dto.Attempt
                        |> Result.mapError (fun m -> codecError "OrderEvent" m)
                        |> Result.bind (fun attempt ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "OrderEvent" m)
                            |> Result.map (fun reference ->
                                PaymentAuthorized(attempt, NonEmptyString.value reference)))
                    | "payment-declined-v2" ->
                        PaymentOperationId.tryParse dto.Attempt
                        |> Result.mapError (fun m -> codecError "OrderEvent" m)
                        |> Result.bind (fun attempt ->
                            NonEmptyString.create 200 dto.Failure
                            |> Result.mapError (fun m -> codecError "OrderEvent" m)
                            |> Result.map (fun reason -> PaymentDeclined(attempt, NonEmptyString.value reason)))
                    | "stock-reserved-v2" ->
                        reservationIdsOfDto "OrderEvent" dto
                        |> Result.map (fun ids -> StockReserved(ids, dto.Generation))
                    | "reservation-expired-v2" ->
                        try
                            DateTimeOffset.FromUnixTimeMilliseconds dto.Deadline
                            |> fun deadline -> Ok(ReservationExpired(dto.Generation, deadline))
                        with _ ->
                            Error(codecError "OrderEvent" "Invalid deadline.")
                    | tag -> Error(codecError "OrderEvent" $"Unknown tag '{tag}'.")))

    let action: Codec<OrderAction> =
        Codec.create
            (fun action ->
                match action with
                | ReserveStock order -> encode "OrderAction" (pendingDto "reserve-stock-v2" order)
                | ReleaseReservations ids ->
                    encode
                        "OrderAction"
                        { empty "release-reservations-v2" with
                            ReservationIds = ids |> List.map ReservationId.wireString |> List.toArray }
                | NotifyCartConverted cartId ->
                    encode
                        "OrderAction"
                        { empty "notify-cart-converted-v2" with
                            CartId = cartId }
                | RequestAuthorization(method, attempt, amount) ->
                    encode
                        "OrderAction"
                        { empty "request-authorization-v2" with
                            Method = PaymentMethodReference.value method
                            Attempt = PaymentOperationId.value attempt
                            Total = Money.wireAmount amount
                            Currency = Money.currencyCode amount }
                | RequestPaymentCancellation reason ->
                    encode
                        "OrderAction"
                        { empty "request-payment-cancellation-v2" with
                            Reason = reason }
                | CommitStock ids ->
                    encode
                        "OrderAction"
                        { empty "commit-stock-v2" with
                            ReservationIds = ids |> List.map ReservationId.wireString |> List.toArray })
            (fun json ->
                decode "OrderAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "reserve-stock-v2" -> pendingOfDto dto |> Result.map ReserveStock
                    | "notify-cart-converted-v2" when not (String.IsNullOrWhiteSpace dto.CartId) ->
                        Ok(NotifyCartConverted dto.CartId)
                    | "request-authorization-v2" ->
                        PaymentMethodReference.tryParse dto.Method
                        |> Result.mapError (fun m -> codecError "OrderAction" m)
                        |> Result.bind (fun method ->
                            PaymentOperationId.tryParse dto.Attempt
                            |> Result.mapError (fun m -> codecError "OrderAction" m)
                            |> Result.bind (fun attempt ->
                                Money.tryOfWire dto.Total dto.Currency
                                |> Result.mapError (fun m -> codecError "OrderAction" m)
                                |> Result.map (fun amount -> RequestAuthorization(method, attempt, amount))))
                    | "request-payment-cancellation-v2" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        Ok(RequestPaymentCancellation dto.Reason)
                    | "commit-stock-v2" -> reservationIdsOfDto "OrderAction" dto |> Result.map CommitStock
                    | "release-reservations-v2" ->
                        reservationIdsOfDto "OrderAction" dto |> Result.map ReleaseReservations
                    | tag -> Error(codecError "OrderAction" $"Unknown tag '{tag}'.")))

    let error: Codec<OrderActionError> =
        Codec.create
            (fun errorValue ->
                let tag =
                    match errorValue with
                    | OrderActionError.InvalidOrderEntityId -> "invalid-order-entity-id-v2"
                    | OrderActionError.SnapshotMissing -> "snapshot-missing-v2"
                    | OrderActionError.CallbackEncodingFailed -> "callback-encoding-v2"
                    | OrderActionError.ActionReceiptMismatch -> "action-receipt-mismatch-v2"
                    | OrderActionError.InvalidAction -> "invalid-action-v2"

                encode "OrderActionError" (empty tag))
            (fun json ->
                decode "OrderActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "invalid-order-entity-id-v2" -> Ok OrderActionError.InvalidOrderEntityId
                    | "snapshot-missing-v2" -> Ok OrderActionError.SnapshotMissing
                    | "callback-encoding-v2" -> Ok OrderActionError.CallbackEncodingFailed
                    | "action-receipt-mismatch-v2" -> Ok OrderActionError.ActionReceiptMismatch
                    | "invalid-action-v2" -> Ok OrderActionError.InvalidAction
                    | tag -> Error(codecError "OrderActionError" $"Unknown tag '{tag}'.")))

    let storeOptions
        (context: PostgresContext)
        : MachineStoreOptions<OrderId, OrderState, OrderEvent, OrderAction, OrderActionError> =
        { MachineStoreOptions.forEntityId<Order, OrderState, OrderEvent, OrderAction, OrderActionError>
              context
              Orders.ActionQueue with
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
        machine<OrderId, OrderState, OrderEvent, OrderAction, OrderActionError> (machineId Orders.MachineKey) {
            chart Orders.chartValue
            chartVersion 2
            initialState Orders.initialState
            store storeArg
            logger log
        }

    let buildWorker log context =
        build log (workerStore context :> IMachineStore<OrderId, OrderState, OrderEvent, OrderAction, OrderActionError>)

    let buildClient log context =
        build log (clientStore context :> IMachineStore<OrderId, OrderState, OrderEvent, OrderAction, OrderActionError>)
