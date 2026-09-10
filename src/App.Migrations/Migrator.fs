namespace App.Migrations

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open DbUp
open DbUp.Engine
open DbUp.Helpers
open Npgsql

type private MigrationAssemblyMarker = class end

type MigrationFailure =
    { Message: string
      Exception: exn option }

[<RequireQualifiedAccess>]
module Migrator =
    [<Literal>]
    let Schema = "fsnix"

    type private MigrationDirectory =
        | Init
        | Main
        | Test
        | Repeatable

    let private assembly = typeof<MigrationAssemblyMarker>.Assembly

    let private directoryName =
        function
        | Init -> "init"
        | Main -> "main"
        | Test -> "test"
        | Repeatable -> "repeatable"

    let private embeddedScripts directory : SqlScript array =
        let marker = $".Migrations.{directoryName directory}."

        assembly.GetManifestResourceNames()
        |> Array.filter (fun name -> name.Contains(marker, StringComparison.Ordinal))
        |> Array.sort
        |> Array.map (fun resourceName ->
            use stream = assembly.GetManifestResourceStream resourceName

            if isNull stream then
                invalidOp $"Embedded migration resource '{resourceName}' could not be opened."

            use reader = new StreamReader(stream, Encoding.UTF8, true)
            SqlScript(resourceName, reader.ReadToEnd()))

    let private failure (stage: string) (exceptionValue: exn) =
        Error
            { Message = $"The {stage} database migration stage failed."
              Exception = Some exceptionValue }

    let private performUpgrade
        (stage: string)
        (connectionString: string)
        (journal: IJournal option)
        (scripts: SqlScript array)
        =
        let builder =
            DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScripts(scripts)
                .WithTransaction()
                .LogToConsole()

        let configured =
            match journal with
            | Some value -> builder.JournalTo value
            | None -> builder.JournalToPostgresqlTable(Schema, "schemaversions")

        let result = configured.Build().PerformUpgrade()

        if result.Successful then
            Ok()
        else
            failure stage result.Error

    let private runOnce (connectionString: string) directory =
        directory
        |> embeddedScripts
        |> performUpgrade (directoryName directory) connectionString None

    let private contentHash (script: SqlScript) =
        script.Contents
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    let private repeatablesToRun (connectionString: string) (scripts: SqlScript array) =
        use connection = new NpgsqlConnection(connectionString)
        connection.Open()

        use command =
            new NpgsqlCommand($"SELECT script_name, content_hash FROM {Schema}.repeatable_migration_state", connection)

        use reader = command.ExecuteReader()
        let applied = Dictionary<string, string>(StringComparer.Ordinal)

        while reader.Read() do
            applied[reader.GetString 0] <- reader.GetString 1

        scripts
        |> Array.filter (fun script ->
            match applied.TryGetValue script.Name with
            | true, hash -> not (String.Equals(hash, contentHash script, StringComparison.Ordinal))
            | false, _ -> true)

    let private recordRepeatables (connectionString: string) (scripts: SqlScript array) =
        use connection = new NpgsqlConnection(connectionString)
        connection.Open()
        use transaction = connection.BeginTransaction()

        for script in scripts do
            use command =
                new NpgsqlCommand(
                    """
                    INSERT INTO fsnix.repeatable_migration_state (script_name, content_hash, applied_at)
                    VALUES (@script_name, @content_hash, CURRENT_TIMESTAMP)
                    ON CONFLICT (script_name)
                    DO UPDATE SET content_hash = EXCLUDED.content_hash,
                                  applied_at = EXCLUDED.applied_at
                    """,
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("script_name", script.Name) |> ignore
            command.Parameters.AddWithValue("content_hash", contentHash script) |> ignore
            command.ExecuteNonQuery() |> ignore

        transaction.Commit()

    let private runRepeatables (connectionString: string) =
        let changedScripts =
            Repeatable |> embeddedScripts |> repeatablesToRun connectionString

        if Array.isEmpty changedScripts then
            Ok()
        else
            match performUpgrade "repeatable" connectionString (Some(NullJournal() :> IJournal)) changedScripts with
            | Error error -> Error error
            | Ok() ->
                try
                    recordRepeatables connectionString changedScripts
                    Ok()
                with exceptionValue ->
                    failure "repeatable state recording" exceptionValue

    let private runStages connectionString environmentName =
        let oneTimeStages =
            if String.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) then
                [ Init; Main; Test ]
            else
                [ Init; Main ]

        let oneTimeResult =
            oneTimeStages
            |> List.fold
                (fun state directory -> state |> Result.bind (fun () -> runOnce connectionString directory))
                (Ok())

        oneTimeResult |> Result.bind (fun () -> runRepeatables connectionString)

    let migrate (connectionString: string) (environmentName: string) : Result<unit, MigrationFailure> =
        if String.IsNullOrWhiteSpace connectionString then
            Error
                { Message = "A PostgreSQL connection string is required."
                  Exception = None }
        else
            try
                use migrationLock = new NpgsqlConnection(connectionString)
                migrationLock.Open()

                use acquireLock =
                    new NpgsqlCommand("SELECT pg_advisory_lock(781912831708714370)", migrationLock)

                acquireLock.ExecuteNonQuery() |> ignore

                try
                    runStages connectionString environmentName
                finally
                    use releaseLock =
                        new NpgsqlCommand("SELECT pg_advisory_unlock(781912831708714370)", migrationLock)

                    releaseLock.ExecuteNonQuery() |> ignore
            with exceptionValue ->
                Error
                    { Message = "Database migration orchestration failed."
                      Exception = Some exceptionValue }
