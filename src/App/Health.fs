namespace App

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Npgsql

[<RequireQualifiedAccess>]
module Health =
    let private respond (status: int) (body: string) (context: HttpContext) =
        context.Response.StatusCode <- status
        context.Response.ContentType <- "text/plain; charset=utf-8"
        context.Response.Headers.CacheControl <- "no-store"
        context.Response.WriteAsync body

    let live (context: HttpContext) =
        respond StatusCodes.Status200OK "live" context

    let startup (context: HttpContext) =
        let health = context.RequestServices.GetRequiredService<RuntimeHealth>()

        if health.Snapshot() |> RuntimeHealth.startupReady then
            respond StatusCodes.Status200OK "started" context
        else
            respond StatusCodes.Status503ServiceUnavailable "starting" context

    let ready (context: HttpContext) : Task =
        task {
            let health = context.RequestServices.GetRequiredService<RuntimeHealth>()
            let timeProvider = context.RequestServices.GetRequiredService<TimeProvider>()
            let configuration = context.RequestServices.GetRequiredService<IConfiguration>()
            let snapshot = health.Snapshot()

            let maximumAge =
                match Int32.TryParse configuration["Health:WorkerMaximumAgeSeconds"] with
                | true, seconds when seconds > 0 -> TimeSpan.FromSeconds(float seconds)
                | _ -> TimeSpan.FromSeconds 30.

            let runtimeReady =
                RuntimeHealth.startupReady snapshot
                && RuntimeHealth.workersReady (timeProvider.GetUtcNow()) maximumAge snapshot

            let! databaseReady =
                task {
                    try
                        let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                        use connection = dataSource.CreateConnection()
                        do! connection.OpenAsync(context.RequestAborted)
                        use command = new NpgsqlCommand("SELECT 1", connection)
                        let! result = command.ExecuteScalarAsync(context.RequestAborted)
                        return not (isNull result)
                    with
                    | :? OperationCanceledException when context.RequestAborted.IsCancellationRequested ->
                        return raise (OperationCanceledException context.RequestAborted)
                    | _ -> return false
                }

            if runtimeReady && databaseReady then
                do! respond StatusCodes.Status200OK "ready" context
            else
                do! respond StatusCodes.Status503ServiceUnavailable "not-ready" context
        }
        :> Task
