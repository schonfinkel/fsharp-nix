namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

[<RequireQualifiedAccess>]
module LedgerSql =
    let cartDeadlinesClaim = Sql.load "Ledgers/cart-deadlines-claim"
    let cartDeadlinesSettle = Sql.load "Ledgers/cart-deadlines-settle"
    let flowDeadlinesClaim = Sql.load "Ledgers/flow-deadlines-claim"
    let gatewayUnknownClaim = Sql.load "Ledgers/gateway-unknown-claim"
    let gatewayUnknownSettle = Sql.load "Ledgers/gateway-unknown-settle"
    let invoiceRendersClaim = Sql.load "Ledgers/invoice-renders-claim"
    let invoiceRendersSettle = Sql.load "Ledgers/invoice-renders-settle"
    let paymentDeadlinesClaim = Sql.load "Ledgers/payment-deadlines-claim"
    let paymentDeadlinesSettle = Sql.load "Ledgers/payment-deadlines-settle"
    let flowDeadlinesSettle = Sql.load "Ledgers/flow-deadlines-settle"
    let gateState = Sql.load "Ledgers/gate-state"
    let reservationDeadlinesClaim = Sql.load "Ledgers/reservation-deadlines-claim"
    let reservationDeadlinesSettle = Sql.load "Ledgers/reservation-deadlines-settle"
    let returnWindowsClaim = Sql.load "Ledgers/return-windows-claim"
    let returnWindowsSettle = Sql.load "Ledgers/return-windows-settle"

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
/// Each ledger's claim and settle statements are explicit files under <c>Sql/Ledgers</c>.
/// </summary>
type LedgerTable =
    {
        /// <summary>Claims due rows; returns the id first, then the ledger's columns.</summary>
        Claim: string
        /// <summary>Settles one claimed row by <c>@id</c>, <c>@owner</c>, <c>@outcome</c> and the
        /// nullable <c>@delay_seconds</c>.</summary>
        Settle: string
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

/// <summary>How a claimed unknown-outcome check is settled.</summary>
[<RequireQualifiedAccess>]
type CheckSettlement =
    /// <summary>The check event was accepted; the next one is due after the delay.</summary>
    | Next of TimeSpan
    /// <summary>The exhausted event was accepted; stop scheduling checks.</summary>
    | Parked
    /// <summary>The event was not accepted; keep the row due for the next pass.</summary>
    | Retry

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

            use command = new NpgsqlCommand(table.Claim, connection, transaction)

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

            let outcome, delay =
                match settlement with
                | Settlement.Done -> "done", None
                | Settlement.Cancelled -> "cancelled", None
                | Settlement.Released -> "released", None
                | Settlement.RetryAfter delay -> "released", Some(max 1L (int64 delay.TotalSeconds))

            use command = new NpgsqlCommand(table.Settle, connection)
            command.Parameters.AddWithValue("id", id) |> ignore
            command.Parameters.AddWithValue("owner", options.Owner) |> ignore
            command.Parameters.AddWithValue("outcome", outcome) |> ignore

            command.Parameters.Add(
                NpgsqlParameter(
                    "delay_seconds",
                    NpgsqlTypes.NpgsqlDbType.Bigint,
                    Value = (delay |> Option.map box |> Option.defaultValue DBNull.Value)
                )
            )
            |> ignore

            let! _ = command.ExecuteNonQueryAsync ct
            ()
        }

    /// <summary>Settles one claimed unknown-outcome check (<c>Ledgers.gatewayUnknown</c>).</summary>
    let settleCheck
        (dataSource: NpgsqlDataSource)
        (options: LeaseOptions)
        (operationId: string)
        (settlement: CheckSettlement)
        (ct: CancellationToken)
        : Task =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct

            let advance, next =
                match settlement with
                | CheckSettlement.Next delay -> true, Some(max 1L (int64 delay.TotalSeconds))
                | CheckSettlement.Parked -> true, None
                | CheckSettlement.Retry -> false, None

            use command = new NpgsqlCommand(LedgerSql.gatewayUnknownSettle, connection)
            command.Parameters.AddWithValue("id", operationId) |> ignore
            command.Parameters.AddWithValue("owner", options.Owner) |> ignore
            command.Parameters.AddWithValue("advance", advance) |> ignore

            command.Parameters.Add(
                NpgsqlParameter(
                    "next_check_seconds",
                    NpgsqlTypes.NpgsqlDbType.Bigint,
                    Value = (next |> Option.map box |> Option.defaultValue DBNull.Value)
                )
            )
            |> ignore

            let! _ = command.ExecuteNonQueryAsync ct
            ()
        }

    /// <summary>The delivery state of a gate callback in the integration outbox. A deadline must
    /// never overtake the event that armed it: it fires only once that callback is sent.</summary>
    let gateState (dataSource: NpgsqlDataSource) (callbackKey: string) (ct: CancellationToken) : Task<GateState> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct

            use command = new NpgsqlCommand(LedgerSql.gateState, connection)

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
    /// <summary>Account-flow expiry deadlines (gated on the flow's arming callback).</summary>
    let flowDeadlines =
        { Claim = LedgerSql.flowDeadlinesClaim
          Settle = LedgerSql.flowDeadlinesSettle }

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
        { Claim = LedgerSql.cartDeadlinesClaim
          Settle = LedgerSql.cartDeadlinesSettle }

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
        { Claim = LedgerSql.reservationDeadlinesClaim
          Settle = LedgerSql.reservationDeadlinesSettle }

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
        { Claim = LedgerSql.returnWindowsClaim
          Settle = LedgerSql.returnWindowsSettle }

    type ReturnWindow =
        { ReturnId: Guid
          AuthorizationId: Guid
          WindowEndsAt: DateTimeOffset }

    let readReturnWindow (reader: NpgsqlDataReader) =
        { ReturnId = reader.GetGuid 0
          AuthorizationId = reader.GetGuid 1
          WindowEndsAt = reader.GetFieldValue<DateTimeOffset> 2 }

    /// <summary>Provider calls with an unknown outcome, due for another gateway check. Only
    /// <c>Claim</c> is used; settlement goes through <c>LeasedLedger.settleCheck</c>.</summary>
    let gatewayUnknown =
        { Claim = LedgerSql.gatewayUnknownClaim
          Settle = LedgerSql.gatewayUnknownSettle }

    type UnknownOperation =
        { OperationId: string
          Machine: string
          Entity: string
          Checks: int }

    let readUnknownOperation (reader: NpgsqlDataReader) =
        { OperationId = reader.GetString 0
          Machine = reader.GetString 1
          Entity = reader.GetString 2
          Checks = reader.GetInt32 3 }

    /// <summary>Authorization expiry deadlines (gated on the authorization's success callback).</summary>
    let paymentDeadlines =
        { Claim = LedgerSql.paymentDeadlinesClaim
          Settle = LedgerSql.paymentDeadlinesSettle }

    type PaymentDeadline =
        { DeadlineId: int64
          PaymentEntityId: string
          OperationId: string
          Deadline: DateTimeOffset
          GateCallbackKey: string }

    let readPaymentDeadline (reader: NpgsqlDataReader) =
        { DeadlineId = reader.GetInt64 0
          PaymentEntityId = reader.GetString 1
          OperationId = reader.GetString 2
          Deadline = reader.GetFieldValue<DateTimeOffset> 3
          GateCallbackKey = reader.GetString 4 }

    /// <summary>Invoice render checks; <c>RetryAfter</c> counts one re-render request.</summary>
    let invoiceRenders =
        { Claim = LedgerSql.invoiceRendersClaim
          Settle = LedgerSql.invoiceRendersSettle }

    type InvoiceRenderCheck =
        { InvoiceId: Guid
          Checks: int
          HasDocument: bool }

    let readInvoiceRenderCheck (reader: NpgsqlDataReader) =
        { InvoiceId = reader.GetGuid 0
          Checks = reader.GetInt32 1
          HasDocument = reader.GetBoolean 2 }
