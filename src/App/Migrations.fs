namespace App

open App.Migrations
open Microsoft.Extensions.Logging
open Npgsql

module AutomataMigrator = ByzantineSystems.Automata.Storage.Postgres.Migrator

[<RequireQualifiedAccess>]
module DatabaseMigrations =

    let private automataConnection (connectionString: string) : string =
        let builder = NpgsqlConnectionStringBuilder connectionString
        builder.SearchPath <- "public"
        builder.ConnectionString

    let runAll (logger: ILogger) (connectionString: string) (environmentName: string) : Result<unit, string> =
        let target = NpgsqlConnectionStringBuilder connectionString
        logger.LogInformation("Applying database migrations to {Database}", target.Database)

        match AutomataMigrator.migrate logger (automataConnection connectionString) with
        | Ok _ ->
            match Migrator.migrate connectionString environmentName with
            | Ok() -> Ok()
            | Error failure -> Error failure.Message
        | Error failure -> Error $"Automata fsm migration failed: %A{failure}"
