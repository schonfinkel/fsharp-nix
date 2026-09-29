namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

/// <summary>
/// Leased claim/settle over an app-owned due-work ledger (deadlines, render jobs, open gateway
/// operations). Every ledger shares one protocol:
/// <list type="number">
/// <item>a short transaction claims due, unleased <c>pending</c> rows with
/// <c>FOR UPDATE SKIP LOCKED</c> in <c>(due, id)</c> order and stamps a lease;</item>
/// <item>the caller does its work (typically an idempotent <c>Machine.enqueue</c>) outside any
/// transaction and without holding a pooled connection;</item>
/// <item>settlement is fenced on the lease owner and <c>status = 'pending'</c>, so a pass whose
/// lease expired cannot overwrite a newer claimant's result.</item>
/// </list>
/// Table and column names come only from compile-time <see cref="LedgerTable"/> constants, never
/// from input, so interpolating them is not an injection vector.
/// </summary>
type LedgerTable =
    {
        Table: string
        IdColumn: string
        DueColumn: string
        /// <summary>Columns returned after the id, in reader order starting at ordinal 1.</summary>
        Columns: string list
        /// <summary>Assignment applied when the work is done (e.g. <c>status = 'fired', fired_at = ...</c>).</summary>
        Done: string
        /// <summary>Assignment applied when the work became moot.</summary>
        Cancelled: string
        /// <summary>Optional extra predicate on due rows (a constant SQL fragment).</summary>
        Filter: string option
    }

type LeaseOptions =
    { Owner: string
      BatchSize: int
      Lease: TimeSpan }

    static member defaults(owner: string) =
        { Owner = owner
          BatchSize = 32
          Lease = TimeSpan.FromMinutes 1. }

/// <summary>How a claimed row is settled.</summary>
[<RequireQualifiedAccess>]
type Settlement =
    | Done
    | Cancelled
    /// <summary>Return to the pool for the next pass (e.g. the gate is still in flight).</summary>
    | Released
    /// <summary>Return to the pool but not before the given delay (bounded retry backoff).</summary>
    | RetryAfter of TimeSpan

/// <summary>What the gate callback's delivery state says about a due deadline.</summary>
[<RequireQualifiedAccess>]
type GateState =
    | Sent
    | Pending
    | Unsent

[<RequireQualifiedAccess>]
module LeasedLedger =
    let claim
        (dataSource: NpgsqlDataSource)
        (table: LedgerTable)
        (options: LeaseOptions)
        (read: NpgsqlDataReader -> 'Row)
        (ct: CancellationToken)
        : Task<'Row list> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! transaction = connection.BeginTransactionAsync ct

            let returning =
                table.IdColumn :: table.Columns
                |> List.map (fun column -> $"target.{column}")
                |> String.concat ", "

            let filter =
                table.Filter |> Option.map (fun f -> $" AND ({f})") |> Option.defaultValue ""

            use command =
                new NpgsqlCommand(
                    $"""UPDATE {table.Table} AS target
                       SET lease_owner = @owner,
                           lease_until = statement_timestamp() + (@lease_seconds * interval '1 second')
                       WHERE target.{table.IdColumn} IN (
                           SELECT candidate.{table.IdColumn}
                           FROM {table.Table} AS candidate
                           WHERE candidate.status = 'pending'
                             AND candidate.{table.DueColumn} <= statement_timestamp()
                             AND (candidate.lease_until IS NULL
                                  OR candidate.lease_until < statement_timestamp()){filter}
                           ORDER BY candidate.{table.DueColumn}, candidate.{table.IdColumn}
                           LIMIT @batch
                           FOR UPDATE OF candidate SKIP LOCKED)
                       RETURNING {returning}""",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("owner", options.Owner) |> ignore

            command.Parameters.AddWithValue("lease_seconds", int64 options.Lease.TotalSeconds)
            |> ignore

            command.Parameters.AddWithValue("batch", options.BatchSize) |> ignore
            let! reader = command.ExecuteReaderAsync ct
            let rows = ResizeArray<'Row>()

            while! reader.ReadAsync ct do
                rows.Add(read reader)

            do! reader.DisposeAsync()
            do! transaction.CommitAsync ct
            return List.ofSeq rows
        }

    let settle
        (dataSource: NpgsqlDataSource)
        (table: LedgerTable)
        (options: LeaseOptions)
        (id: obj)
        (settlement: Settlement)
        (ct: CancellationToken)
        : Task =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct

            let assignment, delay =
                match settlement with
                | Settlement.Done -> table.Done, None
                | Settlement.Cancelled -> table.Cancelled, None
                | Settlement.Released -> "status = 'pending'", None
                | Settlement.RetryAfter delay -> "status = 'pending'", Some delay

            // A delayed retry keeps the lease stamped until the backoff elapses instead of
            // clearing it, so the claim predicate skips the row until then.
            let lease =
                match delay with
                | Some _ ->
                    "lease_owner = NULL, lease_until = statement_timestamp() + (@delay_seconds * interval '1 second')"
                | None -> "lease_owner = NULL, lease_until = NULL"

            use command =
                new NpgsqlCommand(
                    $"""UPDATE {table.Table}
                       SET {assignment}, {lease}
                       WHERE {table.IdColumn} = @id
                         AND lease_owner = @owner
                         AND status = 'pending'""",
                    connection
                )

            command.Parameters.AddWithValue("id", id) |> ignore
            command.Parameters.AddWithValue("owner", options.Owner) |> ignore

            delay
            |> Option.iter (fun delay ->
                command.Parameters.AddWithValue("delay_seconds", max 1L (int64 delay.TotalSeconds))
                |> ignore)

            let! _ = command.ExecuteNonQueryAsync ct
            ()
        }

    /// <summary>The delivery state of a gate callback in the integration outbox. A deadline must
    /// never overtake the event that armed it: it fires only once that callback is sent.</summary>
    let gateState (dataSource: NpgsqlDataSource) (callbackKey: string) (ct: CancellationToken) : Task<GateState> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct

            use command =
                new NpgsqlCommand(
                    "SELECT status FROM fsnix.integration_outbox WHERE callback_key = @callback_key",
                    connection
                )

            command.Parameters.AddWithValue("callback_key", callbackKey) |> ignore
            let! status = command.ExecuteScalarAsync ct

            return
                match status |> Option.ofObj |> Option.map string with
                | Some "sent" -> GateState.Sent
                | Some "pending" -> GateState.Pending
                | Some _
                | None -> GateState.Unsent
        }

