namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

/// <summary>
/// Leased scanning of the expiry deadline ledger. A due deadline fires only when its gate —
/// the integration-outbox callback that started or advanced the flow — is durably sent; a gate
/// that failed or was never enqueued cancels the deadline instead, so expiry can never overtake
/// the event that arms the flow.
/// </summary>
[<RequireQualifiedAccess>]
module FlowDeadlines =

    type ScannerOptions =
        { Owner: string
          BatchSize: int
          Lease: TimeSpan }

        static member defaults(owner: string) =
            { Owner = owner
              BatchSize = 32
              Lease = TimeSpan.FromMinutes 1. }

    type ClaimedDeadline =
        { DeadlineId: int64
          FlowId: Guid
          Generation: int
          Deadline: DateTimeOffset
          GateCallbackKey: string }

    /// <summary>What the gate callback's delivery state says about a due deadline.</summary>
    [<RequireQualifiedAccess>]
    type GateState =
        | Sent
        | Pending
        | Unsent

    let claim
        (dataSource: NpgsqlDataSource)
        (options: ScannerOptions)
        (ct: CancellationToken)
        : Task<ClaimedDeadline list> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.flow_deadlines AS target
                       SET lease_owner = @owner,
                           lease_until = statement_timestamp() + (@lease_seconds * interval '1 second')
                       WHERE target.deadline_id IN (
                           SELECT candidate.deadline_id
                           FROM fsnix.flow_deadlines AS candidate
                           WHERE candidate.status = 'pending'
                             AND candidate.deadline <= statement_timestamp()
                             AND (candidate.lease_until IS NULL
                                  OR candidate.lease_until < statement_timestamp())
                           ORDER BY candidate.deadline, candidate.deadline_id
                           LIMIT @batch
                           FOR UPDATE OF candidate SKIP LOCKED)
                       RETURNING target.deadline_id, target.flow_id, target.generation,
                                 target.deadline, target.gate_callback_key""",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("owner", options.Owner) |> ignore

            command.Parameters.AddWithValue("lease_seconds", int64 options.Lease.TotalSeconds)
            |> ignore

            command.Parameters.AddWithValue("batch", options.BatchSize) |> ignore

            let! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ClaimedDeadline>()

            while! reader.ReadAsync(ct) do
                rows.Add
                    { DeadlineId = reader.GetInt64 0
                      FlowId = reader.GetGuid 1
                      Generation = reader.GetInt32 2
                      Deadline = reader.GetFieldValue<DateTimeOffset> 3
                      GateCallbackKey = reader.GetString 4 }

            reader.Dispose()
            do! transaction.CommitAsync(ct)
            return List.ofSeq rows
        }

    let gateState (dataSource: NpgsqlDataSource) (callbackKey: string) (ct: CancellationToken) : Task<GateState> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    "SELECT status FROM fsnix.integration_outbox WHERE callback_key = @callback_key",
                    connection
                )

            command.Parameters.AddWithValue("callback_key", callbackKey) |> ignore
            let! status = command.ExecuteScalarAsync(ct)

            return
                match status |> Option.ofObj |> Option.map string with
                | Some "sent" -> GateState.Sent
                | Some "pending" -> GateState.Pending
                | Some _
                | None -> GateState.Unsent
        }

    let private settle
        (dataSource: NpgsqlDataSource)
        (options: ScannerOptions)
        (deadlineId: int64)
        (assignment: string)
        (ct: CancellationToken)
        : Task =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    $"""UPDATE fsnix.flow_deadlines
                       SET {assignment}, lease_owner = NULL, lease_until = NULL
                       WHERE deadline_id = @deadline_id
                         AND lease_owner = @owner
                         AND status = 'pending'""",
                    connection
                )

            command.Parameters.AddWithValue("deadline_id", deadlineId) |> ignore
            command.Parameters.AddWithValue("owner", options.Owner) |> ignore
            let! _ = command.ExecuteNonQueryAsync(ct)
            ()
        }

    let fired (dataSource: NpgsqlDataSource) (options: ScannerOptions) (deadlineId: int64) (ct: CancellationToken) =
        settle dataSource options deadlineId "status = 'fired', fired_at = statement_timestamp()" ct

    let cancelled (dataSource: NpgsqlDataSource) (options: ScannerOptions) (deadlineId: int64) (ct: CancellationToken) =
        settle dataSource options deadlineId "status = 'cancelled'" ct

    /// <summary>Returns a claimed deadline to the pool without settling it, so the next pass
    /// retries it (for example while its gate callback is still in flight).</summary>
    let release (dataSource: NpgsqlDataSource) (options: ScannerOptions) (deadlineId: int64) (ct: CancellationToken) =
        settle dataSource options deadlineId "status = 'pending'" ct
