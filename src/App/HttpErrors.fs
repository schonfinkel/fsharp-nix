namespace App

open System.Threading.Tasks
open App.Views
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine

/// <summary>What a machine command means for the HTTP caller, independent of machine types.</summary>
[<RequireQualifiedAccess>]
type HttpOutcome =
    /// <summary>The command committed; effects are queued, not necessarily complete.</summary>
    | Completed
    /// <summary>Durably accepted but not yet processed.</summary>
    | Accepted
    /// <summary>Stale epoch, illegal transition, or a rejected/dead-lettered command.</summary>
    | Conflict
    /// <summary>Transient store/circuit/lifecycle failure; the caller may retry.</summary>
    | Unavailable

[<RequireQualifiedAccess>]
module HttpOutcome =
    let private ofMachineError (error: MachineError<_>) =
        match error with
        | MachineError.Transition _ -> HttpOutcome.Conflict
        | MachineError.Rejected _
        | MachineError.CircuitOpen _
        | MachineError.Timeout _
        | MachineError.Store _ -> HttpOutcome.Unavailable

    let private severity =
        function
        | HttpOutcome.Completed -> 0
        | HttpOutcome.Accepted -> 1
        | HttpOutcome.Conflict -> 2
        | HttpOutcome.Unavailable -> 3

    /// <summary>The most severe of several outcomes (for endpoints that send one command per
    /// child entity); an empty list is <c>Completed</c>.</summary>
    let worst (outcomes: HttpOutcome list) =
        outcomes
        |> List.fold (fun a b -> if severity b > severity a then b else a) HttpOutcome.Completed

    /// <summary>Classifies a <c>Machine.send</c> result.</summary>
    let ofSend (result: Result<CommandResult<_, _, _, _, _>, MachineError<_>>) =
        match result with
        | Ok(CommandResult.Committed _) -> HttpOutcome.Completed
        | Ok CommandResult.Pending -> HttpOutcome.Accepted
        | Ok(CommandResult.Rejected _)
        | Ok(CommandResult.DeadLettered _) -> HttpOutcome.Conflict
        | Error error -> ofMachineError error

    /// <summary>Classifies a <c>Machine.enqueue</c> result: durable acceptance (including an
    /// already-submitted repeat of the same idempotency key) is <c>Accepted</c>.</summary>
    let ofEnqueue (result: Result<_, MachineError<_>>) =
        match result with
        | Ok _ -> HttpOutcome.Accepted
        | Error error -> ofMachineError error

/// <summary>
/// Centralised error responses (PLAN "HTTP mappings"). htmx requests receive a fragment the
/// page's <c>hx-status</c> rules can route; plain form posts receive a full page, so a
/// progressive-enhancement fallback never renders a lone paragraph.
/// </summary>
[<RequireQualifiedAccess>]
module HttpErrors =
    [<Literal>]
    let RetryAfterSeconds = "2"

    let private write (status: int) (heading: string) (message: string) (context: HttpContext) : Task =
        context.Response.StatusCode <- status
        Web.varyHtmx context
        Web.noStore context

        if Web.isHtmx context then
            context.WriteHtmlView(p(class' = "error").attr ("role", "alert") { message })
        else
            context.WriteHtmlView(
                SharedViews.layout
                    context
                    heading
                    (section () {
                        h1 () { heading }
                        p(class' = "error").attr ("role", "alert") { message }
                        a (href = "/") { "Return home" }
                    })
            )

    let badRequest message context =
        write StatusCodes.Status400BadRequest "Bad request" message context

    let notFound message context =
        write StatusCodes.Status404NotFound "Not found" message context

    let unprocessable message context =
        write StatusCodes.Status422UnprocessableEntity "Invalid input" message context

    let conflict message context =
        write StatusCodes.Status409Conflict "Conflict" message context

    let unavailable (context: HttpContext) =
        context.Response.Headers.RetryAfter <- RetryAfterSeconds
        write StatusCodes.Status503ServiceUnavailable "Temporarily unavailable" "Please retry shortly." context

    /// <summary>Accepted but incomplete work: 202 with a status URL to poll or reload.</summary>
    let accepted (statusUrl: string) (context: HttpContext) : Task =
        context.Response.StatusCode <- StatusCodes.Status202Accepted
        context.Response.Headers.Location <- statusUrl
        Web.varyHtmx context
        Web.noStore context

        context.WriteHtmlView(
            p(class' = "notice").attr ("role", "status") {
                "Your request was accepted and is being processed. "
                a (href = statusUrl) { "Check status" }
            }
        )

    /// <summary>Maps a command outcome to a response, running <paramref name="onCompleted"/>
    /// only when the command committed or was durably accepted.</summary>
    let respond
        (outcome: HttpOutcome)
        (conflictMessage: string)
        (onCompleted: HttpContext -> Task)
        (context: HttpContext)
        : Task =
        match outcome with
        | HttpOutcome.Completed
        | HttpOutcome.Accepted -> onCompleted context
        | HttpOutcome.Conflict -> conflict conflictMessage context
        | HttpOutcome.Unavailable -> unavailable context