/// <summary>The app's due-work ledgers.</summary>
[<RequireQualifiedAccess>]
module Ledgers =
    let private fired = "status = 'fired', fired_at = statement_timestamp()"

    /// <summary>Account-flow expiry deadlines (gated on the flow's arming callback).</summary>
    let flowDeadlines =
        { Table = "fsnix.flow_deadlines"
          IdColumn = "deadline_id"
          DueColumn = "deadline"
          Columns = [ "flow_id"; "generation"; "deadline"; "gate_callback_key" ]
          Done = fired
          Cancelled = "status = 'cancelled'"
          Filter = None }

    type FlowDeadline =
        { DeadlineId: int64
          FlowId: Guid
          Generation: int
          Deadline: DateTimeOffset
          GateCallbackKey: string }

    let readFlowDeadline (reader: NpgsqlDataReader) =
        { DeadlineId = reader.GetInt64 0
          FlowId = reader.GetGuid 1
          Generation = reader.GetInt32 2
          Deadline = reader.GetFieldValue<DateTimeOffset> 3
          GateCallbackKey = reader.GetString 4 }

    /// <summary>Cart-abandonment deadlines; a stale generation is a no-op in the cart machine.</summary>
    let cartDeadlines =
        { Table = "fsnix.cart_deadlines"
          IdColumn = "deadline_id"
          DueColumn = "deadline"
          Columns = [ "cart_id"; "generation"; "deadline" ]
          Done = fired
          Cancelled = "status = 'cancelled'"
          Filter = None }

    type CartDeadline =
        { DeadlineId: int64
          CartId: string
          Generation: int64
          Deadline: DateTimeOffset }

    let readCartDeadline (reader: NpgsqlDataReader) =
        { DeadlineId = reader.GetInt64 0
          CartId = reader.GetString 1
          Generation = reader.GetInt64 2
          Deadline = reader.GetFieldValue<DateTimeOffset> 3 }

    /// <summary>Stock-reservation expiry deadlines (gated on the reservation callback).</summary>
    let reservationDeadlines =
        { Table = "fsnix.reservation_deadlines"
          IdColumn = "deadline_id"
          DueColumn = "deadline"
          Columns = [ "order_id"; "generation"; "deadline"; "gate_callback_key" ]
          Done = fired
          Cancelled = "status = 'cancelled'"
          Filter = None }

    type ReservationDeadline =
        { DeadlineId: int64
          OrderId: string
          Generation: int64
          Deadline: DateTimeOffset
          GateCallbackKey: string }

    let readReservationDeadline (reader: NpgsqlDataReader) =
        { DeadlineId = reader.GetInt64 0
          OrderId = reader.GetString 1
          Generation = reader.GetInt64 2
          Deadline = reader.GetFieldValue<DateTimeOffset> 3
          GateCallbackKey = reader.GetString 4 }

    /// <summary>Return-window expiry for open RMAs.</summary>
    let returnWindows =
        { Table = "fsnix.return_requests"
          IdColumn = "return_id"
          DueColumn = "window_ends_at"
          Columns = [ "authorization_id"; "window_ends_at" ]
          Done = "status = 'expired'"
          Cancelled = "status = 'closed'"
          Filter = None }

    type ReturnWindow =
        { ReturnId: Guid
          AuthorizationId: Guid
          WindowEndsAt: DateTimeOffset }

    let readReturnWindow (reader: NpgsqlDataReader) =
        { ReturnId = reader.GetGuid 0
          AuthorizationId = reader.GetGuid 1
          WindowEndsAt = reader.GetFieldValue<DateTimeOffset> 2 }
