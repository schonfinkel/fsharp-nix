namespace App

open System
open System.Threading
open System.Threading.Tasks
open App.Cart
open App.Database
open App.Domain
open App.Orders
open App.Auth
open App.Returns
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.DataProtection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql

/// <summary>Stable idempotency keys for timer and sweep events:
/// <c>timer:v1:{machine}:{entity}:{kind}:{generation}</c>. The destination inbox deduplicates a
/// repeat (crash between enqueue and ledger settlement), and the machine revalidates the carried
/// generation, so a stale timer is a no-op.</summary>
[<RequireQualifiedAccess>]
module TimerKey =
    let create (machine: string) (entity: string) (kind: string) (generation: int64) =
        $"timer:v1:{machine}:{entity}:{kind}:{generation}"

/// <summary>
/// A bounded, observable, concurrency-safe background pass loop. Subclasses implement one pass;
/// the base owns the delay (with jitter so replicas do not synchronise), health reporting, and
/// shutdown: cancellation during shutdown is never reported as a failure.
/// </summary>
[<AbstractClass>]
type PeriodicWorker(service: RuntimeComponent, interval: TimeSpan, logger: ILogger, health: RuntimeHealth) =
    inherit BackgroundService()

    /// <summary>One bounded pass. Must not hold a pooled connection across machine calls.</summary>
    abstract RunPass: CancellationToken -> Task

    override this.ExecuteAsync(ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try
                        do! this.RunPass ct
                        health.Succeeded service
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested -> ()
                    | error ->
                        health.Failed(service, RuntimeFailure.PassFailed)

                        logger.LogError(
                            "{Component} pass failed ({ExceptionType})",
                            RuntimeComponent.code service,
                            SafeDiagnostics.exceptionType error
                        )

                    let jitter = Random.Shared.NextDouble() * 0.2 * interval.TotalMilliseconds
                    do! Task.Delay(interval + TimeSpan.FromMilliseconds jitter, ct)
            with :? OperationCanceledException ->
                ()

            health.Stopped service
        }
        :> Task

[<RequireQualifiedAccess>]
module private ScannerOwner =
    let create prefix =
        $"{prefix}-{Environment.MachineName}-{Guid.NewGuid():N}"

/// <summary>Converts a machine enqueue outcome into a ledger settlement: accepted (or an
/// already-submitted repeat) is done; a refused enqueue retries on the next pass.</summary>
[<RequireQualifiedAccess>]
module private Enqueued =
    let settlement (outcome: Result<_, _>) =
        match outcome with
        | Ok _ -> Settlement.Done
        | Error _ -> Settlement.Released

/// <summary>Fires due return-window deadlines into the return machine.</summary>
type ReturnWindowScanner
    (dataSource: NpgsqlDataSource, returns: ReturnMachineClient, logger: ILogger<ReturnWindowScanner>, health) =
    inherit PeriodicWorker(RuntimeComponent.ReturnWindowScanner, TimeSpan.FromSeconds 5., logger, health)

    let options = LeaseOptions.defaults (ScannerOwner.create "return-window")

    override _.RunPass ct =
        task {
            let! claimed = LeasedLedger.claim dataSource Ledgers.returnWindows options Ledgers.readReturnWindow ct

            for row in claimed do
                let! settlement =
                    match ReturnId.create row.ReturnId, ReturnAuthorizationId.create row.AuthorizationId with
                    | Ok id, Ok authorization ->
                        task {
                            let entity = Returns.returnEntityId id

                            let key =
                                TimerKey.create
                                    Returns.MachineKey
                                    (EntityId.value entity)
                                    "return-window"
                                    (row.WindowEndsAt.ToUnixTimeMilliseconds())

                            let! outcome =
                                Machine.enqueue
                                    returns.Returns
                                    entity
                                    (EventEnvelope.create key (ReturnWindowExpired(authorization, row.WindowEndsAt)))
                                    ct

                            return Enqueued.settlement outcome
                        }
                    | _ -> Task.FromResult Settlement.Cancelled

                do! LeasedLedger.settle dataSource Ledgers.returnWindows options row.ReturnId settlement ct
        }

/// <summary>Fires due stock-reservation expiry deadlines once their arming callback is sent.</summary>
type ReservationExpiryScanner
    (dataSource: NpgsqlDataSource, orders: OrderMachineClient, logger: ILogger<ReservationExpiryScanner>, health) =
    inherit PeriodicWorker(RuntimeComponent.ReservationExpiryScanner, TimeSpan.FromMilliseconds 500., logger, health)

    let options = LeaseOptions.defaults (ScannerOwner.create "reservation-expiry")

    override _.RunPass ct =
        task {
            let! claimed =
                LeasedLedger.claim dataSource Ledgers.reservationDeadlines options Ledgers.readReservationDeadline ct

            for row in claimed do
                let! gate = LeasedLedger.gateState dataSource row.GateCallbackKey ct

                let! settlement =
                    match gate with
                    | GateState.Sent ->
                        task {
                            let key =
                                TimerKey.create Orders.MachineKey row.OrderId "reservation-expiry" row.Generation

                            let! outcome =
                                Machine.enqueue
                                    orders.Orders
                                    (entityId row.OrderId)
                                    (EventEnvelope.create key (ReservationExpired(row.Generation, row.Deadline)))
                                    ct

                            return Enqueued.settlement outcome
                        }
                    | GateState.Pending -> Task.FromResult Settlement.Released
                    | GateState.Unsent -> Task.FromResult Settlement.Cancelled

                do! LeasedLedger.settle dataSource Ledgers.reservationDeadlines options row.DeadlineId settlement ct
        }

