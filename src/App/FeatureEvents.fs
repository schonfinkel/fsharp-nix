namespace App

open System
open App.Domain
open Microsoft.AspNetCore.Http
open Oxpecker

[<RequireQualifiedAccess>]
module FeatureEvents =
    let private writeFeatureChange (response: HttpResponse) cancellationToken =
        task {
            do! response.WriteAsync("event: feature-change\ndata: refresh\n\n", cancellationToken)
            do! response.Body.FlushAsync cancellationToken
        }

    let private writeHeartbeat (response: HttpResponse) cancellationToken =
        task {
            do! response.WriteAsync(": keep-alive\n\n", cancellationToken)
            do! response.Body.FlushAsync cancellationToken
        }

    let stream: EndpointHandler =
        fun context ->
            task {
                let cancellationToken = context.RequestAborted
                context.Response.ContentType <- "text/event-stream"
                context.Response.Headers.CacheControl <- "no-cache"
                context.Response.Headers["X-Accel-Buffering"] <- "no"

                try
                    let source = context.GetService<IFeatureChangeSource>()
                    use! subscription = source.Subscribe cancellationToken
                    do! context.Response.StartAsync cancellationToken
                    do! writeFeatureChange context.Response cancellationToken

                    while not cancellationToken.IsCancellationRequested do
                        let! change = subscription.Next cancellationToken

                        match change with
                        | RefreshRequired -> do! writeFeatureChange context.Response cancellationToken
                        | KeepAlive -> do! writeHeartbeat context.Response cancellationToken
                with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    ()
            }
