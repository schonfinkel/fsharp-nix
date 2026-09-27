namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

type CartMergeSnapshotRow =
    { MergeId: Guid
      SourceCartId: string
      TargetCustomerId: string
      LinesJson: string
      Status: string }

/// <summary>Immutable merge-snapshot store. Captures are insert-once keyed by MergeId; the
/// reconciler lists captured-but-unapplied snapshots to resume a lost handoff.</summary>
[<RequireQualifiedAccess>]
module CartMerge =

    type CaptureOutcome =
        | Captured
        | AlreadyCaptured

    let capture
        (dataSource: NpgsqlDataSource)
        (mergeId: Guid)
        (sourceCartId: string)
        (targetCustomerId: string)
        (linesJson: string)
        (ct: CancellationToken)
        : Task<CaptureOutcome> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.cart_merge_snapshots
                           (merge_id, source_cart_id, target_customer_id, lines, status)
                       VALUES (@merge_id, @source_cart_id, @target_customer_id, @lines::jsonb, 'captured')
                       ON CONFLICT (merge_id) DO NOTHING
                       RETURNING merge_id""",
                    connection
                )

            command.Parameters.AddWithValue("merge_id", mergeId) |> ignore
            command.Parameters.AddWithValue("source_cart_id", sourceCartId) |> ignore

            command.Parameters.AddWithValue("target_customer_id", targetCustomerId)
            |> ignore

            command.Parameters.AddWithValue("lines", linesJson) |> ignore
            let! inserted = command.ExecuteScalarAsync(ct)

            return
                if isNull inserted then
                    CaptureOutcome.AlreadyCaptured
                else
                    CaptureOutcome.Captured
        }

    let tryLoad
        (dataSource: NpgsqlDataSource)
        (mergeId: Guid)
        (ct: CancellationToken)
        : Task<CartMergeSnapshotRow option> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """SELECT merge_id, source_cart_id, target_customer_id, lines::text, status
                       FROM fsnix.cart_merge_snapshots
                       WHERE merge_id = @merge_id""",
                    connection
                )

            command.Parameters.AddWithValue("merge_id", mergeId) |> ignore
            let! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            let row =
                if found then
                    Some
                        { MergeId = reader.GetGuid 0
                          SourceCartId = reader.GetString 1
                          TargetCustomerId = reader.GetString 2
                          LinesJson = reader.GetString 3
                          Status = reader.GetString 4 }
                else
                    None

            reader.Dispose()
            return row
        }

    let markApplied (dataSource: NpgsqlDataSource) (mergeId: Guid) (ct: CancellationToken) : Task =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.cart_merge_snapshots
                       SET status = 'applied', applied_at = statement_timestamp()
                       WHERE merge_id = @merge_id AND status = 'captured'""",
                    connection
                )

            command.Parameters.AddWithValue("merge_id", mergeId) |> ignore
            let! _ = command.ExecuteNonQueryAsync(ct)
            ()
        }

    let pending (dataSource: NpgsqlDataSource) (ct: CancellationToken) : Task<CartMergeSnapshotRow list> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """SELECT merge_id, source_cart_id, target_customer_id, lines::text, status
                       FROM fsnix.cart_merge_snapshots
                       WHERE status = 'captured'
                       ORDER BY captured_at, merge_id""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<CartMergeSnapshotRow>()

            while! reader.ReadAsync(ct) do
                rows.Add
                    { MergeId = reader.GetGuid 0
                      SourceCartId = reader.GetString 1
                      TargetCustomerId = reader.GetString 2
                      LinesJson = reader.GetString 3
                      Status = reader.GetString 4 }

            reader.Dispose()
            return List.ofSeq rows
        }
