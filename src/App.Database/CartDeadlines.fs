namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

/// <summary>Leased scanning of the cart-abandonment deadline ledger. The timer fires only for
/// the epoch that armed it; a stale generation is a no-op inside the cart machine.</summary>
[<RequireQualifiedAccess>]
module CartDeadlines =

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
          CartId: string
          Generation: int64
          Deadline: DateTimeOffset }

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
                    """UPDATE fsnix.cart_deadlines AS target
                       SET lease_owner = @owner,
                           lease_until = statement_timestamp() + (@lease_seconds * interval '1 second')
                       WHERE target.deadline_id IN (
                           SELECT candidate.deadline_id
                           FROM fsnix.cart_deadlines AS candidate
                           WHERE candidate.status = 'pending'
                             AND candidate.deadline <= statement_timestamp()
                             AND (candidate.lease_until IS NULL
                                  OR candidate.lease_until < statement_timestamp())
                           ORDER BY candidate.deadline, candidate.deadline_id
                           LIMIT @batch
                           FOR UPDATE OF candidate SKIP LOCKED)
                       RETURNING target.deadline_id, target.cart_id, target.generation, target.deadline""",
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
                      CartId = reader.GetString 1
                      Generation = reader.GetInt64 2
                      Deadline = reader.GetFieldValue<DateTimeOffset> 3 }

            reader.Dispose()
            do! transaction.CommitAsync(ct)
            return List.ofSeq rows
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
                    $"""UPDATE fsnix.cart_deadlines
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

    let release (dataSource: NpgsqlDataSource) (options: ScannerOptions) (deadlineId: int64) (ct: CancellationToken) =
        settle dataSource options deadlineId "status = 'pending'" ct
