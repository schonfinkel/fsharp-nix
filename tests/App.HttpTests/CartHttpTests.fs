namespace App.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open App
open App.Cart
open App.Database
open App.Domain
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Expecto
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.DependencyInjection
open Npgsql
open Oxpecker.Htmx

type CartHttpTests(fixture: PostgreSqlFixture) =
    let csrfToken (html: string) =
        let matched =
            Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")

        Assert.True(matched.Success, "Expected an antiforgery token.")
        matched.Groups[1].Value

    let postForm (client: HttpClient) (path: string) (fields: (string * string) list) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, path)
            request.Headers.Add(HxRequestHeader.Request, "true")
            let values = fields |> List.map (fun (key, value) -> KeyValuePair(key, value))
            request.Content <- new FormUrlEncodedContent(values)
            return! client.SendAsync request
        }

    let productOne = "00000000-0000-0000-0000-000000000001"

    member _.``guest cart adds an item and persists it via the cookie``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use client = factory.CreateClient()
            let! catalog = client.GetStringAsync "/catalog"
            let token = csrfToken catalog

            let! added =
                postForm
                    client
                    "/cart/items"
                    [ "productId", productOne
                      "quantity", "2"
                      "__RequestVerificationToken", token ]

            let! addedHtml = added.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, added.StatusCode)
            Assert.Contains("Espresso Beans", addedHtml)
            Assert.Contains("hx-swap-oob", addedHtml)
            Assert.Contains("cart-count", addedHtml)
            Assert.Contains("Added Espresso Beans", addedHtml)

            let! cart = client.GetStringAsync "/cart"
            Assert.Contains("Espresso Beans", cart)
            Assert.DoesNotContain("Your cart is empty", cart)
        }

    member _.``stale epoch mutation returns conflict``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use client = factory.CreateClient()
            let! catalog = client.GetStringAsync "/catalog"
            let token = csrfToken catalog

            use! added =
                postForm
                    client
                    "/cart/items"
                    [ "productId", productOne
                      "quantity", "1"
                      "__RequestVerificationToken", token ]

            let! _ = added.Content.ReadAsStringAsync()

            let! cleared = postForm client "/cart/clear" [ "epoch", "0"; "__RequestVerificationToken", token ]

            Assert.Equal(HttpStatusCode.Conflict, cleared.StatusCode)
        }

    member _.``catalog search finds products``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use client = factory.CreateClient()
            let! html = client.GetStringAsync "/catalog?q=espresso"
            Assert.Contains("Espresso Beans", html)
            Assert.DoesNotContain("Ceramic Mug", html)
        }

    member _.``catalog administration requires mfa``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            let! response = client.GetAsync "/admin/catalog"
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        }

    member _.``cart event stream starts with a refresh event``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use client = factory.CreateClient()
            use request = new HttpRequestMessage(HttpMethod.Get, "/cart/events")

            use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType.MediaType)
            use! stream = response.Content.ReadAsStreamAsync()
            use reader = new StreamReader(stream)
            let! eventLine = reader.ReadLineAsync()
            let! dataLine = reader.ReadLineAsync()
            Assert.Equal("event: cart-change", eventLine)
            Assert.Equal("data: refresh", dataLine)
        }

    member _.``merge saga converges a guest cart into the customer cart``() =
        task {
            use factory = new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString)

            let key =
                CapabilityHashKey.create "primary" (Array.init 32 byte)
                |> Result.defaultWith Assert.Fail

            let dataSource = NpgsqlDataSource.Create fixture.ConnectionString
            let capability = Capability.generate ()
            let guestGuid = Guid.NewGuid()
            let guestCartId = Cart.guestId guestGuid

            do!
                CartGuestCapabilities.issue
                    dataSource
                    key
                    (EntityId.value guestCartId)
                    capability
                    (DateTimeOffset.UtcNow.AddHours 1.)
                    CancellationToken.None

            let carts = factory.Services.GetRequiredService<CartMachineClient>()

            let line: CartLine =
                { ProductId = ProductId.create (Guid.Parse productOne) |> Result.defaultWith Assert.Fail
                  Sku = Sku.create "COFFEE-ESPRESSO" |> Result.defaultWith Assert.Fail
                  Name = NonEmptyString.create 200 "Espresso Beans" |> Result.defaultWith Assert.Fail
                  UnitPrice = Money.create 24.90m "USD" |> Result.defaultWith Assert.Fail
                  Quantity = Quantity.create 1 |> Result.defaultWith Assert.Fail }

            let! _ =
                Machine.send
                    carts.Carts
                    guestCartId
                    (EventEnvelope.create $"seed:{guestGuid}" (LineAdded(line, 0L)))
                    CancellationToken.None

            let customerCartId = Cart.customerId Guid.Empty
            let mergeId = Guid.NewGuid()

            let! _ =
                Machine.send
                    carts.Carts
                    guestCartId
                    (EventEnvelope.create $"merge:{mergeId}" (MergeRequested(mergeId, EntityId.value customerCartId)))
                    CancellationToken.None

            let deadline = DateTimeOffset.UtcNow.AddSeconds 15.
            let mutable converged = false

            while DateTimeOffset.UtcNow < deadline && not converged do
                let! snapshot = Machine.state carts.Carts customerCartId CancellationToken.None

                match snapshot with
                | Ok(Some state) ->
                    match state.State with
                    | CartState.Active cart when not cart.Lines.IsEmpty -> converged <- true
                    | _ -> ()
                | _ -> ()

                if not converged then
                    do! Task.Delay(TimeSpan.FromMilliseconds 100.)

            Assert.True(converged, "The merge saga did not converge the customer cart.")

            let deadline = DateTimeOffset.UtcNow.AddSeconds 15.
            let mutable revoked = false

            while DateTimeOffset.UtcNow < deadline && not revoked do
                let! resolved = CartGuestCapabilities.tryResolve dataSource key capability CancellationToken.None

                match resolved with
                | None -> revoked <- true
                | Some _ -> do! Task.Delay(TimeSpan.FromMilliseconds 100.)

            Assert.True(revoked, "The guest capability was not revoked after merge.")
        }
