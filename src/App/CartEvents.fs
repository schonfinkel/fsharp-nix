namespace App

open System
open App.Database
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker

[<RequireQualifiedAccess>]
module CartEvents =
    let private writeRefresh (response: HttpResponse) cancellationToken =
        task {
            do! response.WriteAsync("event: cart-change\ndata: refresh\n\n", cancellationToken)
            do! response.Body.FlushAsync cancellationToken
        }

    let private writeHeartbeat (response: HttpResponse) cancellationToken =
        task {
            do! response.WriteAsync(": keep-alive\n\n", cancellationToken)
            do! response.Body.FlushAsync cancellationToken
        }

    /// <summary>Bounded fan-out over one shared LISTEN connection. Every hint is only an
    /// identifier-free "reread your cart" signal; the browser always rereads durable state, so
    /// the stream itself reveals nothing.</summary>
    let stream: EndpointHandler =
        fun context ->
            task {
                let cancellationToken = context.RequestAborted
                context.Response.ContentType <- "text/event-stream"
                context.Response.Headers.CacheControl <- "no-cache"
                context.Response.Headers["X-Accel-Buffering"] <- "no"

                try
                    let source = context.GetService<ICartChangeSource>()
                    use! subscription = source.Subscribe cancellationToken
                    do! context.Response.StartAsync cancellationToken
                    do! writeRefresh context.Response cancellationToken

                    while not cancellationToken.IsCancellationRequested do
                        let! change = subscription.Next cancellationToken

                        match change with
                        | RefreshRequired -> do! writeRefresh context.Response cancellationToken
                        | KeepAlive -> do! writeHeartbeat context.Response cancellationToken
                with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    ()
            }
