namespace App.Tests

open System
open App.Migrations
open Expecto
open Npgsql

type PostgreSqlFixture(?includeProduction: bool) =
    let baseConnectionString =
        Environment.GetEnvironmentVariable "ConnectionStrings__App"
        |> Option.ofObj
        |> Option.defaultValue "Host=127.0.0.1;Port=5432;Database=fsnix;Username=fsnix;Password=fsnix"

    let connectionStringFor database =
        let builder = NpgsqlConnectionStringBuilder baseConnectionString
        builder.Database <- database
        builder.SearchPath <- Migrator.Schema
        builder.ConnectionString

    let suffix = Guid.NewGuid().ToString("N")[..15]
    let testDatabase = $"fsnix_test_{suffix}"
    let productionTestDatabase = $"fsnix_prod_test_{suffix}"
    let includeProduction = defaultArg includeProduction false
    let testConnectionString = connectionStringFor testDatabase
    let productionTestConnectionString = connectionStringFor productionTestDatabase

    let adminConnectionString =
        let builder = NpgsqlConnectionStringBuilder baseConnectionString
        builder.Database <- "postgres"
        builder.ConnectionString

    member _.ConnectionString = testConnectionString
    member _.ProductionConnectionString = productionTestConnectionString

    member _.InitializeAsync() =
        task {
            use connection = new NpgsqlConnection(adminConnectionString)
            do! connection.OpenAsync()

            let databases =
                [ testDatabase
                  if includeProduction then
                      productionTestDatabase ]

            for database in databases do
                use command =
                    new NpgsqlCommand($"CREATE DATABASE {database} OWNER fsnix", connection)

                let! _ = command.ExecuteNonQueryAsync()
                ()

            use testConnection = new NpgsqlConnection(testConnectionString)
            do! testConnection.OpenAsync()

            use schemaExistsCommand =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT FROM information_schema.schemata WHERE schema_name = 'fsnix')",
                    testConnection
                )

            let! schemaExists = schemaExistsCommand.ExecuteScalarAsync()

            if schemaExists :?> bool then
                invalidOp "A newly created test database unexpectedly contained the application schema."

            match Migrator.migrate testConnectionString "Development" with
            | Ok() -> ()
            | Error failure -> raise (InvalidOperationException(failure.Message, Option.toObj failure.Exception))
        }

    member _.DisposeAsync() =
        task {
            NpgsqlConnection.ClearAllPools()
            use connection = new NpgsqlConnection(adminConnectionString)
            do! connection.OpenAsync()

            let databases =
                [ testDatabase
                  if includeProduction then
                      productionTestDatabase ]

            for database in databases do
                use command =
                    new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", connection)

                let! _ = command.ExecuteNonQueryAsync()
                ()
        }

module DatabaseTest =
    let isolated includeProduction name run =
        testCaseTask name (fun () ->
            task {
                let fixture = PostgreSqlFixture(includeProduction)
                let mutable failure = None

                try
                    do! fixture.InitializeAsync()
                    do! run fixture
                with error ->
                    failure <- Some error

                do! fixture.DisposeAsync()

                match failure with
                | Some error -> return raise error
                | None -> return ()
            })
