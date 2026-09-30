module App.HttpTests.Program

open App.Tests
open Expecto

let private tests =
    testList
        "HTTP"
        [ testList
              "htmx"
              [ DatabaseTest.isolated false "anonymous administration redirects" (fun fixture ->
                    HttpTests(fixture).``anonymous users are redirected away from feature administration`` ())
                DatabaseTest.isolated false "navigation and fragments" (fun fixture ->
                    HttpTests(fixture).``normal navigation returns a layout and HTMX returns a fragment`` ())
                DatabaseTest.isolated false "demo variants" (fun fixture ->
                    HttpTests(fixture).``demo endpoints expose both feature variants`` ())
                DatabaseTest.isolated false "feature event stream starts with refresh" (fun fixture ->
                    HttpTests(fixture).``feature event stream starts with a refresh event`` ())
                DatabaseTest.isolated false "antiforgery is required" (fun fixture ->
                    HttpTests(fixture).``admin scheduling requires antiforgery`` ())
                DatabaseTest.isolated false "successful schedule replaces card" (fun fixture ->
                    HttpTests(fixture).``schedule success replaces card and emits feature event`` ())
                DatabaseTest.isolated false "no-op schedule does not emit an event" (fun fixture ->
                    HttpTests(fixture).``no-op schedule replaces card without emitting a feature event`` ())
                DatabaseTest.isolated false "HTTP failures use deliberate statuses" (fun fixture ->
                    HttpTests(fixture).``validation missing flags and conflicts have deliberate statuses`` ()) ]
          testList
              "authentication"
              [ DatabaseTest.isolated false "bootstrap account" (fun fixture ->
                    AuthHttpTests(fixture)
                        .``bootstrap creates one account and rejects missing or duplicate configuration`` ())
                DatabaseTest.isolated false "password failures lock account" (fun fixture ->
                    AuthHttpTests(fixture).``repeated password failures lock the account without revealing why`` ())
                DatabaseTest.isolated false "TOTP enrollment and administration" (fun fixture ->
                    AuthHttpTests(fixture).``password session must enroll TOTP before feature administration`` ()) ]
          testList
              "account flows"
              [ DatabaseTest.isolated false "registration reaches awaiting-completion" (fun fixture ->
                    AccountFlowHttpTests(fixture)
                        .``registration flow reaches awaiting-completion with a protected email`` ())
                DatabaseTest.isolated false "resend advances the generation" (fun fixture ->
                    AccountFlowHttpTests(fixture)
                        .``resend advances the generation and produces a second protected email`` ())
                DatabaseTest.isolated false "expiry expires the flow" (fun fixture ->
                    AccountFlowHttpTests(fixture).``expiry fires after the deadline and expires the flow`` ())
                DatabaseTest.isolated false "transient smtp failure retries" (fun fixture ->
                    AccountFlowHttpTests(fixture)
                        .``transient smtp failure keeps the payload and a later pass delivers`` ())
                DatabaseTest.isolated false "permanent smtp failure fails the flow" (fun fixture ->
                    AccountFlowHttpTests(fixture)
                        .``permanent smtp failure dead-letters and moves the flow to delivery-failed`` ())
                DatabaseTest.isolated false "public registration and confirmation" (fun fixture ->
                    AccountFlowHttpTests(fixture).``public registration confirms email only on antiforgery post`` ())
                DatabaseTest.isolated false "password reset request privacy and idempotency" (fun fixture ->
                    AccountFlowHttpTests(fixture)
                        .``password-reset request is idempotent and uniform for known and unknown email`` ())
                DatabaseTest.isolated false "password reset completion" (fun fixture ->
                    AccountFlowHttpTests(fixture)
                        .``password-reset link changes password only after csrf-protected post`` ())
                DatabaseTest.isolated false "authenticated email change" (fun fixture ->
                    AccountFlowHttpTests(fixture).``authenticated email-change request confirms the new address`` ())
                DatabaseTest.isolated false "public resend idempotency" (fun fixture ->
                    AccountFlowHttpTests(fixture).``public resend is idempotent for the submitted key`` ()) ]
          testList
              "health"
              [ DatabaseTest.isolated false "public probes are minimal" (fun fixture ->
                    HealthHttpTests(fixture).``public health probes are minimal and no-store`` ())
                DatabaseTest.isolated false "operations are aggregate and MFA protected" (fun fixture ->
                    HealthHttpTests(fixture).``operational health requires MFA and renders only aggregates`` ()) ]
          testList
              "catalog and cart"
              [ DatabaseTest.isolated false "guest cart adds and persists" (fun fixture ->
                    CartHttpTests(fixture).``guest cart adds an item and persists it via the cookie`` ())
                DatabaseTest.isolated false "stale epoch mutation conflicts" (fun fixture ->
                    CartHttpTests(fixture).``stale epoch mutation returns conflict`` ())
                DatabaseTest.isolated false "catalog search finds products" (fun fixture ->
                    CartHttpTests(fixture).``catalog search finds products`` ())
                DatabaseTest.isolated false "catalog administration requires MFA" (fun fixture ->
                    CartHttpTests(fixture).``catalog administration requires mfa`` ())
                DatabaseTest.isolated false "cart event stream starts with refresh" (fun fixture ->
                    CartHttpTests(fixture).``cart event stream starts with a refresh event`` ())
                DatabaseTest.isolated false "merge saga converges into customer cart" (fun fixture ->
                    CartHttpTests(fixture).``merge saga converges a guest cart into the customer cart`` ()) ]
          testList
              "orders"
              [ DatabaseTest.isolated false "checkout reserves and replays" (fun fixture ->
                    OrderHttpTests(fixture).``checkout reserves stock waits for payment and replays`` ())
                DatabaseTest.isolated false "reservation failure" (fun fixture ->
                    OrderHttpTests(fixture).``checkout reports reservation failure without an authorization intent`` ())
                DatabaseTest.isolated false "cancellation and ownership" (fun fixture ->
                    OrderHttpTests(fixture)
                        .``customer cancellation releases reservations and denies another customer`` ()) ]
          testList
              "payments"
              [ DatabaseTest.isolated false "pay step places the order" (fun fixture ->
                    PaymentHttpTests(fixture).``pay step authorizes commits stock and rejects late cancellation`` ())
                DatabaseTest.isolated false "declined payment retries" (fun fixture ->
                    PaymentHttpTests(fixture).``declined payment returns to the pay step and a retry succeeds`` ())
                DatabaseTest.isolated false "unknown cancellation unwinds" (fun fixture ->
                    PaymentHttpTests(fixture).``cancelling an unknown authorization resolves and unwinds payment`` ())
                DatabaseTest.isolated false "unknown authorization reconciles" (fun fixture ->
                    PaymentHttpTests(fixture).``an unknown authorization reconciles without customer action`` ())
                DatabaseTest.isolated false "exhausted reconciliation parks payment" (fun fixture ->
                    PaymentHttpTests(fixture).``exhausted reconciliation parks the payment for review`` ())
                DatabaseTest.isolated false "lapsed authorization reviews order" (fun fixture ->
                    PaymentHttpTests(fixture).``a lapsed authorization sends the placed order to review`` ()) ] ]
    |> testSequenced

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
