namespace App.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open App
open App.Cart
open App.Database
open App.Domain
open App.Orders
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Expecto
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.DependencyInjection
open Npgsql

type OrderHttpTests(fixture: PostgreSqlFixture) =
    let customerId = Guid.Parse "10000000-0000-0000-0000-000000000001"
    let productId = Guid.Parse "00000000-0000-0000-0000-000000000001"

    let inputValue name (html: string) =
        let matched = Regex.Match(html, $"name=\"{name}\"[^>]*value=\"([^\"]+)\"")
        Assert.True(matched.Success, $"Expected the {name} input.")
        matched.Groups[1].Value

    let checkoutFields token orderKey =
        [ "orderKey", orderKey
          "email", "test.operator@example.test"
          "recipient", "Test Operator"
          "line1", "1 Test Street"
          "line2", ""
          "city", "Portland"
          "region", "OR"
          "postalCode", "97201"
          "countryCode", "US"
          "__RequestVerificationToken", token ]

    let postForm (client: HttpClient) (path: string) fields =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, path)

            request.Content <-
                new FormUrlEncodedContent(fields |> List.map (fun (key, value) -> KeyValuePair(key, value)))

            return! client.SendAsync request
        }

    let seedCart (factory: AppFactory) quantity =
        task {
            let carts = factory.Services.GetRequiredService<CartMachineClient>()

            let line: CartLine =
                { ProductId = ProductId.create productId |> Result.defaultWith Assert.Fail
                  Sku = Sku.create "COFFEE-ESPRESSO" |> Result.defaultWith Assert.Fail
                  Name = NonEmptyString.create 200 "Espresso Beans" |> Result.defaultWith Assert.Fail
                  UnitPrice = Money.create 24.90m "USD" |> Result.defaultWith Assert.Fail
                  Quantity = Quantity.create quantity |> Result.defaultWith Assert.Fail }

            let! submitted =
                Machine.send
                    carts.Carts
                    (Cart.customerId customerId)
                    (EventEnvelope.create $"order-http-cart:{Guid.NewGuid():N}" (LineAdded(line, 0L)))
                    CancellationToken.None

            match submitted with
            | Ok(CommandResult.Committed _) -> ()
            | result -> Assert.Fail $"Could not seed the customer cart: %A{result}"
        }

    let submitCheckout (client: HttpClient) =
        task {
            let! checkout = client.GetStringAsync "/checkout"
            let token = inputValue "__RequestVerificationToken" checkout
            let orderKey = inputValue "orderKey" checkout
            let fields = checkoutFields token orderKey
            let! response = postForm client "/checkout" fields
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
            Assert.Equal($"/orders/{orderKey}", response.Headers.Location.OriginalString)
            return Guid.Parse orderKey, token, fields
        }

    let waitForOrder (factory: AppFactory) orderId expected =
        task {
            let orders = factory.Services.GetRequiredService<OrderMachineClient>()
            let deadline = DateTimeOffset.UtcNow.AddSeconds 15.
            let mutable state = None

            while DateTimeOffset.UtcNow < deadline && state.IsNone do
                match! Machine.state orders.Orders (Orders.orderId orderId) CancellationToken.None with
                | Ok(Some snapshot) when expected snapshot.State -> state <- Some snapshot.State
                | _ -> do! Task.Delay 100

            match state with
            | Some value -> return value
            | None -> return Assert.Fail $"Order {orderId:D} did not reach the expected state."
        }

    let scalar convert sql =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)
            let! value = command.ExecuteScalarAsync()
            return convert value
        }

    let waitForScalar expected convert sql =
        task {
            let deadline = DateTimeOffset.UtcNow.AddSeconds 15.
            let mutable actual = Unchecked.defaultof<_>
            let mutable matched = false

            while DateTimeOffset.UtcNow < deadline && not matched do
                let! value = scalar convert sql
                actual <- value
                matched <- value = expected

                if not matched then
                    do! Task.Delay 100

            if not matched then
                Assert.Fail $"Database value did not reach %A{expected}; last value was %A{actual}."

            return actual
        }

    member _.``checkout reserves stock waits for payment and replays``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = customerId)

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            do! seedCart factory 1

            let! orderId, _, fields = submitCheckout client

            let! _ =
                waitForOrder factory orderId (function
                    | AwaitingAuthorization _ -> true
                    | _ -> false)

            let dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>()
            let! persistedBeforeReplay = OrderSnapshots.tryLoad dataSource orderId CancellationToken.None

            match persistedBeforeReplay with
            | Some value -> Assert.Equal(customerId, value.CustomerId)
            | None -> Assert.Fail "The checkout snapshot was missing before replay."

            use! replay = postForm client "/checkout" fields
            let! replayBody = replay.Content.ReadAsStringAsync()

            if replay.StatusCode <> HttpStatusCode.Redirect then
                Assert.Fail $"Expected checkout replay to redirect, but received {replay.StatusCode}: {replayBody}"

            Assert.Equal($"/orders/{orderId:D}", replay.Headers.Location.OriginalString)

            let! snapshot = OrderSnapshots.tryLoad dataSource orderId CancellationToken.None

            match snapshot with
            | Some value ->
                Assert.Equal(24.90m, value.Subtotal)
                Assert.Equal(5m, value.Shipping)
                Assert.Equal(32.29m, value.Total)
            | None -> Assert.Fail "The checkout did not persist its immutable snapshot."

            let! reserved =
                scalar
                    (fun value -> value :?> int)
                    $"SELECT reserved FROM fsnix.product_stock WHERE product_id = '{productId:D}'"

            let! intents =
                scalar
                    (fun value -> value :?> int64)
                    $"SELECT COUNT(*) FROM fsnix.authorization_intents WHERE order_id = 'order:{orderId:D}'"

            Assert.Equal(1, reserved)
            Assert.Equal(0L, intents)
        }

    member _.``checkout reports reservation failure without an authorization intent``() =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    $"UPDATE fsnix.product_stock SET on_hand = 0 WHERE product_id = '{productId:D}'",
                    connection
                )

            let! _ = command.ExecuteNonQueryAsync()

            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = customerId)

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            do! seedCart factory 1
            let! orderId, _, _ = submitCheckout client

            let! _ =
                waitForOrder factory orderId (function
                    | ReservationFailed _ -> true
                    | _ -> false)

            let! response = client.GetAsync $"/orders/{orderId:D}"
            let! html = response.Content.ReadAsStringAsync()

            let! intents =
                scalar
                    (fun value -> value :?> int64)
                    $"SELECT COUNT(*) FROM fsnix.authorization_intents WHERE order_id = 'order:{orderId:D}'"

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Contains("Stock could not be reserved", html)
            Assert.Equal(0L, intents)
        }

    member _.``customer cancellation releases reservations and denies another customer``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = customerId)

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            do! seedCart factory 1
            let! orderId, _, _ = submitCheckout client

            let! _ =
                waitForOrder factory orderId (function
                    | AwaitingAuthorization _ -> true
                    | _ -> false)

            let otherCustomer = Guid.NewGuid()
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use insert =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.users(id,username,normalized_username,email,normalized_email,email_confirmed,security_stamp,concurrency_stamp) VALUES(@id,'other','OTHER','other@example.test','OTHER@EXAMPLE.TEST',TRUE,'security','concurrency')",
                    connection
                )

            insert.Parameters.AddWithValue("id", otherCustomer) |> ignore
            let! _ = insert.ExecuteNonQueryAsync()

            use otherFactory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = otherCustomer)

            use otherClient =
                otherFactory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            let! denied = otherClient.GetAsync $"/orders/{orderId:D}"
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode)

            let! statusHtml = client.GetStringAsync $"/orders/{orderId:D}"
            let cancelToken = inputValue "__RequestVerificationToken" statusHtml
            use! cancelled = postForm client $"/orders/{orderId:D}/cancel" [ "__RequestVerificationToken", cancelToken ]
            Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode)

            let! _ =
                waitForOrder factory orderId (function
                    | Cancelled -> true
                    | _ -> false)

            let! reserved =
                scalar
                    (fun value -> value :?> int)
                    $"SELECT reserved FROM fsnix.product_stock WHERE product_id = '{productId:D}'"

            let! intents =
                scalar
                    (fun value -> value :?> int64)
                    $"SELECT COUNT(*) FROM fsnix.authorization_intents WHERE order_id = 'order:{orderId:D}'"

            Assert.Equal(0, reserved)
            Assert.Equal(0L, intents)
        }
