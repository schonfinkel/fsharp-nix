namespace App

open System
open System.Text.Json
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open App.Cart
open App.Database
open App.Domain
open App.Orders
open App.Views
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open NodaMoney
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine

type CheckoutPricing =
    { ShippingAmount: decimal
      TaxRate: decimal }

[<RequireQualifiedAccess>]
module CheckoutPricing =
    let private decimalValue (configuration: IConfiguration) key fallback =
        match configuration[key] with
        | value when String.IsNullOrWhiteSpace value -> fallback
        | value ->
            match
                Decimal.TryParse(value, Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture)
            with
            | true, parsed -> parsed
            | _ -> invalidOp $"{key} must be an invariant decimal value."

    let load (configuration: IConfiguration) =
        let pricing =
            { ShippingAmount = decimalValue configuration "Checkout:ShippingAmount" 5m
              TaxRate = decimalValue configuration "Checkout:TaxRate" 0.08m }

        if pricing.ShippingAmount < 0m then
            invalidOp "Checkout:ShippingAmount cannot be negative."

        if pricing.TaxRate < 0m || pricing.TaxRate > 1m then
            invalidOp "Checkout:TaxRate must be between 0 and 1."

        pricing

[<RequireQualifiedAccess>]
module OrderEndpointSql =
    let lastDeliveredAt = Sql.load "Orders/last-delivered-at"

