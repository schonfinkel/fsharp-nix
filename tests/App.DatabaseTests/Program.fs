module App.DatabaseTests.Program

open App.Tests
open Expecto

let private tests =
    testList
        "database"
        [ testList
              "postgresql"
              [ DatabaseTest.isolated false "migration seeds definitions and is idempotent" (fun fixture ->
                    PostgresTests(fixture).``migration seeds current definitions and is idempotent`` ())
                DatabaseTest.isolated true "production excludes test migrations" (fun fixture ->
                    PostgresTests(fixture).``production excludes test migrations and still applies repeatables`` ())
                DatabaseTest.isolated false "schedules split intervals" (fun fixture ->
                    PostgresTests(fixture).``immediate and future schedules split intervals`` ())
                DatabaseTest.isolated false "no-op scheduling has no effects" (fun fixture ->
                    PostgresTests(fixture).``no-op scheduling neither writes nor publishes a notification`` ())
                DatabaseTest.isolated false "earlier schedules preserve later boundaries" (fun fixture ->
                    PostgresTests(fixture).``earlier scheduling preserves an existing later boundary`` ())
                DatabaseTest.isolated false "duplicate boundaries conflict" (fun fixture ->
                    PostgresTests(fixture).``duplicate boundaries map to temporal conflict`` ())
                DatabaseTest.isolated false "concurrent duplicate schedules produce one change" (fun fixture ->
                    PostgresTests(fixture).``concurrent duplicate schedules produce one change`` ())
                DatabaseTest.isolated false "database rejects invalid ranges" (fun fixture ->
                    PostgresTests(fixture).``database rejects overlapping and empty ranges`` ()) ]
          testList
              "automata"
              [ DatabaseTest.isolated false "probe loop commits effect receipt and callback" (fun fixture ->
                    ProbeTests.run fixture)
                DatabaseTest.isolated false "outbox relay recovers a committed effect after a crash" (fun fixture ->
                    ProbeTests.crashRecovery fixture)
                DatabaseTest.isolated false "action receipts reject a mismatched payload" (fun fixture ->
                    ProbeTests.receiptVerification fixture)
                DatabaseTest.isolated false "outbox isolates destination exceptions" (fun fixture ->
                    ProbeTests.destinationExceptionsAreIsolated fixture) ]
          testList
              "email outbox"
              [ DatabaseTest.isolated false "claim leases and hides from competitors" (fun fixture ->
                    EmailOutboxTests(fixture).``claim leases a batch and hides it from competing owners`` ())
                DatabaseTest.isolated false "sent settlement erases payload" (fun fixture ->
                    EmailOutboxTests(fixture)
                        .``settle sent erases the payload and enqueues the generation callback`` ())
                DatabaseTest.isolated false "settlement fenced by lease owner" (fun fixture ->
                    EmailOutboxTests(fixture).``settlement is fenced by the claiming lease owner`` ())
                DatabaseTest.isolated false "retryable failure keeps payload" (fun fixture ->
                    EmailOutboxTests(fixture).``retryable failure releases with backoff and keeps the payload`` ())
                DatabaseTest.isolated false "permanent failure dead-letters" (fun fixture ->
                    EmailOutboxTests(fixture)
                        .``permanent failure dead-letters erases the payload and notifies the flow`` ())
                DatabaseTest.isolated false "attempt exhaustion dead-letters" (fun fixture ->
                    EmailOutboxTests(fixture).``exhausted attempts dead-letter even a retryable classification`` ())
                DatabaseTest.isolated false "terminal rows cannot retain payload" (fun fixture ->
                    EmailOutboxTests(fixture).``the database rejects terminal rows that retain a payload`` ()) ]
          testList
              "account-flow requests"
              [ DatabaseTest.isolated false "request idempotency replay and payload conflict" (fun fixture ->
                    AccountFlowRequestTests(fixture).``same key and payload replay one durable request`` ())
                DatabaseTest.isolated false "new request supersedes active flow" (fun fixture ->
                    AccountFlowRequestTests(fixture).``new key supersedes the active flow and cancels its deadline`` ())
                DatabaseTest.isolated false "concurrent identical requests converge" (fun fixture ->
                    AccountFlowRequestTests(fixture)
                        .``concurrent identical requests serialize to one created and one replay`` ()) ]
          testList
              "identity store"
              [ DatabaseTest.isolated false "CRUD and optimistic concurrency" (fun fixture ->
                    IdentityStoreTests(fixture).``store creates finds updates and detects stale writes`` ())
                DatabaseTest.isolated false "duplicate identity fields" (fun fixture ->
                    IdentityStoreTests(fixture).``store maps duplicate normalized email and username`` ())
                DatabaseTest.isolated false "authenticator and recovery codes" (fun fixture ->
                    IdentityStoreTests(fixture)
                        .``authenticator and hashed recovery codes round trip and redeem once`` ())
                DatabaseTest.isolated false "atomic account-flow handoff" (fun fixture ->
                    IdentityStoreTests(fixture)
                        .``account handoff commits atomically and a mismatched completion rolls back`` ())
                DatabaseTest.isolated false "purpose-specific tokens survive restart" (fun fixture ->
                    IdentityStoreTests(fixture).``purpose-specific identity tokens survive an application restart`` ()) ]
          testList
              "catalog and cart stores"
              [ DatabaseTest.isolated false "catalog browses and searches" (fun fixture ->
                    CatalogStoreTests(fixture).``seed catalog browses and searches`` ())
                DatabaseTest.isolated false "catalog update bumps price version" (fun fixture ->
                    CatalogStoreTests(fixture).``update bumps the price version`` ())
                DatabaseTest.isolated false "stock adjustment rejects negative" (fun fixture ->
                    CatalogStoreTests(fixture).``adjust stock rejects a negative balance`` ())
                DatabaseTest.isolated false "catalog create rejects duplicate sku" (fun fixture ->
                    CatalogStoreTests(fixture).``create rejects a duplicate sku`` ())
                DatabaseTest.isolated false "guest capability resolves and revokes" (fun fixture ->
                    CartGuestCapabilitiesTests(fixture).``issue resolves and revokes`` ())
                DatabaseTest.isolated false "guest capability is key scoped" (fun fixture ->
                    CartGuestCapabilitiesTests(fixture).``a different key cannot resolve the capability`` ())
                DatabaseTest.isolated false "merge snapshot is insert-once" (fun fixture ->
                    CartMergeTests(fixture).``capture is insert-once and applied snapshots leave the pending set`` ()) ]
          testList
              "stock reservations"
              [ DatabaseTest.isolated false "last unit race reserves once" (fun fixture ->
                    OrderReservationTests(fixture).``concurrent last-unit reservations allow exactly one order`` ())
                DatabaseTest.isolated false "release fences delayed reserve" (fun fixture ->
                    OrderReservationTests(fixture).``release fences a delayed reserve action`` ())
                DatabaseTest.isolated false "opposite order multi SKU reservation" (fun fixture ->
                    OrderReservationTests(fixture).``opposite order multi SKU reservations do not deadlock`` ())
                DatabaseTest.isolated false "order snapshots are immutable" (fun fixture ->
                    OrderReservationTests(fixture).``order snapshots are immutable and idempotently captured`` ())
                DatabaseTest.isolated false "stock commit is idempotent" (fun fixture ->
                    OrderReservationTests(fixture).``stock commit is idempotent and cannot be released`` ()) ]
          testList
              "payment operations"
              [ DatabaseTest.isolated false "authorization is provider-idempotent" (fun fixture ->
                    PaymentOperationTests.``authorization persists one provider operation and one order callback``
                        fixture) ] ]
    |> testSequenced

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
