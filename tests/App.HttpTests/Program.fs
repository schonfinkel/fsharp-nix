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
                    AuthHttpTests(fixture).``password session must enroll TOTP before feature administration`` ()) ] ]

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
