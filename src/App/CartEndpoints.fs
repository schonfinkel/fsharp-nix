namespace App

open System
open System.Threading
open System.Threading.Tasks
open App.Cart
open App.Database
open App.Domain
open App.Views
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module CartSession =
    let CookieName = "fsnix.cart"

    let GuestLifetime = TimeSpan.FromDays 30.

    let guestCapability (context: HttpContext) : RawCapability option =
        match context.Request.Cookies.TryGetValue CookieName with
        | true, value -> Capability.tryParse value
        | false, _ -> None

    let private mintGuest (context: HttpContext) (ct: CancellationToken) : Task<CartId> =
        task {
            let key = context.GetService<CapabilityHashKey>()
            let dataSource = context.GetService<NpgsqlDataSource>()
            let capability = Capability.generate ()
            let cartId = Cart.guestId (Guid.NewGuid())
            let expiresAt = context.GetService<TimeProvider>().GetUtcNow() + GuestLifetime

            do! CartGuestCapabilities.issue dataSource key (EntityId.value cartId) capability expiresAt ct

            let options = CookieOptions()
            options.HttpOnly <- true
            options.SameSite <- SameSiteMode.Lax
            options.Secure <- context.GetService<IHostEnvironment>().IsProduction()
            options.Expires <- expiresAt
            options.Path <- "/"

            context.Response.Cookies.Append(CookieName, Capability.encode capability, options)
            return cartId
        }

    /// <summary>Resolves the current cart: the customer's canonical cart when authenticated,
    /// otherwise the guest cart named by the bearer capability cookie. A missing or revoked
    /// guest capability mints a fresh one and sets its cookie.</summary>
    let resolve (context: HttpContext) (ct: CancellationToken) : Task<CartId> =
        task {
            let users = context.GetService<UserManager<ApplicationUser>>()
            let! user = users.GetUserAsync context.User

            if not (isNull user) then
                return Cart.customerId user.Id
            else
                let key = context.GetService<CapabilityHashKey>()
                let dataSource = context.GetService<NpgsqlDataSource>()

                match guestCapability context with
                | Some capability ->
                    match! CartGuestCapabilities.tryResolve dataSource key capability ct with
                    | Some entity -> return entityId entity
                    | None -> return! mintGuest context ct
                | None -> return! mintGuest context ct
        }

    let clearCookie (context: HttpContext) =
        context.Response.Cookies.Delete(CookieName)

