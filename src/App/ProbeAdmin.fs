namespace App

open System
open System.Threading
open System.Threading.Tasks
open App.Database
open App.Views
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Npgsql
open Oxpecker

type ProbeEffectHandler(dataSource: NpgsqlDataSource) =
    interface IActionHandler<ProbeId, ProbeAction, ProbeActionError> with
        member _.HandleAsync(action: LeasedAction<ProbeId, ProbeAction>, ct: CancellationToken) =
            ProbeEffects.apply dataSource action.Work ct

[<RequireQualifiedAccess>]
module ProbeAdmin =

    let private entity = entityId "probe-p0"

    let private readOutbox (dataSource: NpgsqlDataSource) (ct: CancellationToken) =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    "SELECT status FROM fsnix.integration_outbox ORDER BY outbox_id DESC LIMIT 1",
                    connection
                )

            let! status = command.ExecuteScalarAsync(ct)
            return status |> Option.ofObj |> Option.map string |> Option.defaultValue "empty"
        }

    let private readReceipts (dataSource: NpgsqlDataSource) (ct: CancellationToken) =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM fsnix.action_receipts WHERE machine_id = @machine_id",
                    connection
                )

            command.Parameters.AddWithValue("machine_id", Probe.MachineKey) |> ignore
            let! count = command.ExecuteScalarAsync(ct)
            return int (count :?> int64)
        }

    let private waitForDone
        (machine: Machine<ProbeId, ProbeState, ProbeEvent, ProbeAction, ProbeActionError>)
        : Task<Snapshot<ProbeState> option> =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 5.

            let mutable snapshot = None

            while DateTime.UtcNow < deadline && snapshot.IsNone do
                let! current = Machine.state machine entity CancellationToken.None

                snapshot <-
                    match current with
                    | Ok(Some state) when state.State.Phase <> EffectPending -> Some state
                    | _ -> None

                if snapshot.IsNone then
                    do! Task.Delay(TimeSpan.FromMilliseconds 50.)

            return snapshot
        }

    let private render (context: HttpContext) (model: ProbeModel) =
        task {
            Web.noStore context
            return! context.WriteHtmlView(ProbeViews.page context model)
        }

    let index: EndpointHandler =
        fun context ->
            task {
                let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let clients = context.RequestServices.GetRequiredService<ProbeMachineClient>()
                let! snapshot = Machine.state clients.Probe entity context.RequestAborted
                let! receipts = readReceipts dataSource context.RequestAborted
                let! outbox = readOutbox dataSource context.RequestAborted

                let model =
                    match snapshot with
                    | Ok(Some state) ->
                        { Phase = string state.State.Phase
                          Runs = state.State.Runs
                          Epoch = Epoch.value state.Epoch
                          CommandResult = "not run"
                          Receipts = receipts
                          Outbox = outbox }
                    | _ ->
                        { Phase = "no snapshot"
                          Runs = 0
                          Epoch = 0UL
                          CommandResult = "not run"
                          Receipts = receipts
                          Outbox = outbox }

                return! render context model
            }

    let run: EndpointHandler =
        fun context ->
            task {
                let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let clients = context.RequestServices.GetRequiredService<ProbeMachineClient>()
                let key = $"probe-%O{Guid.NewGuid()}"

                let! outcome =
                    Machine.send clients.Probe entity (EventEnvelope.create key RunProbe) context.RequestAborted

                let commandResult =
                    match outcome with
                    | Ok(CommandResult.Committed committed) ->
                        $"committed %A{committed.Draft.Event} at epoch {Epoch.value committed.Epoch}"
                    | Ok(CommandResult.Rejected failure) -> $"rejected: %A{failure}"
                    | Ok(CommandResult.DeadLettered failure) -> $"dead-lettered: %A{failure}"
                    | Ok CommandResult.Pending -> "pending"
                    | Error error -> $"machine error: %A{error}"

                let! snapshot = waitForDone clients.Probe
                let! receipts = readReceipts dataSource context.RequestAborted
                let! outbox = readOutbox dataSource context.RequestAborted

                let model =
                    match snapshot with
                    | Some state ->
                        { Phase = string state.State.Phase
                          Runs = state.State.Runs
                          Epoch = Epoch.value state.Epoch
                          CommandResult = commandResult
                          Receipts = receipts
                          Outbox = outbox }
                    | None ->
                        { Phase = "timed out"
                          Runs = 0
                          Epoch = 0UL
                          CommandResult = commandResult
                          Receipts = receipts
                          Outbox = outbox }

                return! render context model
            }
