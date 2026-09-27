namespace App.Tests

open System.Threading
open System.Threading.Tasks
open App

/// <summary>Deterministic in-process transport for delivery tests. Sends are recorded; queued
/// failures are returned in order before sends succeed again, so retry and dead-letter paths
/// are exercised without an SMTP server.</summary>
type FakeEmailTransport() =
    let mutable failures: EmailFailure list = []
    let sent = ResizeArray<OutgoingEmail>()

    member _.Sent = List.ofSeq sent

    member _.FailWith (error: EmailFailure) (times: int) =
        failures <- failures @ List.replicate times error

    interface IEmailTransport with
        member _.Send(email, _ct: CancellationToken) =
            task {
                match failures with
                | error :: rest ->
                    failures <- rest
                    return Error error
                | [] ->
                    sent.Add email
                    return Ok()
            }
