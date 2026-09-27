namespace App

open System
open System.Threading
open System.Threading.Tasks
open App.Auth
open App.Database
open App.Domain
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql

type ProbeMachineClient(context: PostgresContext, loggerFactory: ILoggerFactory, health: RuntimeHealth) =
    let logger = loggerFactory.CreateLogger "ProbeMachineClient"
    let mutable client = None

    member _.Probe =
        client
        |> Option.defaultWith (fun () -> invalidOp "the probe machine client has not started")

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting RuntimeComponent.ProbeMachine

                let machine =
                    match Probe.buildClient logger context with
                    | Ok machine -> machine
                    | Error _ ->
                        health.Failed(RuntimeComponent.ProbeMachine, RuntimeFailure.InvalidChart)
                        invalidOp "probe client machine is invalid"

                match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                | Ok(Startup.Started _) ->
                    client <- Some machine
                    health.Succeeded RuntimeComponent.ProbeMachine
                | Ok(Startup.Refused _) ->
                    health.Failed(RuntimeComponent.ProbeMachine, RuntimeFailure.StartupRefused)
                    return raise (InvalidOperationException "probe client refused to boot")
                | Error _ ->
                    health.Failed(RuntimeComponent.ProbeMachine, RuntimeFailure.StartupFailed)
                    return raise (InvalidOperationException "probe client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped RuntimeComponent.ProbeMachine
            }
            :> Task

type AccountFlowMachineClient(context: PostgresContext, loggerFactory: ILoggerFactory, health: RuntimeHealth) =
    let logger = loggerFactory.CreateLogger "AccountFlowMachineClient"
    let mutable client = None

    member _.Flows =
        client
        |> Option.defaultWith (fun () -> invalidOp "the account flow machine client has not started")

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting RuntimeComponent.AccountFlowMachine

                let machine =
                    match AccountFlowCodec.buildClient logger context with
                    | Ok machine -> machine
                    | Error _ ->
                        health.Failed(RuntimeComponent.AccountFlowMachine, RuntimeFailure.InvalidChart)
                        invalidOp "account flow client machine is invalid"

                match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                | Ok(Startup.Started _) ->
                    client <- Some machine
                    health.Succeeded RuntimeComponent.AccountFlowMachine
                | Ok(Startup.Refused _) ->
                    health.Failed(RuntimeComponent.AccountFlowMachine, RuntimeFailure.StartupRefused)
                    return raise (InvalidOperationException "account flow client refused to boot")
                | Error _ ->
                    health.Failed(RuntimeComponent.AccountFlowMachine, RuntimeFailure.StartupFailed)
                    return raise (InvalidOperationException "account flow client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped RuntimeComponent.AccountFlowMachine
            }
            :> Task

type IntegrationOutboxRelay
    (
        dataSource: NpgsqlDataSource,
        destinations: OutboxDestination list,
        logger: ILogger<IntegrationOutboxRelay>,
        health: RuntimeHealth
    ) =
    inherit BackgroundService()

    let suffix = Guid.NewGuid().ToString("N")[..7]

    let options =
        Outbox.RelayOptions.defaults $"relay-{Environment.MachineName}-{suffix}"

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try
                        let! _ = Outbox.deliverPending dataSource options destinations ct
                        health.Succeeded RuntimeComponent.IntegrationOutboxRelay
                    with error ->
                        health.Failed(RuntimeComponent.IntegrationOutboxRelay, RuntimeFailure.PassFailed)

                        logger.LogError(
                            "integration outbox relay pass failed ({ExceptionType})",
                            SafeDiagnostics.exceptionType error
                        )

                    do! Task.Delay(TimeSpan.FromMilliseconds 500., ct)
            with :? OperationCanceledException ->
                health.Stopped RuntimeComponent.IntegrationOutboxRelay
        }
        :> Task

/// <summary>
/// Fires due flow-expiry deadlines into the flows machine. A deadline whose gate callback is
/// durably sent becomes <c>ExpiryTimerFired</c> (idempotently keyed by deadline id, so a crash
/// between enqueue and settlement is a recognised repeat); one whose gate failed or was never
/// enqueued is cancelled; one whose gate is still in flight is released for the next pass.
/// </summary>
type FlowDeadlineScanner
    (
        dataSource: NpgsqlDataSource,
        flows: AccountFlowMachineClient,
        logger: ILogger<FlowDeadlineScanner>,
        health: RuntimeHealth
    ) =
    inherit BackgroundService()

    let suffix = Guid.NewGuid().ToString("N")[..7]

    let options =
        FlowDeadlines.ScannerOptions.defaults $"deadline-scanner-{Environment.MachineName}-{suffix}"

    let processDeadline (deadline: FlowDeadlines.ClaimedDeadline) (ct: CancellationToken) =
        task {
            let! gate = FlowDeadlines.gateState dataSource deadline.GateCallbackKey ct

            match gate with
            | FlowDeadlines.GateState.Sent ->
                let key = $"flow-deadline:v1:%d{deadline.DeadlineId}"
                let entity = entityId (deadline.FlowId.ToString("D"))

                let! outcome =
                    Machine.enqueue
                        flows.Flows
                        entity
                        (EventEnvelope.create key (ExpiryTimerFired(deadline.Generation, deadline.Deadline)))
                        ct

                match outcome with
                | Ok _ -> do! FlowDeadlines.fired dataSource options deadline.DeadlineId ct
                | Error _ ->
                    logger.LogError("flow deadline {DeadlineId} enqueue failed", deadline.DeadlineId)
                    do! FlowDeadlines.release dataSource options deadline.DeadlineId ct
            | FlowDeadlines.GateState.Pending -> do! FlowDeadlines.release dataSource options deadline.DeadlineId ct
            | FlowDeadlines.GateState.Unsent -> do! FlowDeadlines.cancelled dataSource options deadline.DeadlineId ct
        }

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try
                        let! claimed = FlowDeadlines.claim dataSource options ct

                        for deadline in claimed do
                            do! processDeadline deadline ct

                        health.Succeeded RuntimeComponent.FlowDeadlineScanner
                    with error ->
                        health.Failed(RuntimeComponent.FlowDeadlineScanner, RuntimeFailure.PassFailed)

                        logger.LogError(
                            "flow deadline scanner pass failed ({ExceptionType})",
                            SafeDiagnostics.exceptionType error
                        )

                    do! Task.Delay(TimeSpan.FromMilliseconds 500., ct)
            with :? OperationCanceledException ->
                health.Stopped RuntimeComponent.FlowDeadlineScanner
        }
        :> Task
