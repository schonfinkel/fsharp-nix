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
              "identity store"
              [ DatabaseTest.isolated false "CRUD and optimistic concurrency" (fun fixture ->
                    IdentityStoreTests(fixture).``store creates finds updates and detects stale writes`` ())
                DatabaseTest.isolated false "duplicate identity fields" (fun fixture ->
                    IdentityStoreTests(fixture).``store maps duplicate normalized email and username`` ())
                DatabaseTest.isolated false "authenticator and recovery codes" (fun fixture ->
                    IdentityStoreTests(fixture)
                        .``authenticator and hashed recovery codes round trip and redeem once`` ()) ] ]

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
