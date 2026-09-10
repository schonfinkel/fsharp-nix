module App.UnitTests.Program

open App.Tests
open Expecto

[<EntryPoint>]
let main args =
    runTestsWithCLIArgs [] args UnitTests.tests
