namespace App.DatabaseTests

open System
open System.Threading
open System.Threading.Tasks
open App.Database
open App.Tests
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging.Abstractions
open Npgsql

[<RequireQualifiedAccess>]
module ProbeTests =

    type private Started =
        { DataSource: NpgsqlDataSource
          Machine: Machine<ProbeId, ProbeState, ProbeEvent, ProbeAction, ProbeActionError> }

    let private isOk result =
        match result with
        | Ok _ -> true
        | Error _ -> false

    let private relayOptions = Outbox.RelayOptions.defaults "probe-test-relay"

    let private probeDestination (started: Started) =
        OutboxDestination.forMachine
            Probe.MachineKey
            EntityId.create
            (Serialization.systemTextJson<ProbeEvent> ())
            started.Machine

    let private deliverPending (started: Started) =
        Outbox.deliverPending started.DataSource relayOptions [ probeDestination started ] CancellationToken.None

    let private startProbeMachine (connectionString: string) =
        task {
            let dataSource = AutomataStore.createDataSource connectionString
            let context = AutomataStore.createContext dataSource ignore

            let machine =
                match Probe.buildWorker NullLogger.Instance context with
                | Ok machine -> machine
                | Error errors -> invalidOp $"probe machine is invalid: %A{errors}"

            let! startup =
                Machine.startAsync machine (PostgresChartRegistry { Context = context }) CancellationToken.None

            match startup with
            | Ok(Startup.Started _) ->
                return
                    { DataSource = dataSource
                      Machine = machine }
            | Ok(Startup.Refused defects) -> return invalidOp $"probe machine refused to boot: %A{defects}"
            | Error error -> return invalidOp $"probe machine failed to start: %A{error}"
        }

    let private stop (started: Started) =
        task {
            do! Machine.stopAsync started.Machine CancellationToken.None
            started.DataSource.Dispose()
        }

    let private waitForPhase
        (machine: Machine<ProbeId, ProbeState, ProbeEvent, ProbeAction, ProbeActionError>)
        (entity: ProbeId)
        (phase: ProbePhase)
        : Task<Snapshot<ProbeState> option> =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 5.
            let mutable current = None

            while DateTime.UtcNow < deadline && current.IsNone do
                let! snapshot = Machine.state machine entity CancellationToken.None

                match snapshot with
                | Ok(Some state) when state.State.Phase = phase -> current <- Some state
                | _ -> ()

                if current.IsNone then
                    do! Task.Delay(TimeSpan.FromMilliseconds 50.)

            return current
        }

    let private scalar (connectionString: string) (sql: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command = new NpgsqlCommand(sql, connection)
            let! result = command.ExecuteScalarAsync()
            return result
        }

    let private receiptCount (connectionString: string) =
        task {
            let! count =
                scalar connectionString "SELECT count(*) FROM fsnix.action_receipts WHERE machine_id = 'probes'"

            return int (count :?> int64)
        }

    let private lastOutboxStatus (connectionString: string) =
        task {
            let! status =
                scalar connectionString "SELECT status FROM fsnix.integration_outbox ORDER BY outbox_id DESC LIMIT 1"

            return status |> Option.ofObj |> Option.map string
        }

    let run (fixture: PostgreSqlFixture) =
        task {
            let! started = startProbeMachine fixture.ConnectionString
            use workers = new CancellationTokenSource()
            let processing = (Machine.processor started.Machine).RunAsync workers.Token

            let dispatcher =
                Machine.dispatcher
                    started.Machine
                    (fun (action: LeasedAction<ProbeId, ProbeAction>) (ct: CancellationToken) ->
                        ProbeEffects.apply started.DataSource action.Work ct)

            let outcome =
                task {
                    try
                        let probeId = entityId "probe-loop"
                        let key = "probe-loop-run-1"

                        let! sent =
                            Machine.send
                                started.Machine
                                probeId
                                (EventEnvelope.create key RunProbe)
                                CancellationToken.None

                        Assert.True(isOk sent, $"send must succeed: %A{sent}")

                        let! pending = waitForPhase started.Machine probeId EffectPending
                        Assert.True(Option.isSome pending, "the probe must reach effect-pending")

                        let! _ = dispatcher.PollAsync CancellationToken.None

                        let! delivered = deliverPending started
                        Assert.Equal(1, delivered)

                        let! finished = waitForPhase started.Machine probeId Done

                        let finalState = finished |> Option.map (fun snapshot -> snapshot.State)

                        Assert.True(finalState.IsSome, "the probe must reach done")
                        Assert.Equal({ Phase = Done; Runs = 1 }, finalState.Value)

                        let! receipts = receiptCount fixture.ConnectionString
                        Assert.Equal(1, receipts)

                        let! outbox = lastOutboxStatus fixture.ConnectionString
                        Assert.Equal(Some "sent", outbox)

                        let! replay =
                            Machine.send
                                started.Machine
                                probeId
                                (EventEnvelope.create key RunProbe)
                                CancellationToken.None

                        Assert.True(
                            isOk replay,
                            $"a duplicate submission must resolve to the first outcome: %A{replay}"
                        )

                        let! settled = waitForPhase started.Machine probeId Done

                        let settledRuns = settled |> Option.map (fun snapshot -> snapshot.State.Runs)

                        Assert.True(settledRuns.IsSome, "the probe must still be done after a replay")
                        Assert.Equal(1, settledRuns.Value)

                        let! deliveredAgain = deliverPending started
                        Assert.Equal(0, deliveredAgain)
                        return Ok()
                    with error ->
                        return Error error
                }

            let! result = outcome
            workers.Cancel()
            let! _ = processing
            do! stop started

            match result with
            | Ok() -> return ()
            | Error error -> return raise error
        }

    let crashRecovery (fixture: PostgreSqlFixture) =
        task {
            let! started = startProbeMachine fixture.ConnectionString
            use workers = new CancellationTokenSource()
            let processing = (Machine.processor started.Machine).RunAsync workers.Token

            let outcome =
                task {
                    try
                        let probeId = entityId "probe-recover"

                        let! sent =
                            Machine.send
                                started.Machine
                                probeId
                                (EventEnvelope.create "probe-recover-run-1" RunProbe)
                                CancellationToken.None

                        Assert.True(isOk sent, $"send must succeed: %A{sent}")

                        let! pending = waitForPhase started.Machine probeId EffectPending
                        Assert.True(Option.isSome pending, "the probe must reach effect-pending")

                        let codec = Serialization.systemTextJson<ProbeEvent> ()

                        let eventJson = codec.Encode EffectCompleted |> Result.defaultValue ""

                        use connection = started.DataSource.CreateConnection()
                        do! connection.OpenAsync()

                        use insert =
                            new NpgsqlCommand(
                                """INSERT INTO fsnix.integration_outbox (callback_key, machine_id, entity_id, event)
                                   VALUES ('probe-recover-callback-1', 'probes', 'probe-recover', @event::jsonb)""",
                                connection
                            )

                        insert.Parameters.AddWithValue("event", eventJson) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync()

                        let! delivered = deliverPending started
                        Assert.Equal(1, delivered)

                        let! finished = waitForPhase started.Machine probeId Done

                        let finishedRuns = finished |> Option.map (fun snapshot -> snapshot.State.Runs)

                        Assert.True(finishedRuns.IsSome, "the probe must reach done after relay recovery")
                        Assert.Equal(1, finishedRuns.Value)

                        let! outbox = lastOutboxStatus fixture.ConnectionString
                        Assert.Equal(Some "sent", outbox)
                        return Ok()
                    with error ->
                        return Error error
                }

            let! result = outcome
            workers.Cancel()
            let! _ = processing
            do! stop started

            match result with
            | Ok() -> return ()
            | Error error -> return raise error
        }

    let receiptVerification (fixture: PostgreSqlFixture) =
        task {
            let! started = startProbeMachine fixture.ConnectionString

            let outcome =
                task {
                    try
                        let record =
                            { MachineId = machineId "probes"
                              EntityId = entityId "probe-receipts"
                              CommandId = CommandId.ofInt64 42L
                              Epoch = Epoch.ofUInt64 1UL
                              Ordinal = 0
                              Action = PerformProbeEffect }

                        use connection = started.DataSource.CreateConnection()
                        do! connection.OpenAsync()

                        use seed =
                            new NpgsqlCommand(
                                """INSERT INTO fsnix.action_receipts (machine_id, command_id, ordinal, action_kind, payload_hash)
                                   VALUES ('probes', 42, 0, 'PerformProbeEffect', @hash)""",
                                connection
                            )

                        seed.Parameters.AddWithValue("hash", [| 0uy; 1uy; 2uy |]) |> ignore
                        let! _ = seed.ExecuteNonQueryAsync()

                        let! applied = ProbeEffects.apply started.DataSource record CancellationToken.None

                        Assert.True(
                            (match applied with
                             | Error _ -> true
                             | Ok() -> false),
                            "a receipt with a different payload hash must be rejected"
                        )

                        let! outboxRows =
                            scalar fixture.ConnectionString "SELECT count(*) FROM fsnix.integration_outbox"

                        Assert.Equal(0, int (outboxRows :?> int64))
                        return Ok()
                    with error ->
                        return Error error
                }

            let! result = outcome
            do! stop started

            match result with
            | Ok() -> return ()
            | Error error -> return raise error
        }

    let destinationExceptionsAreIsolated (fixture: PostgreSqlFixture) =
        task {
            use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()

            use insert =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.integration_outbox
                           (callback_key, machine_id, entity_id, event, max_attempts)
                       VALUES
                           ('throwing-1', 'test-destination', 'throws', '{}'::jsonb, 1),
                           ('succeeding-1', 'test-destination', 'succeeds', '{}'::jsonb, 1)""",
                    connection
                )

            let! _ = insert.ExecuteNonQueryAsync()

            let destination: OutboxDestination =
                { MachineId = "test-destination"
                  Deliver =
                    fun (entityId, _, _, _) ->
                        if entityId = "throws" then
                            raise (InvalidOperationException "sensitive provider failure")
                        else
                            Task.FromResult(Ok OutboxDeliveryOutcome.Delivered) }

            let options = Outbox.RelayOptions.defaults "exception-test"
            let! delivered = Outbox.deliverPending dataSource options [ destination ] CancellationToken.None
            Assert.Equal(1, delivered)

            use statuses =
                new NpgsqlCommand(
                    """SELECT entity_id, status, last_error
                       FROM fsnix.integration_outbox
                       ORDER BY outbox_id""",
                    connection
                )

            let! reader = statuses.ExecuteReaderAsync()
            let rows = ResizeArray<string * string * string option>()

            while! reader.ReadAsync() do
                rows.Add(
                    reader.GetString 0,
                    reader.GetString 1,
                    if reader.IsDBNull 2 then None else Some(reader.GetString 2)
                )

            Assert.Equal([ "throws", "dead", Some "destination-threw"; "succeeds", "sent", None ], List.ofSeq rows)
        }
