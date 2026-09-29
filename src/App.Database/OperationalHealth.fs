namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

type OutboxHealthSnapshot =
    { Total: int64
      Pending: int64
      Claimable: int64
      Leased: int64
      Sent: int64
      Dead: int64
      OldestPendingAt: DateTimeOffset option
      NextAvailableAt: DateTimeOffset option
      MaximumPendingAttempts: int option }

type FlowRequestHealthSnapshot =
    { Kind: string
      Status: string
      Count: int64
      ExpiredRequested: int64
      OldestCreatedAt: DateTimeOffset
      EarliestRequestedExpiry: DateTimeOffset option
      LatestUpdatedAt: DateTimeOffset }

type DeadlineHealthSnapshot =
    { Kind: string
      Status: string
      Count: int64
      Due: int64
      Leased: int64
      EarliestPendingDeadline: DateTimeOffset option
      LatestFiredAt: DateTimeOffset option }

type OperationalHealthSnapshot =
    { CapturedAt: DateTimeOffset
      IntegrationOutbox: OutboxHealthSnapshot
      EmailOutbox: OutboxHealthSnapshot
      FlowRequests: FlowRequestHealthSnapshot list
      Deadlines: DeadlineHealthSnapshot list }

[<RequireQualifiedAccess>]
module OperationalHealthSql =
    let healthSnapshot = Sql.load "Operations/health-snapshot"

[<RequireQualifiedAccess>]
module OperationalHealth =
    let private optionalTime (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            None
        else
            Some(reader.GetFieldValue<DateTimeOffset> ordinal)

    let private optionalInt (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            None
        else
            Some(reader.GetInt32 ordinal)

    let private readOutbox (reader: NpgsqlDataReader) =
        { Total = reader.GetInt64 0
          Pending = reader.GetInt64 1
          Claimable = reader.GetInt64 2
          Leased = reader.GetInt64 3
          Sent = reader.GetInt64 4
          Dead = reader.GetInt64 5
          OldestPendingAt = optionalTime reader 6
          NextAvailableAt = optionalTime reader 7
          MaximumPendingAttempts = optionalInt reader 8 }

    let capture (dataSource: NpgsqlDataSource) (ct: CancellationToken) : Task<OperationalHealthSnapshot> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command = new NpgsqlCommand(OperationalHealthSql.healthSnapshot, connection)

            let! reader = command.ExecuteReaderAsync(ct)
            let! capturedRow = reader.ReadAsync(ct)

            if not capturedRow then
                invalidOp "operational health capture returned no timestamp"

            let capturedAt = reader.GetFieldValue<DateTimeOffset> 0
            let! hasIntegration = reader.NextResultAsync(ct)
            let! integrationRow = reader.ReadAsync(ct)

            if not hasIntegration || not integrationRow then
                invalidOp "operational health capture returned no integration outbox row"

            let integration = readOutbox reader
            let! hasEmail = reader.NextResultAsync(ct)
            let! emailRow = reader.ReadAsync(ct)

            if not hasEmail || not emailRow then
                invalidOp "operational health capture returned no email outbox row"

            let email = readOutbox reader
            let! _ = reader.NextResultAsync(ct)
            let flowRequests = ResizeArray<FlowRequestHealthSnapshot>()

            while! reader.ReadAsync(ct) do
                flowRequests.Add
                    { Kind = reader.GetString 0
                      Status = reader.GetString 1
                      Count = reader.GetInt64 2
                      ExpiredRequested = reader.GetInt64 3
                      OldestCreatedAt = reader.GetFieldValue<DateTimeOffset> 4
                      EarliestRequestedExpiry = optionalTime reader 5
                      LatestUpdatedAt = reader.GetFieldValue<DateTimeOffset> 6 }

            let! _ = reader.NextResultAsync(ct)
            let deadlines = ResizeArray<DeadlineHealthSnapshot>()

            while! reader.ReadAsync(ct) do
                deadlines.Add
                    { Kind = reader.GetString 0
                      Status = reader.GetString 1
                      Count = reader.GetInt64 2
                      Due = reader.GetInt64 3
                      Leased = reader.GetInt64 4
                      EarliestPendingDeadline = optionalTime reader 5
                      LatestFiredAt = optionalTime reader 6 }

            return
                { CapturedAt = capturedAt
                  IntegrationOutbox = integration
                  EmailOutbox = email
                  FlowRequests = List.ofSeq flowRequests
                  Deadlines = List.ofSeq deadlines }
        }
