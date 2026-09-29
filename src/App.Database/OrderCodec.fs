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
    type AllocationLineDto = { LineId: string; Quantity: int }

    [<CLIMutable>]
    type ShipmentDto =
        { ShipmentId: string
          AllocationId: string
          CaptureId: string
          OperationId: string
          Lines: AllocationLineDto array
          Merchandise: string
          Shipping: string
          Tax: string
          Total: string
          Currency: string
          Created: bool
          CreationFailed: bool
          Dispatched: bool
          Delivered: bool
          CaptureSucceeded: bool
          RefundRequested: bool
          Refunded: bool }

    [<CLIMutable>]
    type ReturnLineDto =
        { LineId: string
          Quantity: int
          RefundAmount: string }

    [<CLIMutable>]
    type ReturnDto =
        { ReturnId: string
          AuthorizationId: string
          OrderId: string
          Currency: string
          WindowEndsAt: int64
          Lines: ReturnLineDto array
          Status: string }

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
          PaymentSettled: bool
          [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
          AddressSnapshotId: string
          [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
          ShipmentId: string
          [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
          AllocationId: string
          [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
          CaptureId: string
          [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
          OperationId: string
          [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
          Shipments: ShipmentDto array
          Refund: PaymentWire.RefundDto
          Returns: ReturnDto array
          ReturnId: string
          RefundId: string }

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
          PaymentSettled = false
          AddressSnapshotId = null
          ShipmentId = null
          AllocationId = null
          CaptureId = null
          OperationId = null
          Shipments = null
          Refund = Unchecked.defaultof<PaymentWire.RefundDto>
          Returns = null
          ReturnId = ""
          RefundId = "" }

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

    let private shipmentDto (status: OrderShipmentStatus) : OrderWire.ShipmentDto =
        let allocation = status.Plan.Allocation

        { ShipmentId = ShipmentId.wireString status.Plan.ShipmentId
          AllocationId = ShipmentAllocationId.wireString allocation.AllocationId
          CaptureId = CaptureId.wireString status.Plan.CaptureId
          OperationId = PaymentOperationId.value status.Plan.PaymentOperationId
          Lines =
            allocation.Lines
            |> List.map (fun line ->
                ({ LineId = OrderLineId.wireString line.LineId
                   Quantity = line.Quantity }
                : OrderWire.AllocationLineDto))
            |> List.toArray
          Merchandise = Money.wireAmount allocation.Merchandise
          Shipping = Money.wireAmount allocation.Shipping
          Tax = Money.wireAmount allocation.Tax
          Total = Money.wireAmount allocation.Total
          Currency = Money.currencyCode allocation.Total
          Created = status.Created
          CreationFailed = status.CreationFailed
          Dispatched = status.Dispatched
          Delivered = status.Delivered
          CaptureSucceeded = status.CaptureSucceeded
          RefundRequested = status.RefundRequested
          Refunded = status.Refunded }

    let private statusOfPlan plan =
        { Plan = plan
          Created = false
          CreationFailed = false
          Dispatched = false
          Delivered = false
          CaptureSucceeded = false
          RefundRequested = false
          Refunded = false }

    let private shipmentOfDto (dto: OrderWire.ShipmentDto) : Result<OrderShipmentStatus, CodecError> =
        let parseId parse field value =
            parse value |> Result.mapError (fun message -> codecError field message)

        let money field amount =
            Money.tryOfWire amount dto.Currency
            |> Result.mapError (fun message -> codecError field message)

        parseId ShipmentId.tryParse "Shipment" dto.ShipmentId
        |> Result.bind (fun shipmentId ->
            parseId ShipmentAllocationId.tryParse "Shipment" dto.AllocationId
            |> Result.bind (fun allocationId ->
                parseId CaptureId.tryParse "Shipment" dto.CaptureId
                |> Result.bind (fun captureId ->
                    parseId PaymentOperationId.tryParse "Shipment" dto.OperationId
                    |> Result.bind (fun operationId ->
                        (if isNull dto.Lines then [] else Array.toList dto.Lines)
                        |> List.map (fun line ->
                            guid "ShipmentLine" line.LineId
                            |> Result.bind (
                                OrderLineId.create
                                >> Result.mapError (fun message -> codecError "ShipmentLine" message)
                            )
                            |> Result.bind (fun lineId ->
                                if line.Quantity > 0 then
                                    Ok
                                        { LineId = lineId
                                          Quantity = line.Quantity }
                                else
                                    Error(codecError "ShipmentLine" "Quantity must be positive.")))
                        |> sequenceResults
                        |> Result.bind (fun lines ->
                            money "Shipment" dto.Merchandise
                            |> Result.bind (fun merchandise ->
                                money "Shipment" dto.Shipping
                                |> Result.bind (fun shipping ->
                                    money "Shipment" dto.Tax
                                    |> Result.bind (fun tax ->
                                        money "Shipment" dto.Total
                                        |> Result.bind (fun total ->
                                            if
                                                lines.IsEmpty
                                                || total <> merchandise + shipping + tax
                                                || dto.CreationFailed && dto.Created
                                                || dto.Dispatched && not dto.Created
                                                || dto.Delivered && not dto.Dispatched
                                                || dto.CaptureSucceeded && not dto.Dispatched
                                            then
                                                Error(
                                                    codecError
                                                        "Shipment"
                                                        "Invalid shipment allocation or status flags."
                                                )
                                            else
                                                Ok
                                                    { Plan =
                                                        { ShipmentId = shipmentId
                                                          CaptureId = captureId
                                                          PaymentOperationId = operationId
                                                          Allocation =
                                                            { AllocationId = allocationId
                                                              Lines = lines
                                                              Merchandise = merchandise
                                                              Shipping = shipping
                                                              Tax = tax
                                                              Total = total } }
                                                      Created = dto.Created
                                                      CreationFailed = dto.CreationFailed
                                                      Dispatched = dto.Dispatched
                                                      Delivered = dto.Delivered
                                                      CaptureSucceeded = dto.CaptureSucceeded
                                                      RefundRequested = dto.RefundRequested
                                                      Refunded = dto.Refunded })))))))))

    let returnDto (entry: OrderReturnReservation) : OrderWire.ReturnDto =
        let request = entry.Request

        { ReturnId = ReturnId.wireString request.ReturnId
          AuthorizationId = ReturnAuthorizationId.wireString request.AuthorizationId
          OrderId = request.OrderId
          Currency = request.Currency
          WindowEndsAt = request.WindowEndsAt.ToUnixTimeMilliseconds()
          Lines =
            request.Lines
            |> List.map (fun line ->
                ({ LineId = OrderLineId.wireString line.OrderLineId
                   Quantity = line.Quantity
                   RefundAmount = Money.wireAmount line.RefundAmount }
                : OrderWire.ReturnLineDto))
            |> List.toArray
          Status = entry.Status }

    let returnOfDto name (dto: OrderWire.ReturnDto) : Result<OrderReturnReservation, CodecError> =
        if isNull (box dto) then
            Error(codecError name "Missing return.")
        else
            ReturnId.tryParse dto.ReturnId
            |> Result.mapError (codecError name)
            |> Result.bind (fun id ->
                ReturnAuthorizationId.tryParse dto.AuthorizationId
                |> Result.mapError (codecError name)
                |> Result.bind (fun authId ->
                    let lines =
                        (if isNull dto.Lines then [||] else dto.Lines)
                        |> Array.toList
                        |> List.map (fun line ->
                            guid name line.LineId
                            |> Result.bind (OrderLineId.create >> Result.mapError (codecError name))
                            |> Result.bind (fun lineId ->
                                Money.tryOfWire line.RefundAmount dto.Currency
                                |> Result.mapError (codecError name)
                                |> Result.map (fun amount ->
                                    { OrderLineId = lineId
                                      Quantity = line.Quantity
                                      RefundAmount = amount })))
                        |> sequenceResults

                    lines
                    |> Result.bind (fun lines ->
                        try
                            let request =
                                { ReturnId = id
                                  AuthorizationId = authId
                                  OrderId = dto.OrderId
                                  Lines = lines
                                  Currency = dto.Currency
                                  WindowEndsAt = DateTimeOffset.FromUnixTimeMilliseconds dto.WindowEndsAt }

                            ReturnRequest.validate request
                            |> Result.mapError (codecError name)
                            |> Result.bind (fun request ->
                                if dto.Status = "pending" || dto.Status = "refunded" || dto.Status = "rejected" then
                                    Ok
                                        { Request = request
                                          Status = dto.Status }
                                else
                                    Error(codecError name "Invalid return status."))
                        with _ ->
                            Error(codecError name "Invalid return deadline."))))

    let private fulfilmentDto tag (fulfilment: FulfilmentOrder) =
        { pendingDto tag fulfilment.PlacedOrder.Reserved.Pending with
            ReservationIds =
                fulfilment.PlacedOrder.Reserved.ReservationIds
                |> List.map ReservationId.wireString
                |> List.toArray
            ProviderReference = fulfilment.PlacedOrder.ProviderReference
            AddressSnapshotId = OrderSnapshotId.wireString fulfilment.AddressSnapshotId
            Shipments = fulfilment.Shipments |> List.map shipmentDto |> List.toArray
            Returns = fulfilment.Returns |> List.map returnDto |> List.toArray }

    let private fulfilmentOfDto (dto: OrderWire.WireDto) =
        reservedOfDto dto
        |> Result.bind (fun reserved ->
            NonEmptyString.create 200 dto.ProviderReference
            |> Result.mapError (fun message -> codecError "OrderState" message)
            |> Result.bind (fun providerReference ->
                guid "OrderState" dto.AddressSnapshotId
                |> Result.bind (
                    OrderSnapshotId.create
                    >> Result.mapError (fun message -> codecError "OrderState" message)
                )
                |> Result.bind (fun addressSnapshotId ->
                    (if isNull dto.Shipments then
                         []
                     else
                         Array.toList dto.Shipments)
                    |> List.map shipmentOfDto
                    |> sequenceResults
                    |> Result.bind (fun shipments ->
                        let returns =
                            (if isNull dto.Returns then [] else Array.toList dto.Returns)
                            |> List.map (returnOfDto "OrderState")
                            |> sequenceResults

                        returns
                        |> Result.bind (fun returns ->
                            if shipments.IsEmpty then
                                Error(codecError "OrderState" "A fulfilment plan must not be empty.")
                            else
                                Ok
                                    { PlacedOrder =
                                        { Reserved = reserved
                                          ProviderReference = NonEmptyString.value providerReference }
                                      AddressSnapshotId = addressSnapshotId
                                      Shipments = shipments
                                      Returns = returns })))))

    let private shipmentIdentityOfDto name (dto: OrderWire.WireDto) =
        idValue ShipmentId.tryParse name dto.ShipmentId
        |> Result.bind (fun shipmentId ->
            idValue ShipmentAllocationId.tryParse name dto.AllocationId
            |> Result.map (fun allocationId -> shipmentId, allocationId))

    let state: Codec<OrderState> =
        Codec.create
            (fun state ->
                let dto =
                    match state with
                    | Initial -> empty "initial-v1"
                    | ReservationPending order -> pendingDto "reservation-pending-v1" order
                    | AwaitingAuthorization order ->
                        { pendingDto "awaiting-authorization-v1" order.Pending with
                            ReservationIds = order.ReservationIds |> List.map ReservationId.wireString |> List.toArray }
                    | PaymentPending order ->
                        { pendingDto "payment-pending-v1" order.Reserved.Pending with
                            ReservationIds =
                                order.Reserved.ReservationIds
                                |> List.map ReservationId.wireString
                                |> List.toArray
                            Attempt = PaymentOperationId.value order.Attempt }
                    | StockCommitPending order ->
                        { pendingDto "stock-commit-pending-v1" order.Reserved.Pending with
                            ReservationIds =
                                order.Reserved.ReservationIds
                                |> List.map ReservationId.wireString
                                |> List.toArray
                            ProviderReference = order.ProviderReference }
                    | Placed order ->
                        { pendingDto "placed-v1" order.Reserved.Pending with
                            ReservationIds =
                                order.Reserved.ReservationIds
                                |> List.map ReservationId.wireString
                                |> List.toArray
                            ProviderReference = order.ProviderReference }
                    | HeldForReview held ->
                        { fulfilmentDto "held-for-review-v1" held.Fulfilment with
                            Reason = ReasonCode.value held.Reason }
                    | FulfilmentPending order -> fulfilmentDto "fulfilment-pending-v1" order
                    | Processing order -> fulfilmentDto "processing-v1" order
                    | PartiallyShipped order -> fulfilmentDto "partially-shipped-v1" order
                    | Shipped order -> fulfilmentDto "shipped-v1" order
                    | OrderState.Delivered order -> fulfilmentDto "delivered-v1" order
                    | CancellationCompensating order -> fulfilmentDto "cancellation-compensating-v1" order
                    | CancelledAfterRefund order -> fulfilmentDto "cancelled-after-refund-v1" order
                    | CancellationPending order ->
                        { pendingDto "cancellation-pending-v1" order.Order with
                            ReservationIds = order.ReservationIds |> List.map ReservationId.wireString |> List.toArray
                            Reason = ReasonCode.value order.Reason
                            ReservationsSettled = order.ReservationsSettled
                            PaymentSettled = order.PaymentSettled }
                    | Cancelled -> empty "cancelled-v1"
                    | ReservationFailed code ->
                        { empty "reservation-failed-v1" with
                            Failure = ReasonCode.value code }
                    | ManualReview reason ->
                        { empty "manual-review-v1" with
                            Reason = ReasonCode.value reason }
                    | Closed -> empty "closed-v1"

                encode "OrderState" dto)
            (fun json ->
                decode "OrderState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial-v1" -> Ok Initial
                    | "cancelled-v1" -> Ok Cancelled
                    | "closed-v1" -> Ok Closed
                    | "reservation-failed-v1" when not (String.IsNullOrWhiteSpace dto.Failure) ->
                        (CodecSupport.reason dto.Failure |> Result.map ReservationFailed)
                    | "manual-review-v1" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        (CodecSupport.reason dto.Reason |> Result.map ManualReview)
                    | "reservation-pending-v1" -> pendingOfDto dto |> Result.map ReservationPending
                    | "awaiting-authorization-v1" -> reservedOfDto dto |> Result.map AwaitingAuthorization
                    | "payment-pending-v1" ->
                        reservedOfDto dto
                        |> Result.bind (fun reserved ->
                            PaymentOperationId.tryParse dto.Attempt
                            |> Result.mapError (fun m -> codecError "OrderState" m)
                            |> Result.map (fun attempt ->
                                PaymentPending
                                    { Reserved = reserved
                                      Attempt = attempt }))
                    | "stock-commit-pending-v1" ->
                        reservedOfDto dto
                        |> Result.bind (fun reserved ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "OrderState" m)
                            |> Result.map (fun reference ->
                                StockCommitPending
                                    { Reserved = reserved
                                      ProviderReference = NonEmptyString.value reference }))
                    | "placed-v1" ->
                        reservedOfDto dto
                        |> Result.bind (fun reserved ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "OrderState" m)
                            |> Result.map (fun reference ->
                                Placed
                                    { Reserved = reserved
                                      ProviderReference = NonEmptyString.value reference }))
                    | "held-for-review-v1" ->
                        fulfilmentOfDto dto
                        |> Result.bind (fun fulfilment ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason ->
                                HeldForReview
                                    { Fulfilment = fulfilment
                                      Reason = reason }))
                    | "fulfilment-pending-v1" -> fulfilmentOfDto dto |> Result.map FulfilmentPending
                    | "processing-v1" -> fulfilmentOfDto dto |> Result.map Processing
                    | "partially-shipped-v1" -> fulfilmentOfDto dto |> Result.map PartiallyShipped
                    | "shipped-v1" -> fulfilmentOfDto dto |> Result.map Shipped
                    | "delivered-v1" -> fulfilmentOfDto dto |> Result.map OrderState.Delivered
                    | "cancellation-compensating-v1" -> fulfilmentOfDto dto |> Result.map CancellationCompensating
                    | "cancelled-after-refund-v1" -> fulfilmentOfDto dto |> Result.map CancelledAfterRefund
                    | "cancellation-pending-v1" ->
                        pendingOfDto dto
                        |> Result.bind (fun pending ->
                            reservationIdsOfDto "OrderState" dto
                            |> Result.bind (fun ids ->
                                CodecSupport.reason dto.Reason
                                |> Result.map (fun reason ->
                                    CancellationPending
                                        { Order = pending
                                          ReservationIds = ids
                                          Reason = reason
                                          ReservationsSettled = dto.ReservationsSettled
                                          PaymentSettled = dto.PaymentSettled })))
                    | tag -> Error(codecError "OrderState" $"Unknown or invalid tag '{tag}'.")))

    let event: Codec<OrderEvent> =
        Codec.create
            (fun event ->
                let dto =
                    match event with
                    | OrderSubmitted order -> pendingDto "order-submitted-v1" order
                    | StockReserved(ids, generation) ->
                        { empty "stock-reserved-v1" with
                            ReservationIds = ids |> List.map ReservationId.wireString |> List.toArray
                            Generation = generation }
                    | StockReservationFailed failure ->
                        { empty "stock-reservation-failed-v1" with
                            Failure = string failure }
                    | ReservationExpired(generation, deadline) ->
                        { empty "reservation-expired-v1" with
                            Generation = generation
                            Deadline = deadline.ToUnixTimeMilliseconds() }
                    | CancelRequested -> empty "cancel-requested-v1"
                    | ReservationsReleased -> empty "reservations-released-v1"
                    | AuthorizePaymentRequested(method, attempt) ->
                        { empty "authorize-payment-requested-v1" with
                            Method = PaymentMethodReference.value method
                            Attempt = PaymentOperationId.value attempt }
                    | PaymentAuthorized(attempt, reference) ->
                        { empty "payment-authorized-v1" with
                            Attempt = PaymentOperationId.value attempt
                            ProviderReference = reference }
                    | PaymentDeclined(attempt, reason) ->
                        { empty "payment-declined-v1" with
                            Attempt = PaymentOperationId.value attempt
                            Failure = ReasonCode.value reason }
                    | PaymentSettled -> empty "payment-settled-v1"
                    | StockCommitted -> empty "stock-committed-v1"
                    | StockCommitFailed reason ->
                        { empty "stock-commit-failed-v1" with
                            Failure = ReasonCode.value reason }
                    | MarkManualReview reason ->
                        { empty "mark-manual-review-v1" with
                            Reason = ReasonCode.value reason }
                    | FulfilmentRequested plan ->
                        { empty "fulfilment-requested-v1" with
                            Shipments = plan |> List.map (statusOfPlan >> shipmentDto) |> List.toArray }
                    | ShipmentCreated(shipmentId, allocationId) ->
                        { empty "shipment-created-v1" with
                            ShipmentId = ShipmentId.wireString shipmentId
                            AllocationId = ShipmentAllocationId.wireString allocationId }
                    | ShipmentCreationFailed(shipmentId, allocationId, reason) ->
                        { empty "shipment-creation-failed-v1" with
                            ShipmentId = ShipmentId.wireString shipmentId
                            AllocationId = ShipmentAllocationId.wireString allocationId
                            Reason = ReasonCode.value reason }
                    | ShipmentDispatched(shipmentId, allocationId) ->
                        { empty "shipment-dispatched-v1" with
                            ShipmentId = ShipmentId.wireString shipmentId
                            AllocationId = ShipmentAllocationId.wireString allocationId }
                    | ShipmentDelivered(shipmentId, allocationId) ->
                        { empty "shipment-delivered-v1" with
                            ShipmentId = ShipmentId.wireString shipmentId
                            AllocationId = ShipmentAllocationId.wireString allocationId }
                    | PaymentCaptured(captureId, operationId) ->
                        { empty "payment-captured-v1" with
                            CaptureId = CaptureId.wireString captureId
                            OperationId = PaymentOperationId.value operationId }
                    | HoldRequested reason ->
                        { empty "hold-requested-v1" with
                            Reason = ReasonCode.value reason }
                    | ReleaseHoldRequested -> empty "release-hold-requested-v1"
                    | AddressSnapshotChanged snapshotId ->
                        { empty "address-snapshot-changed-v1" with
                            AddressSnapshotId = OrderSnapshotId.wireString snapshotId }
                    | ReturnRequested request ->
                        { empty "return-requested-v1" with
                            Returns =
                                [| returnDto
                                       { Request = request
                                         Status = "pending" } |] }
                    | ReturnRefunded id ->
                        { empty "return-refunded-v1" with
                            ReturnId = ReturnId.wireString id }
                    | ReturnRejected id ->
                        { empty "return-rejected-v1" with
                            ReturnId = ReturnId.wireString id }
                    | OrderRefunded id ->
                        { empty "order-refunded-v1" with
                            RefundId = RefundId.wireString id }
                    | OrderRefundFailed(id, reason) ->
                        { empty "order-refund-failed-v1" with
                            RefundId = RefundId.wireString id
                            Reason = ReasonCode.value reason }
                    | PaymentAuthorizationExpired -> empty "payment-authorization-expired-v1"

                encode "OrderEvent" dto)
            (fun json ->
                decode "OrderEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "order-submitted-v1" -> pendingOfDto dto |> Result.map OrderSubmitted
                    | "cancel-requested-v1" -> Ok CancelRequested
                    | "reservations-released-v1" -> Ok ReservationsReleased
                    | "payment-settled-v1" -> Ok PaymentSettled
                    | "stock-committed-v1" -> Ok StockCommitted
                    | "mark-manual-review-v1" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        (CodecSupport.reason dto.Reason |> Result.map MarkManualReview)
                    | "stock-commit-failed-v1" when not (String.IsNullOrWhiteSpace dto.Failure) ->
                        (CodecSupport.reason dto.Failure |> Result.map StockCommitFailed)
                    | "stock-reservation-failed-v1" ->
                        match dto.Failure with
                        | "InsufficientStock" -> Ok(StockReservationFailed ReservationFailure.InsufficientStock)
                        | "ProductInactive" -> Ok(StockReservationFailed ReservationFailure.ProductInactive)
                        | "PriceVersionMismatch" -> Ok(StockReservationFailed ReservationFailure.PriceVersionMismatch)
                        | _ -> Ok(StockReservationFailed ReservationFailure.InvalidReservation)
                    | "authorize-payment-requested-v1" ->
                        PaymentMethodReference.tryParse dto.Method
                        |> Result.mapError (fun m -> codecError "OrderEvent" m)
                        |> Result.bind (fun method ->
                            PaymentOperationId.tryParse dto.Attempt
                            |> Result.mapError (fun m -> codecError "OrderEvent" m)
                            |> Result.map (fun attempt -> AuthorizePaymentRequested(method, attempt)))
                    | "payment-authorized-v1" ->
                        PaymentOperationId.tryParse dto.Attempt
                        |> Result.mapError (fun m -> codecError "OrderEvent" m)
                        |> Result.bind (fun attempt ->
                            NonEmptyString.create 200 dto.ProviderReference
                            |> Result.mapError (fun m -> codecError "OrderEvent" m)
                            |> Result.map (fun reference ->
                                PaymentAuthorized(attempt, NonEmptyString.value reference)))
                    | "payment-declined-v1" ->
                        PaymentOperationId.tryParse dto.Attempt
                        |> Result.mapError (fun m -> codecError "OrderEvent" m)
                        |> Result.bind (fun attempt ->
                            CodecSupport.reason dto.Failure
                            |> Result.map (fun reason -> PaymentDeclined(attempt, reason)))
                    | "stock-reserved-v1" ->
                        reservationIdsOfDto "OrderEvent" dto
                        |> Result.map (fun ids -> StockReserved(ids, dto.Generation))
                    | "reservation-expired-v1" ->
                        try
                            DateTimeOffset.FromUnixTimeMilliseconds dto.Deadline
                            |> fun deadline -> Ok(ReservationExpired(dto.Generation, deadline))
                        with _ ->
                            Error(codecError "OrderEvent" "Invalid deadline.")
                    | "fulfilment-requested-v1" ->
                        (if isNull dto.Shipments then
                             []
                         else
                             Array.toList dto.Shipments)
                        |> List.map shipmentOfDto
                        |> sequenceResults
                        |> Result.bind (fun shipments ->
                            if
                                shipments.IsEmpty
                                || shipments
                                   |> List.exists (fun shipment ->
                                       shipment.Created
                                       || shipment.CreationFailed
                                       || shipment.Dispatched
                                       || shipment.Delivered
                                       || shipment.CaptureSucceeded)
                            then
                                Error(codecError "OrderEvent" "Invalid fulfilment plan.")
                            else
                                Ok(FulfilmentRequested(shipments |> List.map _.Plan)))
                    | "shipment-created-v1" -> shipmentIdentityOfDto "OrderEvent" dto |> Result.map ShipmentCreated
                    | "shipment-creation-failed-v1" ->
                        shipmentIdentityOfDto "OrderEvent" dto
                        |> Result.bind (fun (shipmentId, allocationId) ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> ShipmentCreationFailed(shipmentId, allocationId, reason)))
                    | "shipment-dispatched-v1" ->
                        shipmentIdentityOfDto "OrderEvent" dto |> Result.map ShipmentDispatched
                    | "shipment-delivered-v1" ->
                        shipmentIdentityOfDto "OrderEvent" dto |> Result.map ShipmentDelivered
                    | "payment-captured-v1" ->
                        idValue CaptureId.tryParse "OrderEvent" dto.CaptureId
                        |> Result.bind (fun captureId ->
                            idValue PaymentOperationId.tryParse "OrderEvent" dto.OperationId
                            |> Result.map (fun operationId -> PaymentCaptured(captureId, operationId)))
                    | "hold-requested-v1" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        (CodecSupport.reason dto.Reason |> Result.map HoldRequested)
                    | "release-hold-requested-v1" -> Ok ReleaseHoldRequested
                    | "address-snapshot-changed-v1" ->
                        guid "OrderEvent" dto.AddressSnapshotId
                        |> Result.bind (
                            OrderSnapshotId.create
                            >> Result.mapError (fun message -> codecError "OrderEvent" message)
                        )
                        |> Result.map AddressSnapshotChanged
                    | "return-requested-v1" when not (isNull dto.Returns) && dto.Returns.Length = 1 ->
                        returnOfDto "OrderEvent" dto.Returns[0]
                        |> Result.map (fun entry -> ReturnRequested entry.Request)
                    | "return-refunded-v1" ->
                        ReturnId.tryParse dto.ReturnId
                        |> Result.mapError (codecError "OrderEvent")
                        |> Result.map ReturnRefunded
                    | "return-rejected-v1" ->
                        ReturnId.tryParse dto.ReturnId
                        |> Result.mapError (codecError "OrderEvent")
                        |> Result.map ReturnRejected
                    | "order-refunded-v1" ->
                        RefundId.tryParse dto.RefundId
                        |> Result.mapError (codecError "OrderEvent")
                        |> Result.map OrderRefunded
                    | "order-refund-failed-v1" ->
                        RefundId.tryParse dto.RefundId
                        |> Result.mapError (codecError "OrderEvent")
                        |> Result.bind (fun id ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> OrderRefundFailed(id, reason)))
                    | "payment-authorization-expired-v1" -> Ok PaymentAuthorizationExpired
                    | tag -> Error(codecError "OrderEvent" $"Unknown tag '{tag}'.")))

    let action: Codec<OrderAction> =
        Codec.create
            (fun action ->
                match action with
                | ReserveStock order -> encode "OrderAction" (pendingDto "reserve-stock-v1" order)
                | ReleaseReservations ids ->
                    encode
                        "OrderAction"
                        { empty "release-reservations-v1" with
                            ReservationIds = ids |> List.map ReservationId.wireString |> List.toArray }
                | NotifyCartConverted cartId ->
                    encode
                        "OrderAction"
                        { empty "notify-cart-converted-v1" with
                            CartId = cartId }
                | RequestAuthorization(method, attempt, amount) ->
                    encode
                        "OrderAction"
                        { empty "request-authorization-v1" with
                            Method = PaymentMethodReference.value method
                            Attempt = PaymentOperationId.value attempt
                            Total = Money.wireAmount amount
                            Currency = Money.currencyCode amount }
                | RequestPaymentCancellation reason ->
                    encode
                        "OrderAction"
                        { empty "request-payment-cancellation-v1" with
                            Reason = ReasonCode.value reason }
                | CommitStock ids ->
                    encode
                        "OrderAction"
                        { empty "commit-stock-v1" with
                            ReservationIds = ids |> List.map ReservationId.wireString |> List.toArray }
                | CreateShipment(shipment, addressSnapshotId) ->
                    encode
                        "OrderAction"
                        { empty "create-shipment-v1" with
                            AddressSnapshotId = OrderSnapshotId.wireString addressSnapshotId
                            Shipments = [| shipment |> statusOfPlan |> shipmentDto |] }
                | RequestCapture(shipmentId, allocation, captureId, operationId, providerReference) ->
                    let shipment =
                        { ShipmentId = shipmentId
                          CaptureId = captureId
                          PaymentOperationId = operationId
                          Allocation = allocation }

                    encode
                        "OrderAction"
                        { empty "request-capture-v1" with
                            ProviderReference = providerReference
                            Shipments = [| shipment |> statusOfPlan |> shipmentDto |] }
                | StartReturn request ->
                    encode
                        "OrderAction"
                        { empty "start-return-v1" with
                            Returns =
                                [| returnDto
                                       { Request = request
                                         Status = "pending" } |] }
                | StartRefund request ->
                    encode
                        "OrderAction"
                        { empty "start-refund-v1" with
                            Refund = PaymentCodec.refundDto request }
                | RequestInvoice snapshotId ->
                    encode
                        "OrderAction"
                        { empty "request-invoice-v1" with
                            SnapshotId = OrderSnapshotId.wireString snapshotId })
            (fun json ->
                decode "OrderAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "reserve-stock-v1" -> pendingOfDto dto |> Result.map ReserveStock
                    | "notify-cart-converted-v1" when not (String.IsNullOrWhiteSpace dto.CartId) ->
                        Ok(NotifyCartConverted dto.CartId)
                    | "request-authorization-v1" ->
                        PaymentMethodReference.tryParse dto.Method
                        |> Result.mapError (fun m -> codecError "OrderAction" m)
                        |> Result.bind (fun method ->
                            PaymentOperationId.tryParse dto.Attempt
                            |> Result.mapError (fun m -> codecError "OrderAction" m)
                            |> Result.bind (fun attempt ->
                                Money.tryOfWire dto.Total dto.Currency
                                |> Result.mapError (fun m -> codecError "OrderAction" m)
                                |> Result.map (fun amount -> RequestAuthorization(method, attempt, amount))))
                    | "request-payment-cancellation-v1" when not (String.IsNullOrWhiteSpace dto.Reason) ->
                        (CodecSupport.reason dto.Reason |> Result.map RequestPaymentCancellation)
                    | "commit-stock-v1" -> reservationIdsOfDto "OrderAction" dto |> Result.map CommitStock
                    | "release-reservations-v1" ->
                        reservationIdsOfDto "OrderAction" dto |> Result.map ReleaseReservations
                    | "create-shipment-v1" ->
                        guid "OrderAction" dto.AddressSnapshotId
                        |> Result.bind (
                            OrderSnapshotId.create
                            >> Result.mapError (fun message -> codecError "OrderAction" message)
                        )
                        |> Result.bind (fun snapshotId ->
                            if isNull dto.Shipments || dto.Shipments.Length <> 1 then
                                Error(codecError "OrderAction" "CreateShipment requires exactly one shipment.")
                            else
                                shipmentOfDto dto.Shipments[0]
                                |> Result.bind (fun shipment ->
                                    if
                                        shipment.Created
                                        || shipment.CreationFailed
                                        || shipment.Dispatched
                                        || shipment.Delivered
                                        || shipment.CaptureSucceeded
                                    then
                                        Error(codecError "OrderAction" "CreateShipment contains status flags.")
                                    else
                                        Ok(CreateShipment(shipment.Plan, snapshotId))))
                    | "request-capture-v1" when not (String.IsNullOrWhiteSpace dto.ProviderReference) ->
                        if isNull dto.Shipments || dto.Shipments.Length <> 1 then
                            Error(codecError "OrderAction" "RequestCapture requires exactly one allocation.")
                        else
                            shipmentOfDto dto.Shipments[0]
                            |> Result.bind (fun shipment ->
                                if
                                    shipment.Created
                                    || shipment.CreationFailed
                                    || shipment.Dispatched
                                    || shipment.Delivered
                                    || shipment.CaptureSucceeded
                                then
                                    Error(codecError "OrderAction" "RequestCapture contains status flags.")
                                else
                                    Ok(
                                        RequestCapture(
                                            shipment.Plan.ShipmentId,
                                            shipment.Plan.Allocation,
                                            shipment.Plan.CaptureId,
                                            shipment.Plan.PaymentOperationId,
                                            dto.ProviderReference
                                        )
                                    ))
                    | "start-return-v1" when not (isNull dto.Returns) && dto.Returns.Length = 1 ->
                        returnOfDto "OrderAction" dto.Returns[0]
                        |> Result.map (fun entry -> StartReturn entry.Request)
                    | "start-refund-v1" -> PaymentCodec.refundOfDto "OrderAction" dto.Refund |> Result.map StartRefund
                    | "request-invoice-v1" ->
                        guid "OrderAction" dto.SnapshotId
                        |> Result.bind (
                            OrderSnapshotId.create
                            >> Result.mapError (fun message -> codecError "OrderAction" message)
                        )
                        |> Result.map RequestInvoice
                    | tag -> Error(codecError "OrderAction" $"Unknown tag '{tag}'.")))

    let error: Codec<OrderActionError> =
        Codec.create
            (fun errorValue ->
                let tag =
                    match errorValue with
                    | OrderActionError.InvalidOrderEntityId -> "invalid-order-entity-id-v1"
                    | OrderActionError.SnapshotMissing -> "snapshot-missing-v1"
                    | OrderActionError.CallbackEncodingFailed -> "callback-encoding-v1"
                    | OrderActionError.ActionReceiptMismatch -> "action-receipt-mismatch-v1"
                    | OrderActionError.InvalidAction -> "invalid-action-v1"

                encode "OrderActionError" (empty tag))
            (fun json ->
                decode "OrderActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "invalid-order-entity-id-v1" -> Ok OrderActionError.InvalidOrderEntityId
                    | "snapshot-missing-v1" -> Ok OrderActionError.SnapshotMissing
                    | "callback-encoding-v1" -> Ok OrderActionError.CallbackEncodingFailed
                    | "action-receipt-mismatch-v1" -> Ok OrderActionError.ActionReceiptMismatch
                    | "invalid-action-v1" -> Ok OrderActionError.InvalidAction
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
            chartVersion Orders.ChartVersion
            initialState Orders.initialState
            store storeArg
            logger log
        }

    let buildWorker log context =
        build log (workerStore context :> IMachineStore<OrderId, OrderState, OrderEvent, OrderAction, OrderActionError>)

    let buildClient log context =
        build log (clientStore context :> IMachineStore<OrderId, OrderState, OrderEvent, OrderAction, OrderActionError>)
