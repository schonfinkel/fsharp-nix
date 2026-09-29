namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Auth
open Npgsql

/// <summary>One claimed protected email awaiting delivery. The payload leaves this record only
/// to be unprotected and sent; it is erased from the database the moment the row reaches a
/// terminal state.</summary>
type ClaimedEmail =
    { EmailId: int64
      FlowId: Guid
      Generation: int
      ProtectedPayload: byte[]
      EncryptionVersion: int
      Attempts: int
      MaxAttempts: int }

[<RequireQualifiedAccess>]
module EmailOutboxSql =
    let claimEmails = Sql.load "Accounts/claim-emails"
    let deadLetterEmail = Sql.load "Accounts/dead-letter-email"
    let releaseEmailRetry = Sql.load "Accounts/release-email-retry"
    let settleEmailSent = Sql.load "Accounts/settle-email-sent"

/// <summary>
/// Leased delivery of pending account emails. Claiming follows the same protocol as the
/// integration-outbox relay: a short transaction leases a bounded batch and increments the
/// attempt count, delivery happens outside any database transaction, and settlement is a
/// lease-fenced update. Sending an email is an external effect, so the settlement transaction
/// pairs the terminal row update with the machine callback insert — the flow machine learns
/// about the outcome through <c>fsnix.integration_outbox</c>, never through a shared call.
/// </summary>
[<RequireQualifiedAccess>]
module EmailOutbox =

    type RelayOptions =
        { Owner: string
          BatchSize: int
          Lease: TimeSpan
          MaxBackoff: TimeSpan }

        static member defaults(owner: string) =
            { Owner = owner
              BatchSize = 16
              Lease = TimeSpan.FromMinutes 1.
              MaxBackoff = TimeSpan.FromHours 1. }

    /// <summary>Stable callback keys for the delivery outcome of one notification generation.
    /// A relay crash after SMTP acceptance but before settlement replays to the same key, so
    /// duplicate delivery resolves to <c>AlreadySubmitted</c> in the flows machine.</summary>
    let sentCallbackKey (flowId: Guid) (generation: int) =
        $"email:v1:{flowId:D}:{generation}:sent"

    let failedCallbackKey (flowId: Guid) (generation: int) =
        $"email:v1:{flowId:D}:{generation}:failed"

    let private entityIdString (flowId: Guid) = flowId.ToString("D")

    let private backoffSeconds (options: RelayOptions) (attempts: int) =
        Math.Min(30. * 2.0 ** float attempts, options.MaxBackoff.TotalSeconds)
        |> fun seconds -> int64 seconds

    /// <summary>Claims a bounded batch of pending emails: leased, attempt-incremented, and
    /// skipped by competing relays until the lease expires.</summary>
    let claim (dataSource: NpgsqlDataSource) (options: RelayOptions) (ct: CancellationToken) : Task<ClaimedEmail list> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            use command = new NpgsqlCommand(EmailOutboxSql.claimEmails, connection, transaction)

            command.Parameters.AddWithValue("owner", options.Owner) |> ignore

            command.Parameters.AddWithValue("lease_seconds", int64 options.Lease.TotalSeconds)
            |> ignore

            command.Parameters.AddWithValue("batch", options.BatchSize) |> ignore

            let! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ClaimedEmail>()

            while! reader.ReadAsync(ct) do
                rows.Add
                    { EmailId = reader.GetInt64 0
                      FlowId = reader.GetGuid 1
                      Generation = reader.GetInt32 2
                      ProtectedPayload = reader.GetValue(3) :?> byte[]
                      EncryptionVersion = reader.GetInt32 4
                      Attempts = reader.GetInt32 5
                      MaxAttempts = reader.GetInt32 6 }

            reader.Dispose()
            do! transaction.CommitAsync(ct)
            return List.ofSeq rows
        }

    let private encodeCallback (event: FlowEvent) : Result<string, string> =
        AccountFlowCodec.event.Encode event
        |> Result.mapError (fun error -> $"delivery callback failed to encode: %A{error}")

    let private insertCallback
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (callbackKey: string)
        (flowId: Guid)
        (eventJson: string)
        (ct: CancellationToken)
        : Task =
        WorkflowEffects.callback
            connection
            transaction
            callbackKey
            AccountFlow.MachineKey
            (entityIdString flowId)
            eventJson
            ct

    /// <summary>Settles a claimed email as sent: erases the protected payload and enqueues the
    /// generation-matched <c>NotificationSent</c> callback in one transaction. Returns
    /// <c>false</c> when the lease fence no longer matches (another relay owns the row), in
    /// which case no callback is written.</summary>
    let settleSent
        (dataSource: NpgsqlDataSource)
        (options: RelayOptions)
        (email: ClaimedEmail)
        (ct: CancellationToken)
        : Task<Result<bool, string>> =
        task {
            match encodeCallback (NotificationSent email.Generation) with
            | Error message -> return Error message
            | Ok eventJson ->
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync(ct)
                use! transaction = connection.BeginTransactionAsync(ct)

                use settle =
                    new NpgsqlCommand(EmailOutboxSql.settleEmailSent, connection, transaction)

                settle.Parameters.AddWithValue("email_id", email.EmailId) |> ignore
                settle.Parameters.AddWithValue("owner", options.Owner) |> ignore
                let! updated = settle.ExecuteNonQueryAsync(ct)

                if updated = 1 then
                    do!
                        insertCallback
                            connection
                            transaction
                            (sentCallbackKey email.FlowId email.Generation)
                            email.FlowId
                            eventJson
                            ct

                do! transaction.CommitAsync(ct)
                return Ok(updated = 1)
        }

    /// <summary>Records one failed delivery attempt. Retryable classifications release the row
    /// with bounded exponential backoff; permanent classifications and exhausted attempts mark
    /// the row dead, erase the protected payload, and enqueue the generation-matched
    /// <c>NotificationSendFailed</c> callback in the same transaction. The classification is a
    /// bounded internal code — never an address, token, body, or raw provider error.</summary>
    let fail
        (dataSource: NpgsqlDataSource)
        (options: RelayOptions)
        (email: ClaimedEmail)
        (classification: string)
        (permanent: bool)
        (ct: CancellationToken)
        : Task<Result<unit, string>> =
        task {
            let exhausted = permanent || email.Attempts >= email.MaxAttempts
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            if not exhausted then
                use release = new NpgsqlCommand(EmailOutboxSql.releaseEmailRetry, connection)

                release.Parameters.AddWithValue("email_id", email.EmailId) |> ignore
                release.Parameters.AddWithValue("owner", options.Owner) |> ignore
                release.Parameters.AddWithValue("classification", classification) |> ignore

                release.Parameters.AddWithValue("backoff_seconds", backoffSeconds options email.Attempts)
                |> ignore

                let! _ = release.ExecuteNonQueryAsync(ct)
                return Ok()
            else
                match encodeCallback (NotificationSendFailed email.Generation) with
                | Error message -> return Error message
                | Ok eventJson ->
                    use! transaction = connection.BeginTransactionAsync(ct)

                    use dead =
                        new NpgsqlCommand(EmailOutboxSql.deadLetterEmail, connection, transaction)

                    dead.Parameters.AddWithValue("email_id", email.EmailId) |> ignore
                    dead.Parameters.AddWithValue("owner", options.Owner) |> ignore
                    dead.Parameters.AddWithValue("classification", classification) |> ignore
                    let! updated = dead.ExecuteNonQueryAsync(ct)

                    if updated = 1 then
                        do!
                            insertCallback
                                connection
                                transaction
                                (failedCallbackKey email.FlowId email.Generation)
                                email.FlowId
                                eventJson
                                ct

                    do! transaction.CommitAsync(ct)
                    return Ok()
        }
