namespace App

open System
open System.Threading
open System.Threading.Tasks
open App.Auth
open App.Cart
open App.Database
open App.Domain
open App.Orders
open App.Payments
open App.Shipments
open App.Refunds
open App.Returns
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

type CartMachineClient(context: PostgresContext, loggerFactory: ILoggerFactory, health: RuntimeHealth) =
    let logger = loggerFactory.CreateLogger "CartMachineClient"
    let mutable client = None

    member _.Carts =
        client
        |> Option.defaultWith (fun () -> invalidOp "the cart machine client has not started")

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting RuntimeComponent.CartMachine

                let machine =
                    match CartCodec.buildClient logger context with
                    | Ok machine -> machine
                    | Error _ ->
                        health.Failed(RuntimeComponent.CartMachine, RuntimeFailure.InvalidChart)
                        invalidOp "cart client machine is invalid"

                match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                | Ok(Startup.Started _) ->
                    client <- Some machine
                    health.Succeeded RuntimeComponent.CartMachine
                | Ok(Startup.Refused _) ->
                    health.Failed(RuntimeComponent.CartMachine, RuntimeFailure.StartupRefused)
                    return raise (InvalidOperationException "cart client refused to boot")
                | Error _ ->
                    health.Failed(RuntimeComponent.CartMachine, RuntimeFailure.StartupFailed)
                    return raise (InvalidOperationException "cart client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped RuntimeComponent.CartMachine
            }
            :> Task

type OrderMachineClient(context: PostgresContext, loggerFactory: ILoggerFactory, health: RuntimeHealth) =
    let logger = loggerFactory.CreateLogger "OrderMachineClient"
    let mutable client = None

    member _.Orders =
        client
        |> Option.defaultWith (fun () -> invalidOp "the order machine client has not started")

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting RuntimeComponent.OrderMachine

                let machine =
                    match OrderCodec.buildClient logger context with
                    | Ok machine -> machine
                    | Error _ ->
                        health.Failed(RuntimeComponent.OrderMachine, RuntimeFailure.InvalidChart)
                        invalidOp "order client machine is invalid"

                match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                | Ok(Startup.Started _) ->
                    client <- Some machine
                    health.Succeeded RuntimeComponent.OrderMachine
                | Ok(Startup.Refused _) ->
                    health.Failed(RuntimeComponent.OrderMachine, RuntimeFailure.StartupRefused)
                    return raise (InvalidOperationException "order client refused to boot")
                | Error _ ->
                    health.Failed(RuntimeComponent.OrderMachine, RuntimeFailure.StartupFailed)
                    return raise (InvalidOperationException "order client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped RuntimeComponent.OrderMachine
            }
            :> Task

type PaymentMachineClient(context: PostgresContext, loggerFactory: ILoggerFactory, health: RuntimeHealth) =
    let logger = loggerFactory.CreateLogger "PaymentMachineClient"
    let mutable client = None

    member _.Payments =
        client
        |> Option.defaultWith (fun () -> invalidOp "the payment machine client has not started")

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting RuntimeComponent.PaymentMachine

                let machine =
                    match PaymentCodec.buildClient logger context with
                    | Ok machine -> machine
                    | Error _ ->
                        health.Failed(RuntimeComponent.PaymentMachine, RuntimeFailure.InvalidChart)
                        invalidOp "payment client machine is invalid"

                match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                | Ok(Startup.Started _) ->
                    client <- Some machine
                    health.Succeeded RuntimeComponent.PaymentMachine
                | Ok(Startup.Refused _) ->
                    health.Failed(RuntimeComponent.PaymentMachine, RuntimeFailure.StartupRefused)
                    return raise (InvalidOperationException "payment client refused to boot")
                | Error _ ->
                    health.Failed(RuntimeComponent.PaymentMachine, RuntimeFailure.StartupFailed)
                    return raise (InvalidOperationException "payment client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped RuntimeComponent.PaymentMachine
            }
            :> Task

