namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

[<RequireQualifiedAccess>]
module ReservationDeadlines =
    type Options =
        { Owner: string
          BatchSize: int
          Lease: TimeSpan }

    type Claimed =
        { Id: int64
          OrderId: string
          Generation: int64
          Deadline: DateTimeOffset
          Gate: string }

    let defaults owner =
        { Owner = owner
          BatchSize = 32
          Lease = TimeSpan.FromMinutes 1. }

    let claim (dataSource: NpgsqlDataSource) options (ct: CancellationToken) : Task<Claimed list> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! transaction = connection.BeginTransactionAsync ct

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.reservation_deadlines target SET lease_owner=@owner,
                lease_until=statement_timestamp()+(@lease * interval '1 second') WHERE target.deadline_id IN
                (SELECT deadline_id FROM fsnix.reservation_deadlines WHERE status='pending' AND deadline<=statement_timestamp()
                 AND (lease_until IS NULL OR lease_until<statement_timestamp()) ORDER BY deadline, deadline_id
                 LIMIT @batch FOR UPDATE SKIP LOCKED)
                RETURNING target.deadline_id,target.order_id,target.generation,target.deadline,target.gate_callback_key""",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("owner", options.Owner) |> ignore

            command.Parameters.AddWithValue("lease", int64 options.Lease.TotalSeconds)
            |> ignore

            command.Parameters.AddWithValue("batch", options.BatchSize) |> ignore
            let! reader = command.ExecuteReaderAsync ct
            let rows = ResizeArray<Claimed>()

            while! reader.ReadAsync ct do
                rows.Add
                    { Id = reader.GetInt64 0
                      OrderId = reader.GetString 1
                      Generation = reader.GetInt64 2
                      Deadline = reader.GetFieldValue<DateTimeOffset> 3
                      Gate = reader.GetString 4 }

            reader.Dispose()
            do! transaction.CommitAsync ct
            return List.ofSeq rows
        }

    let gateState (dataSource: NpgsqlDataSource) key (ct: CancellationToken) =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct

            use command =
                new NpgsqlCommand("SELECT status FROM fsnix.integration_outbox WHERE callback_key=@key", connection)

            command.Parameters.AddWithValue("key", key) |> ignore
            let! value = command.ExecuteScalarAsync ct
            return if isNull value then "unsent" else string value
        }

    let settle (dataSource: NpgsqlDataSource) options id (status: string) (ct: CancellationToken) =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct

            use command =
                new NpgsqlCommand(
                    "UPDATE fsnix.reservation_deadlines SET status=@status, fired_at=CASE WHEN @status='fired' THEN statement_timestamp() ELSE fired_at END, lease_owner=NULL, lease_until=NULL WHERE deadline_id=@id AND lease_owner=@owner AND status='pending'",
                    connection
                )

            command.Parameters.AddWithValue("status", status) |> ignore
            command.Parameters.AddWithValue("id", id) |> ignore
            command.Parameters.AddWithValue("owner", options.Owner) |> ignore
            let! _ = command.ExecuteNonQueryAsync ct
            return ()
        }
