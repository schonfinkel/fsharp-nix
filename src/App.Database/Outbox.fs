namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Npgsql

/// <summary>What a delivery attempt concluded for one claimed row.</summary>
type OutboxDeliveryOutcome =
    | Delivered
    | AlreadyDelivered

[<RequireQualifiedAccess>]
type OutboxFailure =
    | DestinationMissing
    | CallbackInvalid
    | DestinationUnavailable
    | DestinationThrew

[<RequireQualifiedAccess>]
module OutboxFailure =
    let wireCode =
        function
        | OutboxFailure.DestinationMissing -> "destination-missing"
        | OutboxFailure.CallbackInvalid -> "callback-invalid"
        | OutboxFailure.DestinationUnavailable -> "destination-unavailable"
        | OutboxFailure.DestinationThrew -> "destination-threw"

/// <summary>
/// One destination machine the relay can deliver to. The typed machine instance and its
/// event codec are closed over at construction, so the relay stays untyped and can never
/// decode one machine's events with another's codec.
/// </summary>
type OutboxDestination =
    { MachineId: string
      Deliver: string * string * string * CancellationToken -> Task<Result<OutboxDeliveryOutcome, OutboxFailure>> }

[<RequireQualifiedAccess>]
module OutboxDestination =

    /// <summary>
    /// Builds a destination from a machine client, its entity-id decoder, and the exact event
    /// codec the machine's store persists. <paramref name="entityOf" /> must be the same
    /// projection the store uses, or rows cannot find their entities.
    /// </summary>
    let forMachineProvider
        (machineIdValue: string)
        (entityOf: string -> 'EntityId)
        (codec: Codec<'Event>)
        (getMachine: unit -> Machine<'EntityId, 'State, 'Event, 'Action, 'Err>)
        : OutboxDestination =
        { MachineId = machineIdValue
          Deliver =
            fun (entityId, eventJson, callbackKey, ct) ->
                task {
                    match codec.Decode eventJson with
                    | Error _ -> return Error OutboxFailure.CallbackInvalid
                    | Ok event ->
                        let machine = getMachine ()

                        let! outcome =
                            Machine.enqueue machine (entityOf entityId) (EventEnvelope.create callbackKey event) ct

                        match outcome with
                        | Ok(SubmissionOutcome.Accepted _) -> return Ok Delivered
                        | Ok(SubmissionOutcome.AlreadySubmitted _) -> return Ok AlreadyDelivered
                        | Error _ -> return Error OutboxFailure.DestinationUnavailable
                } }

    let forMachine
        (machineIdValue: string)
        (entityOf: string -> 'EntityId)
        (codec: Codec<'Event>)
        (machine: Machine<'EntityId, 'State, 'Event, 'Action, 'Err>)
        : OutboxDestination =
        forMachineProvider machineIdValue entityOf codec (fun () -> machine)

/// <summary>
/// Leased, machine-routed delivery of committed local effects. One pass claims a bounded
/// batch under a short transaction (lease + attempt increment), delivers outside the
/// transaction, then completes each row with a lease-fenced update. A relay that dies between
/// claim and completion leaves a leased row that becomes claimable again after the lease
/// expires; redelivery then resolves to <c>AlreadyDelivered</c> on the callback key. Rows that
/// exhaust their attempts become <c>dead</c> and stop consuming workers.
/// </summary>
[<RequireQualifiedAccess>]
module Outbox =

    type RelayOptions =
        { Owner: string
          BatchSize: int
          Lease: TimeSpan
          MaxBackoff: TimeSpan }

        static member defaults(owner: string) =
            { Owner = owner
              BatchSize = 32
              Lease = TimeSpan.FromMinutes 1.
              MaxBackoff = TimeSpan.FromHours 1. }

    type private ClaimedRow =
        { OutboxId: int64
          CallbackKey: string
          MachineId: string
          EntityId: string
          Event: string
          Attempts: int
          MaxAttempts: int }

    let private backoffSeconds (options: RelayOptions) (attempts: int) =
        Math.Min(30. * 2.0 ** float attempts, options.MaxBackoff.TotalSeconds)
        |> fun seconds -> int64 seconds

    let private claim
        (dataSource: NpgsqlDataSource)
        (options: RelayOptions)
        (ct: CancellationToken)
        : Task<ClaimedRow list> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.integration_outbox AS target
                       SET lease_owner = @owner,
                           lease_until = statement_timestamp() + (@lease_seconds * interval '1 second'),
                           attempts = target.attempts + 1
                       WHERE target.outbox_id IN (
                           SELECT candidate.outbox_id
                           FROM fsnix.integration_outbox AS candidate
                           WHERE candidate.status = 'pending'
                             AND candidate.available_at <= statement_timestamp()
                             AND (candidate.lease_until IS NULL
                                  OR candidate.lease_until < statement_timestamp())
                           ORDER BY candidate.available_at, candidate.outbox_id
                           LIMIT @batch
                           FOR UPDATE OF candidate SKIP LOCKED)
                       RETURNING target.outbox_id, target.callback_key, target.machine_id,
                                 target.entity_id, target.event, target.attempts, target.max_attempts""",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("owner", options.Owner) |> ignore

            command.Parameters.AddWithValue("lease_seconds", int64 options.Lease.TotalSeconds)
            |> ignore

            command.Parameters.AddWithValue("batch", options.BatchSize) |> ignore

            let! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ClaimedRow>()

            while! reader.ReadAsync(ct) do
                rows.Add
                    { OutboxId = reader.GetInt64 0
                      CallbackKey = reader.GetString 1
                      MachineId = reader.GetString 2
                      EntityId = reader.GetString 3
                      Event = reader.GetString 4
                      Attempts = reader.GetInt32 5
                      MaxAttempts = reader.GetInt32 6 }

            reader.Dispose()
            do! transaction.CommitAsync(ct)
            return List.ofSeq rows
        }

    let private complete
        (dataSource: NpgsqlDataSource)
        (options: RelayOptions)
        (outboxId: int64)
        (ct: CancellationToken)
        : Task<bool> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.integration_outbox
                       SET status = 'sent', sent_at = statement_timestamp(),
                           lease_owner = NULL, lease_until = NULL, last_error = NULL
                       WHERE outbox_id = @outbox_id
                         AND lease_owner = @owner
                         AND status = 'pending'""",
                    connection
                )

            command.Parameters.AddWithValue("outbox_id", outboxId) |> ignore
            command.Parameters.AddWithValue("owner", options.Owner) |> ignore
            let! affected = command.ExecuteNonQueryAsync(ct)
            return affected = 1
        }

    let private failOnce
        (dataSource: NpgsqlDataSource)
        (options: RelayOptions)
        (row: ClaimedRow)
        (failure: OutboxFailure)
        (ct: CancellationToken)
        : Task =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            let exhausted = row.Attempts >= row.MaxAttempts

            let sql =
                if exhausted then
                    """UPDATE fsnix.integration_outbox
                       SET status = 'dead', failed_at = statement_timestamp(),
                           lease_owner = NULL, lease_until = NULL,
                           last_error = left(@message, 500)
                       WHERE outbox_id = @outbox_id
                         AND lease_owner = @owner
                         AND status = 'pending'"""
                else
                    """UPDATE fsnix.integration_outbox
                       SET available_at = statement_timestamp() + (@backoff_seconds * interval '1 second'),
                            lease_owner = NULL, lease_until = NULL,
                            last_error = left(@message, 500)
                       WHERE outbox_id = @outbox_id
                         AND lease_owner = @owner
                         AND status = 'pending'"""

            use command = new NpgsqlCommand(sql, connection)
            command.Parameters.AddWithValue("outbox_id", row.OutboxId) |> ignore
            command.Parameters.AddWithValue("owner", options.Owner) |> ignore

            command.Parameters.AddWithValue("message", OutboxFailure.wireCode failure)
            |> ignore

            if not exhausted then
                command.Parameters.AddWithValue("backoff_seconds", backoffSeconds options row.Attempts)
                |> ignore

            let! _ = command.ExecuteNonQueryAsync(ct)
            ()
        }

    /// <summary>
    /// One relay pass: claim, deliver, complete. Returns the number of rows newly or already
    /// durably accepted. Unknown machine ids and decode failures count as failed attempts and
    /// eventually dead-letter, so a misrouted row can never silently vanish into another
    /// machine's queue.
    /// </summary>
    let deliverPending
        (dataSource: NpgsqlDataSource)
        (options: RelayOptions)
        (destinations: OutboxDestination list)
        (ct: CancellationToken)
        : Task<int> =
        task {
            let! claimed = claim dataSource options ct

            let byMachine =
                destinations
                |> List.map (fun destination -> destination.MachineId, destination)
                |> Map.ofList

            let mutable delivered = 0

            for row in claimed do
                match byMachine |> Map.tryFind row.MachineId with
                | None -> do! failOnce dataSource options row OutboxFailure.DestinationMissing ct
                | Some destination ->
                    let! outcome =
                        task {
                            try
                                return! destination.Deliver(row.EntityId, row.Event, row.CallbackKey, ct)
                            with
                            | :? OperationCanceledException when ct.IsCancellationRequested ->
                                return raise (OperationCanceledException ct)
                            | _ -> return Error OutboxFailure.DestinationThrew
                        }

                    match outcome with
                    | Ok _ ->
                        let! completed = complete dataSource options row.OutboxId ct

                        if completed then
                            delivered <- delivered + 1
                    | Error failure -> do! failOnce dataSource options row failure ct

            return delivered
        }