type ShipmentMachineClient(context: PostgresContext, loggerFactory: ILoggerFactory, health: RuntimeHealth) =
    let logger = loggerFactory.CreateLogger "ShipmentMachineClient"
    let mutable client = None

    member _.Shipments =
        client
        |> Option.defaultWith (fun () -> invalidOp "the shipment machine client has not started")

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting RuntimeComponent.ShipmentMachine

                let machine =
                    match ShipmentCodec.buildClient logger context with
                    | Ok machine -> machine
                    | Error _ ->
                        health.Failed(RuntimeComponent.ShipmentMachine, RuntimeFailure.InvalidChart)
                        invalidOp "shipment client machine is invalid"

                match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                | Ok(Startup.Started _) ->
                    client <- Some machine
                    health.Succeeded RuntimeComponent.ShipmentMachine
                | Ok(Startup.Refused _) ->
                    health.Failed(RuntimeComponent.ShipmentMachine, RuntimeFailure.StartupRefused)
                    return raise (InvalidOperationException "shipment client refused to boot")
                | Error _ ->
                    health.Failed(RuntimeComponent.ShipmentMachine, RuntimeFailure.StartupFailed)
                    return raise (InvalidOperationException "shipment client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped RuntimeComponent.ShipmentMachine
            }
            :> Task

type RefundMachineClient(context: PostgresContext, loggerFactory: ILoggerFactory, health: RuntimeHealth) =
    let logger = loggerFactory.CreateLogger "RefundMachineClient"
    let mutable client = None

    member _.Refunds =
        client
        |> Option.defaultWith (fun () -> invalidOp "refund machine client has not started")

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting RuntimeComponent.RefundMachine

                let machine =
                    match RefundCodec.buildClient logger context with
                    | Ok machine -> machine
                    | Error _ ->
                        health.Failed(RuntimeComponent.RefundMachine, RuntimeFailure.InvalidChart)
                        invalidOp "refund chart is invalid"

                match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                | Ok(Startup.Started _) ->
                    client <- Some machine
                    health.Succeeded RuntimeComponent.RefundMachine
                | Ok(Startup.Refused _) ->
                    health.Failed(RuntimeComponent.RefundMachine, RuntimeFailure.StartupRefused)
                    return raise (InvalidOperationException "refund client refused to boot")
                | Error _ ->
                    health.Failed(RuntimeComponent.RefundMachine, RuntimeFailure.StartupFailed)
                    return raise (InvalidOperationException "refund client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped RuntimeComponent.RefundMachine
            }
            :> Task

type ReturnMachineClient(context: PostgresContext, loggerFactory: ILoggerFactory, health: RuntimeHealth) =
    let logger = loggerFactory.CreateLogger "ReturnMachineClient"
    let mutable client = None

    member _.Returns =
        client
        |> Option.defaultWith (fun () -> invalidOp "return machine client has not started")

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting RuntimeComponent.ReturnMachine

                let machine =
                    match ReturnCodec.buildClient logger context with
                    | Ok machine -> machine
                    | Error _ ->
                        health.Failed(RuntimeComponent.ReturnMachine, RuntimeFailure.InvalidChart)
                        invalidOp "return chart is invalid"

                match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                | Ok(Startup.Started _) ->
                    client <- Some machine
                    health.Succeeded RuntimeComponent.ReturnMachine
                | Ok(Startup.Refused _) ->
                    health.Failed(RuntimeComponent.ReturnMachine, RuntimeFailure.StartupRefused)
                    return raise (InvalidOperationException "return client refused to boot")
                | Error _ ->
                    health.Failed(RuntimeComponent.ReturnMachine, RuntimeFailure.StartupFailed)
                    return raise (InvalidOperationException "return client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped RuntimeComponent.ReturnMachine
            }
            :> Task