/// <summary>
/// Fires due cart-abandonment deadlines. Each deadline is armed by the epoch that recorded the
/// cart touch; a stale generation is a no-op inside the cart machine, so a superseded timer can
/// never abandon a cart that has since been touched.
/// </summary>
type CartAbandonmentScanner
    (dataSource: NpgsqlDataSource, carts: CartMachineClient, logger: ILogger<CartAbandonmentScanner>, health) =
    inherit PeriodicWorker(RuntimeComponent.CartAbandonmentScanner, TimeSpan.FromMilliseconds 500., logger, health)

    let options = LeaseOptions.defaults (ScannerOwner.create "cart-deadline")

    override _.RunPass ct =
        task {
            let! claimed = LeasedLedger.claim dataSource Ledgers.cartDeadlines options Ledgers.readCartDeadline ct

            for row in claimed do
                let key = TimerKey.create Cart.MachineKey row.CartId "abandonment" row.Generation

                let! outcome =
                    Machine.enqueue
                        carts.Carts
                        (entityId row.CartId)
                        (EventEnvelope.create key (AbandonmentTimerFired(row.Generation, row.Deadline)))
                        ct

                do!
                    LeasedLedger.settle
                        dataSource
                        Ledgers.cartDeadlines
                        options
                        row.DeadlineId
                        (Enqueued.settlement outcome)
                        ct
        }

/// <summary>
/// Resumes stalled merges: any snapshot still <c>captured</c> is re-notified to its source cart
/// under a stable idempotency key, so a guest whose capture callback was dead-lettered proceeds
/// to submit the snapshot. A guest that already submitted absorbs the duplicate.
/// </summary>
type CartMergeScanner(dataSource: NpgsqlDataSource, carts: CartMachineClient, logger: ILogger<CartMergeScanner>, health)
    =
    inherit PeriodicWorker(RuntimeComponent.CartMergeScanner, TimeSpan.FromSeconds 5., logger, health)

    override _.RunPass ct =
        task {
            let! pending = CartMerge.pending dataSource ct
            let mutable refused = 0

            for snapshot in pending do
                let key = $"cart-merge-resume:v1:{snapshot.MergeId:D}:captured"

                let! outcome =
                    Machine.enqueue
                        carts.Carts
                        (entityId snapshot.SourceCartId)
                        (EventEnvelope.create key (MergeSnapshotCaptured snapshot.MergeId))
                        ct

                if Result.isError outcome then
                    refused <- refused + 1

            if refused > 0 then
                logger.LogWarning("cart merge scanner: {Refused} resume enqueue(s) refused", refused)
        }

type IntegrationOutboxRelay
    (dataSource: NpgsqlDataSource, destinations: OutboxDestination list, logger: ILogger<IntegrationOutboxRelay>, health)
    =
    inherit PeriodicWorker(RuntimeComponent.IntegrationOutboxRelay, TimeSpan.FromMilliseconds 500., logger, health)

    let options = Outbox.RelayOptions.defaults (ScannerOwner.create "relay")

    override _.RunPass ct =
        task {
            let! _ = Outbox.deliverPending dataSource options destinations ct
            ()
        }

/// <summary>
/// Fires due flow-expiry deadlines into the flows machine. A deadline whose gate callback is
/// durably sent becomes <c>ExpiryTimerFired</c>; one whose gate failed or was never enqueued is
/// cancelled; one whose gate is still in flight is released for the next pass.
/// </summary>
type FlowDeadlineScanner
    (dataSource: NpgsqlDataSource, flows: AccountFlowMachineClient, logger: ILogger<FlowDeadlineScanner>, health) =
    inherit PeriodicWorker(RuntimeComponent.FlowDeadlineScanner, TimeSpan.FromMilliseconds 500., logger, health)

    let options = LeaseOptions.defaults (ScannerOwner.create "flow-deadline")

    override _.RunPass ct =
        task {
            let! claimed = LeasedLedger.claim dataSource Ledgers.flowDeadlines options Ledgers.readFlowDeadline ct

            for row in claimed do
                let! gate = LeasedLedger.gateState dataSource row.GateCallbackKey ct

                let! settlement =
                    match gate with
                    | GateState.Sent ->
                        task {
                            let entity = row.FlowId.ToString("D")

                            let key =
                                TimerKey.create AccountFlow.MachineKey entity "expiry" (int64 row.Generation)

                            let! outcome =
                                Machine.enqueue
                                    flows.Flows
                                    (entityId entity)
                                    (EventEnvelope.create key (ExpiryTimerFired(row.Generation, row.Deadline)))
                                    ct

                            return Enqueued.settlement outcome
                        }
                    | GateState.Pending -> Task.FromResult Settlement.Released
                    | GateState.Unsent -> Task.FromResult Settlement.Cancelled

                do! LeasedLedger.settle dataSource Ledgers.flowDeadlines options row.DeadlineId settlement ct
        }

/// <summary>The hosted relay: bounded passes over <c>fsnix.account_email_outbox</c> with the
/// same failure isolation as the integration-outbox relay.</summary>
type EmailDeliveryRelay
    (
        dataSource: NpgsqlDataSource,
        dataProtection: IDataProtectionProvider,
        transport: IEmailTransport,
        options: EmailOptions,
        logger: ILogger<EmailDeliveryRelay>,
        health
    ) =
    inherit PeriodicWorker(RuntimeComponent.EmailDeliveryRelay, TimeSpan.FromMilliseconds 500., logger, health)

    let relayOptions =
        EmailOutbox.RelayOptions.defaults (ScannerOwner.create "email-relay")

    override _.RunPass ct =
        task {
            let! _ = EmailDelivery.deliverPending dataSource dataProtection transport options relayOptions ct
            ()
        }