[<RequireQualifiedAccess>]
module CartEndpoints =

    let private readState (carts: CartMachineClient) (cartId: CartId) (ct: CancellationToken) : Task<CartState> =
        task {
            let! snapshot = Machine.state carts.Carts cartId ct

            return
                match snapshot with
                | Ok(Some state) -> state.State
                | _ -> Cart.initialState
        }

    let private modelOf (state: CartState) (error: string option) : CartModel =
        let lines, epoch =
            match state with
            | CartState.Active cart
            | CartState.Abandoned cart -> cart.Lines, cart.Epoch
            | CartState.MergeFrozen cart -> cart.Lines, cart.Epoch
            | CartState.Empty
            | CartState.MergedInto _
            | CartState.Converted -> [], Cart.InitialEpoch

        let lineModels =
            lines
            |> List.map (fun line ->
                { ProductId = ProductId.wireString line.ProductId
                  Sku = Sku.value line.Sku
                  Name = NonEmptyString.value line.Name
                  UnitPrice = Money.format line.UnitPrice
                  Quantity = Quantity.value line.Quantity })

        let count = lines |> List.sumBy (fun line -> Quantity.value line.Quantity)

        let total =
            match lines with
            | [] -> ""
            | first :: rest ->
                let subtotal =
                    rest
                    |> List.fold
                        (fun acc line ->
                            Money.add acc (Money.multiply line.UnitPrice (decimal (Quantity.value line.Quantity))))
                        (Money.multiply first.UnitPrice (decimal (Quantity.value first.Quantity)))

                Money.format subtotal

        { Epoch = epoch
          Lines = lineModels
          Count = count
          Total = total
          Error = error }

    let private renderMutation (context: HttpContext) (cartId: CartId) (message: string option) (status: int) : Task =
        task {
            Web.noStore context
            Web.varyHtmx context
            context.Response.StatusCode <- status

            let carts = context.GetService<CartMachineClient>()
            let! state = readState carts cartId context.RequestAborted
            let model = modelOf state None

            if Web.isHtmx context then
                let content =
                    Fragment() {
                        CartViews.fragment context model
                        CartViews.badge model.Count

                        match message with
                        | Some message -> CartViews.alert message
                        | None -> ()
                    }

                return! context.WriteHtmlView content
            else
                return! Web.redirect "/cart" context
        }

    let private eventEpoch (event: CartEvent) : CartEpoch option =
        match event with
        | LineAdded(_, epoch)
        | QuantityChanged(_, _, epoch)
        | LineRemoved(_, epoch)
        | Cleared epoch -> Some epoch
        | MergeRequested _
        | MergeSnapshotCaptured _
        | MergeApplied _
        | MergeFailed _
        | ApplyMerge _
        | AbandonmentTimerFired _ -> None

    let private send (context: HttpContext) (cartId: CartId) (event: CartEvent) (successMessage: string) : Task =
        task {
            let carts = context.GetService<CartMachineClient>()

            let conflictMessage =
                "The cart changed in another window; review your items and try again."

            // A stale epoch is rejected up front so it never reaches the machine as a
            // dead-lettered command; the rare read/send race still maps to 409 below.
            let mutable stale = false

            match eventEpoch event with
            | Some expected ->
                let! state = readState carts cartId context.RequestAborted

                match Cart.epochOf state with
                | Some current when current = expected -> ()
                | _ -> stale <- true
            | None -> ()

            if stale then
                return! renderMutation context cartId (Some conflictMessage) StatusCodes.Status409Conflict
            else
                let key = $"cart-command:%O{Guid.NewGuid()}"

                let! outcome = Machine.send carts.Carts cartId (EventEnvelope.create key event) context.RequestAborted

                match outcome with
                | Ok(CommandResult.Committed _) ->
                    return! renderMutation context cartId (Some successMessage) StatusCodes.Status200OK
                | Ok(CommandResult.Rejected _)
                | Ok(CommandResult.DeadLettered _) ->
                    return! renderMutation context cartId (Some conflictMessage) StatusCodes.Status409Conflict
                | _ -> return! renderMutation context cartId None StatusCodes.Status503ServiceUnavailable
        }

    let private field name (form: IFormCollection) =
        match form.TryGetValue name with
        | true, value -> string value
        | false, _ -> ""

    let private routeProductId (context: HttpContext) : ProductId option =
        context.TryGetRouteValue("productId")
        |> Option.bind (fun value ->
            match ProductId.tryParse value with
            | Ok id -> Some id
            | Error _ -> None)

    let page: EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                Web.varyHtmx context
                let carts = context.GetService<CartMachineClient>()
                let! cartId = CartSession.resolve context context.RequestAborted
                let! state = readState carts cartId context.RequestAborted
                let fragment = CartViews.fragment context (modelOf state None)

                if Web.isHtmx context then
                    return! context.WriteHtmlView fragment
                else
                    let content =
                        Fragment() {
                            SharedViews.cartEventStream
                            fragment
                        }

                    return! context.WriteHtmlView(SharedViews.layout context "Cart" content)
            }

    let addItem: EndpointHandler =
        fun context ->
            task {
                let! form = context.Request.ReadFormAsync context.RequestAborted
                let productIdText = field "productId" form
                let quantityText = field "quantity" form

                let invalid (message: string) =
                    task {
                        context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity
                        return! context.WriteHtmlView(p (class' = "error") { message })
                    }

                match ProductId.tryParse productIdText with
                | Error _ -> return! invalid "Choose a product to add."
                | Ok productId ->
                    match Int32.TryParse quantityText with
                    | false, _ -> return! invalid "Enter a valid quantity."
                    | true, raw ->
                        match Quantity.create raw with
                        | Error _ -> return! invalid "Enter a quantity between 1 and 999."
                        | Ok quantity ->
                            let catalog = context.GetService<CatalogStore>()
                            let! product = catalog.Get(ProductId.value productId, context.RequestAborted)

                            match product with
                            | None
                            | Some { Active = false } -> return! invalid "That product is not available."
                            | Some product ->
                                let line: CartLine =
                                    { ProductId = productId
                                      Sku = product.Sku
                                      Name = product.Name
                                      UnitPrice = product.UnitPrice
                                      Quantity = quantity }

                                let carts = context.GetService<CartMachineClient>()
                                let! cartId = CartSession.resolve context context.RequestAborted
                                let! state = readState carts cartId context.RequestAborted
                                let epoch = Cart.epochOf state |> Option.defaultValue Cart.InitialEpoch

                                return!
                                    send
                                        context
                                        cartId
                                        (LineAdded(line, epoch))
                                        $"Added {NonEmptyString.value product.Name}."
            }

    let updateItem: EndpointHandler =
        fun context ->
            task {
                let! form = context.Request.ReadFormAsync context.RequestAborted
                let quantityText = field "quantity" form
                let epochText = field "epoch" form

                match routeProductId context with
                | None ->
                    context.Response.StatusCode <- StatusCodes.Status404NotFound
                    return! context.WriteHtmlView(p (class' = "error") { "Item not found." })
                | Some productId ->
                    match Int32.TryParse quantityText, Int64.TryParse epochText with
                    | (false, _), _
                    | _, (false, _) ->
                        context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                        return! context.WriteHtmlView(p (class' = "error") { "Enter a valid quantity." })
                    | (true, raw), (true, epoch) ->
                        match Quantity.create raw with
                        | Error _ ->
                            context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                            return!
                                context.WriteHtmlView(p (class' = "error") { "Enter a quantity between 1 and 999." })
                        | Ok quantity ->
                            let carts = context.GetService<CartMachineClient>()
                            let! cartId = CartSession.resolve context context.RequestAborted

                            return!
                                send context cartId (QuantityChanged(productId, quantity, epoch)) "Updated your cart."
            }

    let removeItem: EndpointHandler =
        fun context ->
            task {
                let! form = context.Request.ReadFormAsync context.RequestAborted
                let epochText = field "epoch" form

                match routeProductId context with
                | None ->
                    context.Response.StatusCode <- StatusCodes.Status404NotFound
                    return! context.WriteHtmlView(p (class' = "error") { "Item not found." })
                | Some productId ->
                    match Int64.TryParse epochText with
                    | false, _ ->
                        context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                        return! context.WriteHtmlView(p (class' = "error") { "The request could not be processed." })
                    | true, epoch ->
                        let carts = context.GetService<CartMachineClient>()
                        let! cartId = CartSession.resolve context context.RequestAborted
                        return! send context cartId (LineRemoved(productId, epoch)) "Removed the item."
            }

    let clear: EndpointHandler =
        fun context ->
            task {
                let! form = context.Request.ReadFormAsync context.RequestAborted
                let epochText = field "epoch" form

                match Int64.TryParse epochText with
                | false, _ ->
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                    return! context.WriteHtmlView(p (class' = "error") { "The request could not be processed." })
                | true, epoch ->
                    let carts = context.GetService<CartMachineClient>()
                    let! cartId = CartSession.resolve context context.RequestAborted
                    return! send context cartId (Cleared epoch) "Cleared your cart."
            }

    /// <summary>Authenticated merge initiation: freezes the guest cart for the signed-in
    /// customer, then revokes the guest cookie. The saga proceeds asynchronously; the customer
    /// cart page refreshes via SSE once the snapshot has been applied.</summary>
    let merge: EndpointHandler =
        fun context ->
            task {
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! user = users.GetUserAsync context.User

                if isNull user then
                    return! Web.redirect "/account/login" context
                else
                    let customerCartId = Cart.customerId user.Id
                    let key = context.GetService<CapabilityHashKey>()
                    let dataSource = context.GetService<NpgsqlDataSource>()
                    let carts = context.GetService<CartMachineClient>()

                    let! resolved =
                        match CartSession.guestCapability context with
                        | Some capability ->
                            CartGuestCapabilities.tryResolve dataSource key capability context.RequestAborted
                        | None -> Task.FromResult None

                    let mutable merged = false

                    match resolved with
                    | Some entity ->
                        let guestCartId = entityId entity
                        let! state = readState carts guestCartId context.RequestAborted

                        let nonEmpty =
                            match state with
                            | CartState.Active cart -> not cart.Lines.IsEmpty
                            | CartState.Abandoned cart -> not cart.Lines.IsEmpty
                            | _ -> false

                        if nonEmpty then
                            let mergeId = Guid.NewGuid()
                            let mergeKey = $"cart-merge:%O{mergeId}"

                            let! _ =
                                Machine.send
                                    carts.Carts
                                    guestCartId
                                    (EventEnvelope.create
                                        mergeKey
                                        (MergeRequested(mergeId, EntityId.value customerCartId)))
                                    context.RequestAborted

                            merged <- true
                    | None -> ()

                    CartSession.clearCookie context

                    if merged then
                        context.Response.Headers[HxResponseHeader.Trigger] <- "cart-change"

                    return! Web.redirect "/cart" context
            }