type ReturnWindowScanner
    (
        dataSource: NpgsqlDataSource,
        returns: ReturnMachineClient,
        logger: ILogger<ReturnWindowScanner>,
        health: RuntimeHealth
    ) =
    inherit BackgroundService()
    let owner = $"return-window-{Environment.MachineName}-{Guid.NewGuid():N}"

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try
                        use connection = dataSource.CreateConnection()
                        do! connection.OpenAsync ct

                        use claim =
                            new NpgsqlCommand(
                                "WITH due AS (SELECT return_id FROM fsnix.return_requests WHERE status='pending' AND window_ends_at <= statement_timestamp() AND (lease_until IS NULL OR lease_until < statement_timestamp()) ORDER BY window_ends_at, return_id LIMIT 20 FOR UPDATE SKIP LOCKED) UPDATE fsnix.return_requests r SET lease_owner=@owner,lease_until=statement_timestamp()+interval '30 seconds' FROM due WHERE r.return_id=due.return_id RETURNING r.return_id,r.authorization_id,r.window_ends_at",
                                connection
                            )

                        claim.Parameters.AddWithValue("owner", owner) |> ignore
                        use! reader = claim.ExecuteReaderAsync ct
                        let claimed = ResizeArray<ReturnId * ReturnAuthorizationId * DateTimeOffset>()

                        while! reader.ReadAsync ct do
                            let id = ReturnId.create (reader.GetGuid 0) |> Result.defaultWith invalidOp

                            let auth =
                                ReturnAuthorizationId.create (reader.GetGuid 1) |> Result.defaultWith invalidOp

                            claimed.Add((id, auth, reader.GetFieldValue<DateTimeOffset> 2))

                        reader.Dispose()

                        for (id, auth, deadline) in claimed do
                            let key =
                                $"return-window:{ReturnId.wireString id}:{deadline.ToUnixTimeMilliseconds()}"

                            let! outcome =
                                Machine.enqueue
                                    returns.Returns
                                    (Returns.returnEntityId id)
                                    (EventEnvelope.create key (ReturnWindowExpired(auth, deadline)))
                                    ct

                            use settle =
                                new NpgsqlCommand(
                                    "UPDATE fsnix.return_requests SET status=@status,lease_owner=NULL,lease_until=NULL WHERE return_id=@id AND lease_owner=@owner",
                                    connection
                                )

                            settle.Parameters.AddWithValue("id", ReturnId.value id) |> ignore
                            settle.Parameters.AddWithValue("owner", owner) |> ignore

                            settle.Parameters.AddWithValue(
                                "status",
                                if Result.isOk outcome then "expired" else "pending"
                            )
                            |> ignore

                            let! _ = settle.ExecuteNonQueryAsync ct
                            ()

                        health.Succeeded RuntimeComponent.ReturnWindowScanner
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested -> ()
                    | error ->
                        health.Failed(RuntimeComponent.ReturnWindowScanner, RuntimeFailure.PassFailed)

                        logger.LogError(
                            "return-window scanner pass failed ({ExceptionType})",
                            SafeDiagnostics.exceptionType error
                        )

                    do! Task.Delay(TimeSpan.FromSeconds 5., ct)
            with :? OperationCanceledException ->
                health.Stopped RuntimeComponent.ReturnWindowScanner
        }
        :> Task

type ReservationExpiryScanner
    (
        dataSource: NpgsqlDataSource,
        orders: OrderMachineClient,
        logger: ILogger<ReservationExpiryScanner>,
        health: RuntimeHealth
    ) =
    inherit BackgroundService()
    let suffix = Guid.NewGuid().ToString("N")[..7]

    let options =
        ReservationDeadlines.defaults $"reservation-expiry-{Environment.MachineName}-{suffix}"

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try
                        let! claimed = ReservationDeadlines.claim dataSource options ct

                        for row in claimed do
                            let! gate = ReservationDeadlines.gateState dataSource row.Gate ct

                            if gate = "sent" then
                                let key = $"reservation-expiry:v1:{row.Id}"

                                let! result =
                                    Machine.enqueue
                                        orders.Orders
                                        (entityId row.OrderId)
                                        (EventEnvelope.create key (ReservationExpired(row.Generation, row.Deadline)))
                                        ct

                                match result with
                                | Ok _ -> do! ReservationDeadlines.settle dataSource options row.Id "fired" ct
                                | Error _ -> do! ReservationDeadlines.settle dataSource options row.Id "pending" ct
                            elif gate = "pending" then
                                do! ReservationDeadlines.settle dataSource options row.Id "pending" ct
                            else
                                do! ReservationDeadlines.settle dataSource options row.Id "cancelled" ct

                        health.Succeeded RuntimeComponent.ReservationExpiryScanner
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested -> ()
                    | error ->
                        health.Failed(RuntimeComponent.ReservationExpiryScanner, RuntimeFailure.PassFailed)

                        logger.LogError(
                            "reservation expiry scanner failed ({ExceptionType})",
                            SafeDiagnostics.exceptionType error
                        )

                    do! Task.Delay(TimeSpan.FromMilliseconds 500., ct)
            with :? OperationCanceledException ->
                health.Stopped RuntimeComponent.ReservationExpiryScanner
        }
        :> Task