[<RequireQualifiedAccess>]
module OrderEndpoints =
    let private formValue (form: IFormCollection) name =
        match form.TryGetValue name with
        | true, value -> string value
        | _ -> ""

    let private lineIdFor (orderId: Guid) (productId: ProductId) =
        let bytes =
            SHA256.HashData(Encoding.UTF8.GetBytes $"{orderId:D}:{ProductId.wireString productId}")

        let id = Guid(bytes.AsSpan(0, 16))
        OrderLineId.create id |> Result.defaultWith invalidOp

    let private status state =
        match state with
        | Initial -> "Preparing order"
        | ReservationPending _ -> "Reserving stock"
        | AwaitingAuthorization _ -> "Awaiting payment"
        | PaymentPending _ -> "Authorizing payment"
        | StockCommitPending _ -> "Committing stock"
        | Placed _ -> "Order placed"
        | HeldForReview _ -> "Under operator review"
        | FulfilmentPending _ -> "Preparing shipment"
        | Processing _ -> "Processing"
        | PartiallyShipped _ -> "Partially shipped"
        | Shipped _ -> "Shipped"
        | OrderState.Delivered _ -> "Delivered"
        | CancellationCompensating _ -> "Refunding cancelled order"
        | CancelledAfterRefund _ -> "Cancelled and refunded"
        | CancellationPending _ -> "Cancelling order"
        | Cancelled -> "Cancelled"
        | ReservationFailed _ -> "Stock could not be reserved"
        | ManualReview _ -> "Under operator review"
        | Closed -> "Closed"

    let private orderTotal state =
        match state with
        | ReservationPending order -> Money.format order.Totals.Total
        | AwaitingAuthorization order -> Money.format order.Pending.Totals.Total
        | PaymentPending order -> Money.format order.Reserved.Pending.Totals.Total
        | StockCommitPending order -> Money.format order.Reserved.Pending.Totals.Total
        | Placed order -> Money.format order.Reserved.Pending.Totals.Total
        | HeldForReview order -> Money.format order.Fulfilment.PlacedOrder.Reserved.Pending.Totals.Total
        | FulfilmentPending order
        | Processing order
        | PartiallyShipped order
        | Shipped order
        | OrderState.Delivered order -> Money.format order.PlacedOrder.Reserved.Pending.Totals.Total
        | CancellationCompensating order
        | CancelledAfterRefund order -> Money.format order.PlacedOrder.Reserved.Pending.Totals.Total
        | CancellationPending order -> Money.format order.Order.Totals.Total
        | Initial
        | Cancelled
        | ReservationFailed _
        | ManualReview _
        | Closed -> ""

    let private canCancel state =
        match state with
        | ReservationPending _
        | AwaitingAuthorization _
        | PaymentPending _ -> true
        | Shipped order when order.Shipments |> List.forall (fun shipment -> not shipment.Delivered) -> true
        | Initial
        | StockCommitPending _
        | Placed _
        | HeldForReview _
        | FulfilmentPending _
        | Processing _
        | PartiallyShipped _
        | Shipped _
        | OrderState.Delivered _
        | CancellationCompensating _
        | CancelledAfterRefund _
        | CancellationPending _
        | Cancelled
        | ReservationFailed _
        | ManualReview _
        | Closed -> false

    let private tooLateToCancel state =
        match state with
        | Shipped order when order.Shipments |> List.forall (fun shipment -> not shipment.Delivered) -> false
        | StockCommitPending _
        | Placed _
        | HeldForReview _
        | FulfilmentPending _
        | Processing _
        | PartiallyShipped _
        | Shipped _
        | OrderState.Delivered _ -> true
        | CancellationCompensating _
        | CancelledAfterRefund _ -> true
        | _ -> false

    let sandboxMethods =
        [ PaymentMethodReference.Sandbox.Success, "Sandbox card (approves)"
          PaymentMethodReference.Sandbox.Decline, "Sandbox card (declines)"
          PaymentMethodReference.Sandbox.Unknown, "Sandbox card (unknown outcome)" ]
        |> List.map (fun (method, label) -> PaymentMethodReference.value method, label)

    let private sandboxMethodValues = sandboxMethods |> List.map fst |> Set.ofList

    let private operationBelongsToOrder (orderId: Guid) operationId =
        let value = PaymentOperationId.value operationId
        let prefix = $"authorize:v1:{orderId:D}:"

        value.StartsWith(prefix, StringComparison.Ordinal)
        && match Guid.TryParseExact(value[prefix.Length ..], "D") with
           | true, suffix -> suffix <> Guid.Empty
           | false, _ -> false

    let page: EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                let key = Guid.NewGuid().ToString("D")
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! user = users.GetUserAsync context.User

                let model =
                    { OrderKey = key
                      Email = if isNull user then "" else user.Email
                      Recipient = if isNull user then "" else user.UserName
                      Line1 = ""
                      Line2 = ""
                      City = ""
                      Region = ""
                      PostalCode = ""
                      CountryCode = "US"
                      Error = None }

                return! context.WriteHtmlView(CheckoutViews.page context model)
            }

    let submit: EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! user = users.GetUserAsync context.User

                if isNull user then
                    return! Web.redirect "/account/login" context
                else
                    let! form = context.Request.ReadFormAsync context.RequestAborted
                    let rawOrderKey = formValue form "orderKey"

                    match Guid.TryParseExact(rawOrderKey, "D") with
                    | false, _ ->
                        context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                        return!
                            context.WriteHtmlView(
                                p (class' = "error") { "Checkout expired. Return to your cart and try again." }
                            )
                    | true, orderGuid ->
                        let dataSource = context.GetService<NpgsqlDataSource>()
                        let! existing = OrderSnapshots.tryLoad dataSource orderGuid context.RequestAborted

                        let address =
                            OrderAddress.create
                                (formValue form "recipient")
                                (formValue form "line1")
                                (Some(formValue form "line2"))
                                (formValue form "city")
                                (Some(formValue form "region"))
                                (formValue form "postalCode")
                                (formValue form "countryCode")

                        match address with
                        | Error message ->
                            context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity
                            return! context.WriteHtmlView(p (class' = "error") { message })
                        | Ok address ->
                            let cartId = Cart.customerId user.Id
                            let carts = context.GetService<CartMachineClient>()
                            let! cartSnapshot = Machine.state carts.Carts cartId context.RequestAborted

                            let lines =
                                match cartSnapshot with
                                | Ok(Some snapshot) ->
                                    match snapshot.State with
                                    | CartState.Active cart
                                    | CartState.Abandoned cart -> cart.Lines
                                    | _ -> []
                                | _ -> []

                            if lines.IsEmpty then
                                match existing with
                                | Some snapshot when snapshot.CustomerId = user.Id ->
                                    return! Web.redirect $"/orders/{orderGuid:D}" context
                                | Some _ ->
                                    context.Response.StatusCode <- StatusCodes.Status409Conflict

                                    return!
                                        context.WriteHtmlView(
                                            p (class' = "error") { "This checkout key has already been used." }
                                        )
                                | None ->
                                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                                    return!
                                        context.WriteHtmlView(
                                            p (class' = "error") {
                                                "Your cart is empty or has already been checked out."
                                            }
                                        )
                            else
                                let catalog = context.GetService<CatalogStore>()
                                let mutable currentProducts = Map.empty

                                for line in lines do
                                    let! product = catalog.Get(ProductId.value line.ProductId, context.RequestAborted)

                                    match product with
                                    | Some product -> currentProducts <- currentProducts.Add(line.ProductId, product)
                                    | None -> ()

                                let mismatch =
                                    lines
                                    |> List.exists (fun line ->
                                        match currentProducts.TryFind line.ProductId with
                                        | Some product -> not product.Active || product.UnitPrice <> line.UnitPrice
                                        | None -> true)

                                if mismatch then
                                    context.Response.StatusCode <- StatusCodes.Status409Conflict

                                    return!
                                        context.WriteHtmlView(
                                            p (class' = "error") {
                                                "A product or price changed. Review your cart and submit checkout again."
                                            }
                                        )
                                else
                                    let orderLines =
                                        lines
                                        |> List.map (fun line ->
                                            let product = currentProducts[line.ProductId]

                                            { LineId = lineIdFor orderGuid line.ProductId
                                              ProductId = line.ProductId
                                              Sku = line.Sku
                                              Name = line.Name
                                              UnitPrice = line.UnitPrice
                                              Quantity = line.Quantity
                                              PriceVersion = product.PriceVersion })

                                    let pricing = context.GetService<CheckoutPricing>()

                                    let shipping =
                                        Money.create
                                            pricing.ShippingAmount
                                            (Money.currencyCode orderLines.Head.UnitPrice)
                                        |> Result.defaultWith invalidOp

                                    let! totalsResult =
                                        Task.FromResult(
                                            OrderPricing.compute
                                                (orderLines |> List.map (fun line -> line.UnitPrice, line.Quantity))
                                                shipping
                                                pricing.TaxRate
                                        )

                                    match totalsResult with
                                    | Error message ->
                                        context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity
                                        return! context.WriteHtmlView(p (class' = "error") { message })
                                    | Ok totals ->
                                        let snapshotId =
                                            OrderSnapshotId.create orderGuid |> Result.defaultWith invalidOp

                                        let orderId = Orders.orderId orderGuid
                                        let orderEntity = EntityId.value orderId

                                        let addressDto =
                                            {| recipient = NonEmptyString.value address.Recipient
                                               line1 = NonEmptyString.value address.Line1
                                               line2 = address.Line2
                                               city = NonEmptyString.value address.City
                                               region = address.Region
                                               postalCode = NonEmptyString.value address.PostalCode
                                               countryCode = address.CountryCode |}

                                        let linesDto =
                                            orderLines
                                            |> List.map (fun line ->
                                                {| lineId = OrderLineId.wireString line.LineId
                                                   productId = ProductId.wireString line.ProductId
                                                   sku = Sku.value line.Sku
                                                   name = NonEmptyString.value line.Name
                                                   amount = Money.wireAmount line.UnitPrice
                                                   currency = Money.currencyCode line.UnitPrice
                                                   quantity = Quantity.value line.Quantity
                                                   priceVersion = PriceVersion.value line.PriceVersion |})

                                        let snapshot =
                                            { SnapshotId = orderGuid
                                              OrderId = orderEntity
                                              CustomerId = user.Id
                                              CustomerEmail = formValue form "email"
                                              AddressJson = JsonSerializer.Serialize addressDto
                                              LinesJson = JsonSerializer.Serialize linesDto
                                              Subtotal = Money.amount totals.Subtotal
                                              Shipping = Money.amount totals.Shipping
                                              Tax = Money.amount totals.Tax
                                              Total = Money.amount totals.Total
                                              Currency = Money.currencyCode totals.Total }

                                        use connection = dataSource.CreateConnection()
                                        do! connection.OpenAsync context.RequestAborted
                                        use! transaction = connection.BeginTransactionAsync context.RequestAborted

                                        match!
                                            OrderSnapshots.insert connection transaction snapshot context.RequestAborted
                                        with
                                        | Error message ->
                                            do! transaction.RollbackAsync context.RequestAborted
                                            context.Response.StatusCode <- StatusCodes.Status409Conflict
                                            return! context.WriteHtmlView(p (class' = "error") { message })
                                        | Ok() -> do! transaction.CommitAsync context.RequestAborted

                                        let pending =
                                            { SnapshotId = snapshotId
                                              CustomerId = user.Id.ToString("D")
                                              CartId = EntityId.value cartId
                                              Lines = orderLines
                                              Totals = totals
                                              Generation = 1L }

                                        let orders = context.GetService<OrderMachineClient>()

                                        let! outcome =
                                            Machine.send
                                                orders.Orders
                                                orderId
                                                (EventEnvelope.create
                                                    $"checkout:v1:{orderGuid:D}"
                                                    (OrderSubmitted pending))
                                                context.RequestAborted

                                        match outcome with
                                        | Ok(CommandResult.Committed _) ->
                                            return! Web.redirect $"/orders/{orderGuid:D}" context
                                        | Ok(CommandResult.Rejected _)
                                        | Ok(CommandResult.DeadLettered _) ->
                                            context.Response.StatusCode <- StatusCodes.Status409Conflict

                                            return!
                                                context.WriteHtmlView(
                                                    p (class' = "error") {
                                                        "This checkout could not be accepted. Review your cart and retry."
                                                    }
                                                )
                                        | _ ->
                                            context.Response.StatusCode <- StatusCodes.Status503ServiceUnavailable
                                            context.Response.Headers["Retry-After"] <- "2"

                                            return!
                                                context.WriteHtmlView(
                                                    p (class' = "error") { "Checkout is temporarily unavailable." }
                                                )
            }

    let show: EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                let idText = context.TryGetRouteValue("orderId") |> Option.defaultValue ""

                match Guid.TryParseExact(idText, "D") with
                | false, _ ->
                    context.Response.StatusCode <- 404
                    return! context.WriteHtmlView(p () { "Order not found." })
                | true, guid ->
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let! user = users.GetUserAsync context.User
                    let orderId = Orders.orderId guid
                    let dataSource = context.GetService<NpgsqlDataSource>()

                    let! owned =
                        if isNull user then
                            Task.FromResult false
                        else
                            OrderSnapshots.isOwnedBy dataSource (EntityId.value orderId) user.Id context.RequestAborted

                    if not owned then
                        context.Response.StatusCode <- 404
                        return! context.WriteHtmlView(p () { "Order not found." })
                    else
                        let orders = context.GetService<OrderMachineClient>()
                        let! snapshot = Machine.state orders.Orders orderId context.RequestAborted

                        let! invoice =
                            InvoiceQueries.tryForOrder
                                dataSource
                                (EntityId.value orderId)
                                user.Id
                                context.RequestAborted

                        match snapshot with
                        | Ok(Some order) ->
                            let pay =
                                match order.State with
                                | AwaitingAuthorization _ ->
                                    Some
                                        { Attempt =
                                            $"authorize:v1:{guid:D}:{Guid.NewGuid():D}"
                                            |> PaymentOperationId.create
                                            |> Result.defaultWith invalidOp
                                            |> PaymentOperationId.value
                                          Methods = sandboxMethods }
                                | _ -> None

                            let model =
                                let returnLines, returnStatuses =
                                    match order.State with
                                    | OrderState.Delivered fulfilment ->
                                        let reserved lineId =
                                            fulfilment.Returns
                                            |> List.filter (fun entry -> entry.Status <> "rejected")
                                            |> List.collect (fun entry -> entry.Request.Lines)
                                            |> List.filter (fun line -> line.OrderLineId = lineId)
                                            |> List.sumBy _.Quantity

                                        let availableLines =
                                            fulfilment.PlacedOrder.Reserved.Pending.Lines
                                            |> List.choose (fun line ->
                                                let available = Quantity.value line.Quantity - reserved line.LineId

                                                if available <= 0 then
                                                    None
                                                else
                                                    Some
                                                        { LineId = OrderLineId.wireString line.LineId
                                                          Name = NonEmptyString.value line.Name
                                                          Available = available
                                                          ReturnKey = Guid.NewGuid().ToString("D") })

                                        let statuses =
                                            fulfilment.Returns
                                            |> List.map (fun entry ->
                                                ReturnId.wireString entry.Request.ReturnId, entry.Status)

                                        availableLines, statuses
                                    | _ -> [], []

                                { OrderId = guid.ToString("D")
                                  Status = status order.State
                                  Total = orderTotal order.State
                                  CanCancel = canCancel order.State
                                  TooLateToCancel = tooLateToCancel order.State
                                  Pay = pay
                                  ReturnLines = returnLines
                                  ReturnStatuses = returnStatuses
                                  Invoice =
                                    invoice
                                    |> Option.filter (fun (_, _, hasDocument) -> hasDocument)
                                    |> Option.map (fun (id, number, _) ->
                                        $"/invoices/{InvoiceId.wireString id}/pdf", InvoiceNumber.display number)
                                  Error = None }

                            return! context.WriteHtmlView(CheckoutViews.orderPage context model)
                        | _ ->
                            context.Response.StatusCode <- 404
                            return! context.WriteHtmlView(p () { "Order not found." })
            }

    let authorize: EndpointHandler =
        fun context ->
            task {
                let idText = context.TryGetRouteValue("orderId") |> Option.defaultValue ""

                match Guid.TryParseExact(idText, "D") with
                | false, _ ->
                    context.Response.StatusCode <- 404
                    return! context.WriteHtmlView(p () { "Order not found." })
                | true, guid ->
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let! user = users.GetUserAsync context.User
                    let orderId = Orders.orderId guid
                    let dataSource = context.GetService<NpgsqlDataSource>()

                    let! owned =
                        if isNull user then
                            Task.FromResult false
                        else
                            OrderSnapshots.isOwnedBy dataSource (EntityId.value orderId) user.Id context.RequestAborted

                    if not owned then
                        context.Response.StatusCode <- 404
                        return! context.WriteHtmlView(p () { "Order not found." })
                    else
                        let! form = context.Request.ReadFormAsync context.RequestAborted

                        match
                            (formValue form "attempt" |> PaymentOperationId.tryParse,
                             formValue form "method" |> PaymentMethodReference.tryParse)
                        with
                        | Ok attempt, Ok method when
                            operationBelongsToOrder guid attempt
                            && sandboxMethodValues.Contains(PaymentMethodReference.value method)
                            ->
                            let orders = context.GetService<OrderMachineClient>()

                            let! outcome =
                                Machine.send
                                    orders.Orders
                                    orderId
                                    (EventEnvelope.create
                                        (PaymentOperationId.value attempt)
                                        (AuthorizePaymentRequested(method, attempt)))
                                    context.RequestAborted

                            match outcome with
                            | Ok(CommandResult.Committed _)
                            | Ok(CommandResult.Rejected _) -> return! Web.redirect $"/orders/{guid:D}" context
                            | Ok(CommandResult.DeadLettered _) ->
                                context.Response.StatusCode <- StatusCodes.Status409Conflict

                                return!
                                    context.WriteHtmlView(
                                        p (class' = "error") { "This payment request is no longer valid." }
                                    )
                            | _ ->
                                context.Response.StatusCode <- StatusCodes.Status503ServiceUnavailable
                                context.Response.Headers["Retry-After"] <- "2"

                                return!
                                    context.WriteHtmlView(
                                        p (class' = "error") { "Payment authorization is temporarily unavailable." }
                                    )
                        | _ ->
                            context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                            return!
                                context.WriteHtmlView(p (class' = "error") { "Choose a payment method and try again." })
            }

    let cancel: EndpointHandler =
        fun context ->
            task {
                let idText = context.TryGetRouteValue("orderId") |> Option.defaultValue ""

                match Guid.TryParseExact(idText, "D") with
                | false, _ ->
                    context.Response.StatusCode <- 404
                    return! context.WriteHtmlView(p () { "Order not found." })
                | true, guid ->
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let! user = users.GetUserAsync context.User
                    let orderId = Orders.orderId guid
                    let dataSource = context.GetService<NpgsqlDataSource>()

                    let! owned =
                        if isNull user then
                            Task.FromResult false
                        else
                            OrderSnapshots.isOwnedBy dataSource (EntityId.value orderId) user.Id context.RequestAborted

                    if not owned then
                        context.Response.StatusCode <- 404
                        return! context.WriteHtmlView(p () { "Order not found." })
                    else
                        let orders = context.GetService<OrderMachineClient>()

                        let! _ =
                            Machine.send
                                orders.Orders
                                orderId
                                (EventEnvelope.create $"cancel:v1:{guid:D}" CancelRequested)
                                context.RequestAborted

                        return! Web.redirect $"/orders/{guid:D}" context
            }

    let requestReturn: EndpointHandler =
        fun context ->
            task {
                let idText = context.TryGetRouteValue("orderId") |> Option.defaultValue ""

                match Guid.TryParseExact(idText, "D") with
                | false, _ ->
                    context.Response.StatusCode <- StatusCodes.Status404NotFound
                    return! context.WriteHtmlView(p () { "Order not found." })
                | true, guid ->
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let! user = users.GetUserAsync context.User
                    let orderId = Orders.orderId guid
                    let dataSource = context.GetService<NpgsqlDataSource>()

                    let! owned =
                        if isNull user then
                            Task.FromResult false
                        else
                            OrderSnapshots.isOwnedBy dataSource (EntityId.value orderId) user.Id context.RequestAborted

                    if not owned then
                        context.Response.StatusCode <- StatusCodes.Status404NotFound
                        return! context.WriteHtmlView(p () { "Order not found." })
                    else
                        let! form = context.Request.ReadFormAsync context.RequestAborted
                        let lineId = formValue form "lineId"
                        let quantity = formValue form "quantity"
                        let key = formValue form "returnKey"
                        let orders = context.GetService<OrderMachineClient>()
                        let! snapshot = Machine.state orders.Orders orderId context.RequestAborted

                        match
                            snapshot, ReturnId.tryParse key, Int32.TryParse quantity, Guid.TryParseExact(lineId, "D")
                        with
                        | Ok(Some order), Ok returnId, (true, amount), (true, parsedLineId) when amount > 0 ->
                            match order.State with
                            | OrderState.Delivered fulfilment ->
                                match
                                    fulfilment.PlacedOrder.Reserved.Pending.Lines
                                    |> List.tryFind (fun line -> OrderLineId.value line.LineId = parsedLineId)
                                with
                                | None ->
                                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity
                                    return! context.WriteHtmlView(p (class' = "error") { "Choose an order line." })
                                | Some line ->
                                    use connection = dataSource.CreateConnection()
                                    do! connection.OpenAsync context.RequestAborted

                                    use cmd = new NpgsqlCommand(OrderEndpointSql.lastDeliveredAt, connection)

                                    cmd.Parameters.AddWithValue("order", EntityId.value orderId) |> ignore
                                    let! delivered = cmd.ExecuteScalarAsync context.RequestAborted

                                    if isNull delivered || delivered = DBNull.Value then
                                        context.Response.StatusCode <- StatusCodes.Status409Conflict

                                        return!
                                            context.WriteHtmlView(p (class' = "error") { "Delivery has not settled." })
                                    else
                                        let deadline = (delivered :?> DateTimeOffset).AddDays 30.

                                        if context.GetService<TimeProvider>().GetUtcNow() > deadline then
                                            context.Response.StatusCode <- StatusCodes.Status409Conflict

                                            return!
                                                context.WriteHtmlView(
                                                    p (class' = "error") { "The return window has ended." }
                                                )
                                        else
                                            let totals = fulfilment.PlacedOrder.Reserved.Pending.Totals
                                            let merchandise = Money.multiply line.UnitPrice (decimal amount)

                                            let tax =
                                                if Money.amount totals.Subtotal = 0m then
                                                    Money.zero (Money.currencyCode merchandise)
                                                    |> Result.defaultWith invalidOp
                                                else
                                                    Money.multiply
                                                        totals.Tax
                                                        (Money.amount merchandise / Money.amount totals.Subtotal)

                                            let request =
                                                { ReturnId = returnId
                                                  AuthorizationId =
                                                    ReturnAuthorizationId.create (ReturnId.value returnId)
                                                    |> Result.defaultWith invalidOp
                                                  OrderId = EntityId.value orderId
                                                  Currency = Money.currencyCode merchandise
                                                  WindowEndsAt = deadline
                                                  Lines =
                                                    [ { OrderLineId = line.LineId
                                                        Quantity = amount
                                                        Merchandise = merchandise
                                                        Tax = tax } ] }

                                            let! outcome =
                                                Machine.send
                                                    orders.Orders
                                                    orderId
                                                    (EventEnvelope.create
                                                        $"return:{guid:D}:{ReturnId.wireString returnId}"
                                                        (ReturnRequested request))
                                                    context.RequestAborted

                                            match outcome with
                                            | Ok(CommandResult.Committed _) ->
                                                return! Web.redirect $"/orders/{guid:D}" context
                                            | _ ->
                                                context.Response.StatusCode <- StatusCodes.Status409Conflict

                                                return!
                                                    context.WriteHtmlView(
                                                        p (class' = "error") {
                                                            "This quantity is not available for return."
                                                        }
                                                    )
                            | _ ->
                                context.Response.StatusCode <- StatusCodes.Status409Conflict
                                return! context.WriteHtmlView(p (class' = "error") { "The order is not delivered." })
                        | _ ->
                            context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity
                            return! context.WriteHtmlView(p (class' = "error") { "Enter a valid return quantity." })
            }
