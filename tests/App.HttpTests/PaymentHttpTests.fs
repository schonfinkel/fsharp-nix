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
open App.Invoices
open App.Orders
open App.Payments
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Expecto
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.DependencyInjection
open Npgsql

type PaymentHttpTests(fixture: PostgreSqlFixture) =
    let customerId = Guid.Parse "10000000-0000-0000-0000-000000000001"
    let productId = Guid.Parse "00000000-0000-0000-0000-000000000001"

    let inputValue name (html: string) =
        let matched = Regex.Match(html, $"name=\"{name}\"[^>]*value=\"([^\"]+)\"")
        Assert.True(matched.Success, $"Expected the {name} input.")
        matched.Groups[1].Value

    let postForm (client: HttpClient) (path: string) fields =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, path)

            request.Content <-
                new FormUrlEncodedContent(fields |> List.map (fun (key, value) -> KeyValuePair(key, value)))

            return! client.SendAsync request
        }

    let seedCart (factory: AppFactory) =
        task {
            let carts = factory.Services.GetRequiredService<CartMachineClient>()

            let line: CartLine =
                { ProductId = ProductId.create productId |> Result.defaultWith Assert.Fail
                  Sku = Sku.create "COFFEE-ESPRESSO" |> Result.defaultWith Assert.Fail
                  Name = NonEmptyString.create 200 "Espresso Beans" |> Result.defaultWith Assert.Fail
                  UnitPrice = Money.create 24.90m "USD" |> Result.defaultWith Assert.Fail
                  Quantity = Quantity.create 1 |> Result.defaultWith Assert.Fail }

            let! submitted =
                Machine.send
                    carts.Carts
                    (Cart.customerId customerId)
                    (EventEnvelope.create $"payment-http-cart:{Guid.NewGuid():N}" (LineAdded(line, 0L)))
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

            let fields =
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

            let! response = postForm client "/checkout" fields
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
            return Guid.Parse orderKey, token
        }

    let waitForOrder (factory: AppFactory) orderId predicate =
        task {
            let orders = factory.Services.GetRequiredService<OrderMachineClient>()
            let deadline = DateTimeOffset.UtcNow.AddSeconds 20.
            let mutable found = None

            while DateTimeOffset.UtcNow < deadline && found.IsNone do
                match! Machine.state orders.Orders (Orders.orderId orderId) CancellationToken.None with
                | Ok(Some snapshot) when predicate snapshot.State -> found <- Some snapshot.State
                | _ -> do! Task.Delay 100

            return
                found
                |> Option.defaultWith (fun () -> Assert.Fail $"Order {orderId:D} did not reach the expected state.")
        }

    let waitForPayment (factory: AppFactory) paymentId predicate =
        task {
            let payments = factory.Services.GetRequiredService<PaymentMachineClient>()
            let deadline = DateTimeOffset.UtcNow.AddSeconds 20.
            let mutable found = None

            while DateTimeOffset.UtcNow < deadline && found.IsNone do
                match! Machine.state payments.Payments (Payments.paymentId paymentId) CancellationToken.None with
                | Ok(Some snapshot) when predicate snapshot.State -> found <- Some snapshot.State
                | _ -> do! Task.Delay 100

            return
                found
                |> Option.defaultWith (fun () -> Assert.Fail $"Payment {paymentId:D} did not reach the expected state.")
        }

    let waitForInvoice (factory: AppFactory) orderId =
        task {
            let invoices = factory.Services.GetRequiredService<InvoiceMachineClient>()

            let entity =
                OrderSnapshotId.create orderId
                |> Result.defaultWith Assert.Fail
                |> InvoiceId.ofSnapshot
                |> Invoices.invoiceEntityId

            let deadline = DateTimeOffset.UtcNow.AddSeconds 20.
            let mutable found = None

            while DateTimeOffset.UtcNow < deadline && found.IsNone do
                match! Machine.state invoices.Invoices entity CancellationToken.None with
                | Ok(Some snapshot) ->
                    match snapshot.State with
                    | Rendered(issued, _) -> found <- Some issued
                    | _ -> do! Task.Delay 100
                | _ -> do! Task.Delay 100

            return
                found
                |> Option.defaultWith (fun () -> Assert.Fail $"Order {orderId:D} was not invoiced.")
        }

    let authorize (client: HttpClient) orderId method =
        task {
            let! html = client.GetStringAsync $"/orders/{orderId:D}"
            let token = inputValue "__RequestVerificationToken" html
            let rawAttempt = inputValue "attempt" html

            let attempt =
                PaymentOperationId.tryParse rawAttempt |> Result.defaultWith Assert.Fail

            let! response =
                postForm
                    client
                    $"/orders/{orderId:D}/authorize"
                    [ "attempt", rawAttempt
                      "method", PaymentMethodReference.value method
                      "__RequestVerificationToken", token ]

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
            return attempt
        }

    let scalar convert sql =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)
            let! value = command.ExecuteScalarAsync()
            return convert value
        }

    member _.``pay step authorizes commits stock and rejects late cancellation``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = customerId)

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            do! seedCart factory
            let! orderId, checkoutToken = submitCheckout client

            let! _ =
                waitForOrder factory orderId (function
                    | AwaitingAuthorization _ -> true
                    | _ -> false)

            let! payPage = client.GetStringAsync $"/orders/{orderId:D}"
            let payToken = inputValue "__RequestVerificationToken" payPage
            let rawAttempt = inputValue "attempt" payPage

            let! foreignAttempt =
                postForm
                    client
                    $"/orders/{orderId:D}/authorize"
                    [ "attempt", $"authorize:v1:{Guid.NewGuid():D}:{Guid.NewGuid():D}"
                      "method", PaymentMethodReference.value PaymentMethodReference.Sandbox.Success
                      "__RequestVerificationToken", payToken ]

            Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignAttempt.StatusCode)

            let! unsupportedMethod =
                postForm
                    client
                    $"/orders/{orderId:D}/authorize"
                    [ "attempt", rawAttempt
                      "method", "vault-token-which-must-not-be-persisted"
                      "__RequestVerificationToken", payToken ]

            Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupportedMethod.StatusCode)

            let! attempt = authorize client orderId PaymentMethodReference.Sandbox.Success

            let! _ =
                waitForOrder factory orderId (function
                    | Placed _ -> true
                    | _ -> false)

            let! html = client.GetStringAsync $"/orders/{orderId:D}"
            Assert.Contains("Order placed", html)
            Assert.Contains("can no longer be cancelled", html)

            let! invoice = waitForInvoice factory orderId
            Assert.Equal($"order:{orderId:D}", invoice.Request.OrderId)
            Assert.Equal(1L, invoice.Number.Sequence)

            let! invoicedTotal =
                scalar
                    string
                    $"SELECT i.total_amount = s.total_amount AND i.number = 1 FROM fsnix.invoices i JOIN fsnix.order_snapshots s USING (snapshot_id) WHERE i.order_id='order:{orderId:D}'"

            Assert.Equal("True", invoicedTotal)

            let number = InvoiceNumber.display invoice.Number
            let invoiceId = InvoiceId.wireString invoice.Request.InvoiceId

            Assert.True(
                Text.RegularExpressions.Regex.IsMatch(number, "^INV-[0-9]{4}-(0[1-9]|1[0-2])-00000001$"),
                number
            )

            let! invoices = client.GetStringAsync "/invoices"
            Assert.Contains(number, invoices)
            Assert.Contains($"/invoices/{invoiceId}/pdf", invoices)

            let! orderPage = client.GetStringAsync $"/orders/{orderId:D}"
            Assert.Contains($"Invoice {number} (PDF)", orderPage)

            use! pdf = client.GetAsync $"/invoices/{invoiceId}/pdf"
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode)
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType.MediaType)
            Assert.Equal($"{number}.pdf", pdf.Content.Headers.ContentDisposition.FileName.Trim('"'))
            Assert.Contains("no-store", pdf.Headers.CacheControl.ToString())
            let! bytes = pdf.Content.ReadAsByteArrayAsync()
            Assert.True(Pdf.isPdf bytes, "Expected a PDF body.")

            let! stored =
                scalar
                    string
                    $"SELECT encode(sha256, 'hex') FROM fsnix.invoice_documents WHERE invoice_id='{invoiceId}'"

            Assert.Equal(Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData(bytes: byte array)), stored)

            let! admin = client.GetStringAsync "/admin/invoices"
            Assert.Contains(number, admin)
            Assert.Contains($"/admin/invoices/{invoiceId}/pdf", admin)

            use strangerFactory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = Guid.NewGuid())

            use stranger =
                strangerFactory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            let! foreign = stranger.GetAsync $"/invoices/{invoiceId}/pdf"
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode)

            use anonymousFactory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use anonymous =
                anonymousFactory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            let! adminAnonymous = anonymous.GetAsync "/admin/invoices"
            Assert.Equal(HttpStatusCode.Redirect, adminAnonymous.StatusCode)
            let! pdfAnonymous = anonymous.GetAsync $"/invoices/{invoiceId}/pdf"
            Assert.Equal(HttpStatusCode.Redirect, pdfAnonymous.StatusCode)

            let! onHand =
                scalar Convert.ToInt32 $"SELECT on_hand FROM fsnix.product_stock WHERE product_id='{productId:D}'"

            let! reserved =
                scalar Convert.ToInt32 $"SELECT reserved FROM fsnix.product_stock WHERE product_id='{productId:D}'"

            let! intent =
                scalar string $"SELECT status FROM fsnix.authorization_intents WHERE order_id='order:{orderId:D}'"

            let! operation =
                scalar
                    string
                    $"SELECT status FROM fsnix.payment_operations WHERE operation_id='{PaymentOperationId.value attempt}'"

            Assert.Equal(199, onHand)
            Assert.Equal(0, reserved)
            Assert.Equal("completed", intent)
            Assert.Equal("succeeded", operation)

            let gateway = factory.Services.GetRequiredService<SimulatedPaymentGateway>()
            Assert.Equal(1, gateway.AuthorizeCalls attempt)

            let! cancelled =
                postForm client $"/orders/{orderId:D}/cancel" [ "__RequestVerificationToken", checkoutToken ]

            Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode)

            let! state =
                waitForOrder factory orderId (function
                    | Placed _ -> true
                    | _ -> false)

            match state with
            | Placed _ -> ()
            | other -> Assert.Fail $"A late cancellation changed the order to %A{other}."
        }

    member _.``declined payment returns to the pay step and a retry succeeds``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = customerId)

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            do! seedCart factory
            let! orderId, _ = submitCheckout client

            let! _ =
                waitForOrder factory orderId (function
                    | AwaitingAuthorization _ -> true
                    | _ -> false)

            let! declinedAttempt = authorize client orderId PaymentMethodReference.Sandbox.Decline

            let! _ =
                waitForOrder factory orderId (function
                    | AwaitingAuthorization _ -> true
                    | _ -> false)

            let! approvedAttempt = authorize client orderId PaymentMethodReference.Sandbox.Success

            let! _ =
                waitForOrder factory orderId (function
                    | Placed _ -> true
                    | _ -> false)

            let gateway = factory.Services.GetRequiredService<SimulatedPaymentGateway>()
            Assert.Equal(1, gateway.AuthorizeCalls declinedAttempt)
            Assert.Equal(1, gateway.AuthorizeCalls approvedAttempt)

            let! failed =
                scalar
                    Convert.ToInt64
                    $"SELECT COUNT(*) FROM fsnix.payment_operations WHERE order_id='order:{orderId:D}' AND status='failed'"

            let! succeeded =
                scalar
                    Convert.ToInt64
                    $"SELECT COUNT(*) FROM fsnix.payment_operations WHERE order_id='order:{orderId:D}' AND status='succeeded'"

            Assert.Equal(1L, failed)
            Assert.Equal(1L, succeeded)
        }

    member _.``cancelling an unknown authorization resolves and unwinds payment``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = customerId)

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            do! seedCart factory
            let! orderId, _ = submitCheckout client

            let! _ =
                waitForOrder factory orderId (function
                    | AwaitingAuthorization _ -> true
                    | _ -> false)

            let! attempt = authorize client orderId PaymentMethodReference.Sandbox.Unknown

            let! _ =
                waitForPayment factory orderId (function
                    | AuthorizationUnknown _ -> true
                    | _ -> false)

            let! html = client.GetStringAsync $"/orders/{orderId:D}"
            let token = inputValue "__RequestVerificationToken" html

            let! cancelled = postForm client $"/orders/{orderId:D}/cancel" [ "__RequestVerificationToken", token ]

            Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode)

            let! _ =
                waitForOrder factory orderId (function
                    | Cancelled -> true
                    | _ -> false)

            let! onHand =
                scalar Convert.ToInt32 $"SELECT on_hand FROM fsnix.product_stock WHERE product_id='{productId:D}'"

            let! reserved =
                scalar Convert.ToInt32 $"SELECT reserved FROM fsnix.product_stock WHERE product_id='{productId:D}'"

            let! intent =
                scalar string $"SELECT status FROM fsnix.authorization_intents WHERE order_id='order:{orderId:D}'"

            let! operation =
                scalar
                    string
                    $"SELECT status FROM fsnix.payment_operations WHERE operation_id='{PaymentOperationId.value attempt}'"

            Assert.Equal(200, onHand)
            Assert.Equal(0, reserved)
            Assert.Equal("cancelled", intent)
            Assert.True(operation = "succeeded" || operation = "failed")

            let gateway = factory.Services.GetRequiredService<SimulatedPaymentGateway>()
            Assert.Equal(1, gateway.AuthorizeCalls attempt)
        }

    member private _.startUnknownAuthorization() =
        task {
            let factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, userId = customerId)

            let client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            do! seedCart factory
            let! orderId, _ = submitCheckout client

            let! _ =
                waitForOrder factory orderId (function
                    | AwaitingAuthorization _ -> true
                    | _ -> false)

            let! attempt = authorize client orderId PaymentMethodReference.Sandbox.Unknown

            let! _ =
                waitForPayment factory orderId (function
                    | AuthorizationUnknown _ -> true
                    | _ -> false)

            let operation = PaymentOperationId.value attempt

            let! scheduled =
                scalar
                    string
                    $"SELECT reconcile_machine || ':' || (reconcile_entity = 'payment:{orderId:D}') || ':' || (next_check_at > statement_timestamp()) FROM fsnix.payment_operations WHERE operation_id='{operation}' AND status='unknown'"

            Assert.Equal("payments:true:true", scheduled)
            return factory, client, orderId, operation
        }

    member this.``an unknown authorization reconciles without customer action``() =
        task {
            let! factory, client, orderId, operation = this.startUnknownAuthorization ()
            use factory = factory
            use client = client

            let! _ =
                scalar
                    ignore
                    $"UPDATE fsnix.payment_operations SET next_check_at = statement_timestamp() - interval '1 second' WHERE operation_id='{operation}'"

            let! settled =
                waitForPayment factory orderId (function
                    | Authorized _
                    | Declined _ -> true
                    | _ -> false)

            let! order =
                waitForOrder factory orderId (function
                    | Placed _
                    | AwaitingAuthorization _ -> true
                    | _ -> false)

            match settled, order with
            | Authorized _, Placed _
            | Declined _, AwaitingAuthorization _ -> ()
            | other -> Assert.Fail $"Payment and order disagree after reconciliation: %A{other}"

            let! row =
                scalar
                    string
                    $"SELECT status || ':' || checks || ':' || (next_check_at IS NULL) FROM fsnix.payment_operations WHERE operation_id='{operation}'"

            Assert.True(row.EndsWith(":1:true", StringComparison.Ordinal), row)
            Assert.True(not (row.StartsWith("unknown", StringComparison.Ordinal)), row)
        }

    member this.``exhausted reconciliation parks the payment for review``() =
        task {
            let! factory, client, orderId, operation = this.startUnknownAuthorization ()
            use factory = factory
            use client = client

            let! _ =
                scalar
                    ignore
                    $"UPDATE fsnix.payment_operations SET checks = 5, next_check_at = statement_timestamp() - interval '1 second' WHERE operation_id='{operation}'"

            let! _ =
                waitForPayment factory orderId (function
                    | PaymentState.ManualReview reason -> ReasonCode.value reason = "gateway-outcome-unknown"
                    | _ -> false)

            let! row =
                scalar
                    string
                    $"SELECT status || ':' || checks || ':' || (next_check_at IS NULL) FROM fsnix.payment_operations WHERE operation_id='{operation}'"

            Assert.Equal("unknown:6:true", row)
        }