/// <summary>
/// Fires due cart-abandonment deadlines. Each deadline is armed by the epoch that recorded the
/// cart touch; a stale generation is a no-op inside the cart machine, so a superseded timer can
/// never abandon a cart that has since been touched.
/// </summary>
type CartAbandonmentScanner
    (
        dataSource: NpgsqlDataSource,
        carts: CartMachineClient,
        logger: ILogger<CartAbandonmentScanner>,
        health: RuntimeHealth
    ) =
    inherit BackgroundService()

    let suffix = Guid.NewGuid().ToString("N")[..7]

    let options =
        CartDeadlines.ScannerOptions.defaults $"cart-deadline-scanner-{Environment.MachineName}-{suffix}"

    let processDeadline (deadline: CartDeadlines.ClaimedDeadline) (ct: CancellationToken) =
        task {
            let key = $"cart-abandonment:v1:{deadline.CartId}:{deadline.Generation}"
            let entity = entityId deadline.CartId

            let! outcome =
                Machine.enqueue
                    carts.Carts
                    entity
                    (EventEnvelope.create key (AbandonmentTimerFired(deadline.Generation, deadline.Deadline)))
                    ct

            match outcome with
            | Ok _ -> do! CartDeadlines.fired dataSource options deadline.DeadlineId ct
            | Error _ ->
                logger.LogError("cart deadline {DeadlineId} enqueue failed", deadline.DeadlineId)
                do! CartDeadlines.release dataSource options deadline.DeadlineId ct
        }

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try
                        let! claimed = CartDeadlines.claim dataSource options ct

                        for deadline in claimed do
                            do! processDeadline deadline ct

                        health.Succeeded RuntimeComponent.CartAbandonmentScanner
                    with error ->
                        health.Failed(RuntimeComponent.CartAbandonmentScanner, RuntimeFailure.PassFailed)

                        logger.LogError(
                            "cart abandonment scanner pass failed ({ExceptionType})",
                            SafeDiagnostics.exceptionType error
                        )

                    do! Task.Delay(TimeSpan.FromMilliseconds 500., ct)
            with :? OperationCanceledException ->
                health.Stopped RuntimeComponent.CartAbandonmentScanner
        }
        :> Task

/// <summary>
/// Resumes stalled merges: any snapshot still <c>captured</c> is re-notified to its source cart
/// under a stable idempotency key, so a guest whose capture callback was dead-lettered proceeds
/// to submit the snapshot. A guest that already submitted absorbs the duplicate.
/// </summary>
type CartMergeScanner
    (dataSource: NpgsqlDataSource, carts: CartMachineClient, logger: ILogger<CartMergeScanner>, health: RuntimeHealth) =
    inherit BackgroundService()

    let processSnapshot (snapshot: CartMergeSnapshotRow) (ct: CancellationToken) =
        task {
            let key = $"cart-merge-resume:v1:{snapshot.MergeId:D}:captured"
            let entity = entityId snapshot.SourceCartId

            let! _ =
                Machine.enqueue
                    carts.Carts
                    entity
                    (EventEnvelope.create key (MergeSnapshotCaptured snapshot.MergeId))
                    ct

            ()
        }

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try
                        let! pending = CartMerge.pending dataSource ct

                        for snapshot in pending do
                            do! processSnapshot snapshot ct

                        health.Succeeded RuntimeComponent.CartMergeScanner
                    with error ->
                        health.Failed(RuntimeComponent.CartMergeScanner, RuntimeFailure.PassFailed)

                        logger.LogError(
                            "cart merge scanner pass failed ({ExceptionType})",
                            SafeDiagnostics.exceptionType error
                        )

                    do! Task.Delay(TimeSpan.FromSeconds 5., ct)
            with :? OperationCanceledException ->
                health.Stopped RuntimeComponent.CartMergeScanner
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
