namespace App.Database

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open App.Auth
open App.Domain
open Npgsql

/// <summary>Result of creating an existing-user account-flow request. Replayed returns the
/// original flow id; key reuse with a different canonical request is a conflict.</summary>
[<RequireQualifiedAccess>]
type AccountFlowRequestResult =
    | Created of Guid
    | Replayed of Guid
    | IdempotencyConflict
    | UserNotFound

[<RequireQualifiedAccess>]
module AccountFlowRequestSql =
    let cancelSupersededFlowDeadlines =
        Sql.load "Accounts/cancel-superseded-flow-deadlines"

    let findActiveFlowByEmail = Sql.load "Accounts/find-active-flow-by-email"
    let findIdempotentRequest = Sql.load "Accounts/find-idempotent-request"
    let insertFirstFlowDeadline = Sql.load "Accounts/insert-first-flow-deadline"
    let insertFlowCallback = Sql.load "Accounts/insert-flow-callback"
    let insertIdempotentFlowRequest = Sql.load "Accounts/insert-idempotent-flow-request"
    let loadActiveRequest = Sql.load "Accounts/load-active-request"
    let lockUser = Sql.load "Accounts/lock-user"
    let supersedeActiveFlows = Sql.load "Accounts/supersede-active-flows"

/// <summary>Restricted queries and transactional creation for password-reset and email-change
/// requests. A user-row lock serializes all request creation for that identity; request row,
/// supersession, deadline, and start callback commit atomically.</summary>
[<RequireQualifiedAccess>]
module AccountFlowRequests =

    let startCallbackKey (flowId: Guid) = $"flow-request:v1:{flowId:D}:start"

    let create
        (dataSource: NpgsqlDataSource)
        (kind: FlowKind)
        (userId: Guid)
        (destinationEmail: string)
        (expiresAt: DateTimeOffset)
        (idempotencyKeyHash: byte[])
        (requestHash: byte[])
        (ct: CancellationToken)
        : Task<Result<AccountFlowRequestResult, string>> =
        task {
            match UserId.create userId with
            | Error message -> return Error message
            | Ok user ->
                match AccountFlowCodec.event.Encode(StartRequested(kind, user)) with
                | Error error -> return Error $"account-flow start event failed to encode: %A{error}"
                | Ok startEvent ->
                    use connection = dataSource.CreateConnection()
                    do! connection.OpenAsync(ct)
                    use! transaction = connection.BeginTransactionAsync(ct)

                    use lockUser =
                        new NpgsqlCommand(AccountFlowRequestSql.lockUser, connection, transaction)

                    lockUser.Parameters.AddWithValue("user_id", userId) |> ignore
                    let! foundUser = lockUser.ExecuteScalarAsync(ct)

                    if isNull foundUser then
                        do! transaction.RollbackAsync(ct)
                        return Ok AccountFlowRequestResult.UserNotFound
                    else
                        use existing =
                            new NpgsqlCommand(AccountFlowRequestSql.findIdempotentRequest, connection, transaction)

                        existing.Parameters.AddWithValue("user_id", userId) |> ignore
                        existing.Parameters.AddWithValue("flow_kind", FlowKind.wireName kind) |> ignore
                        existing.Parameters.AddWithValue("key_hash", idempotencyKeyHash) |> ignore
                        let! reader = existing.ExecuteReaderAsync(ct)
                        let! hasExisting = reader.ReadAsync(ct)

                        let replay =
                            if hasExisting then
                                Some(reader.GetGuid 0, reader.GetFieldValue<byte array> 1)
                            else
                                None

                        reader.Dispose()

                        match replay with
                        | Some(flowId, storedHash) ->
                            do! transaction.CommitAsync(ct)

                            if CryptographicOperations.FixedTimeEquals(storedHash, requestHash) then
                                return Ok(AccountFlowRequestResult.Replayed flowId)
                            else
                                return Ok AccountFlowRequestResult.IdempotencyConflict
                        | None ->
                            let flowId = Guid.NewGuid()
                            let callbackKey = startCallbackKey flowId

                            use supersede =
                                new NpgsqlCommand(AccountFlowRequestSql.supersedeActiveFlows, connection, transaction)

                            supersede.Parameters.AddWithValue("flow_id", flowId) |> ignore
                            supersede.Parameters.AddWithValue("user_id", userId) |> ignore
                            supersede.Parameters.AddWithValue("flow_kind", FlowKind.wireName kind) |> ignore
                            let! _ = supersede.ExecuteNonQueryAsync(ct)

                            use cancelDeadlines =
                                new NpgsqlCommand(
                                    AccountFlowRequestSql.cancelSupersededFlowDeadlines,
                                    connection,
                                    transaction
                                )

                            cancelDeadlines.Parameters.AddWithValue("user_id", userId) |> ignore

                            cancelDeadlines.Parameters.AddWithValue("flow_kind", FlowKind.wireName kind)
                            |> ignore

                            let! _ = cancelDeadlines.ExecuteNonQueryAsync(ct)

                            use insertRequest =
                                new NpgsqlCommand(
                                    AccountFlowRequestSql.insertIdempotentFlowRequest,
                                    connection,
                                    transaction
                                )

                            insertRequest.Parameters.AddWithValue("flow_id", flowId) |> ignore

                            insertRequest.Parameters.AddWithValue("flow_kind", FlowKind.wireName kind)
                            |> ignore

                            insertRequest.Parameters.AddWithValue("user_id", userId) |> ignore
                            insertRequest.Parameters.AddWithValue("destination", destinationEmail) |> ignore
                            insertRequest.Parameters.AddWithValue("expires_at", expiresAt) |> ignore
                            insertRequest.Parameters.AddWithValue("key_hash", idempotencyKeyHash) |> ignore
                            insertRequest.Parameters.AddWithValue("request_hash", requestHash) |> ignore
                            let! _ = insertRequest.ExecuteNonQueryAsync(ct)

                            use deadline =
                                new NpgsqlCommand(
                                    AccountFlowRequestSql.insertFirstFlowDeadline,
                                    connection,
                                    transaction
                                )

                            deadline.Parameters.AddWithValue("flow_id", flowId) |> ignore
                            deadline.Parameters.AddWithValue("deadline", expiresAt) |> ignore
                            deadline.Parameters.AddWithValue("callback_key", callbackKey) |> ignore
                            let! _ = deadline.ExecuteNonQueryAsync(ct)

                            use callback =
                                new NpgsqlCommand(AccountFlowRequestSql.insertFlowCallback, connection, transaction)

                            callback.Parameters.AddWithValue("callback_key", callbackKey) |> ignore
                            callback.Parameters.AddWithValue("machine_id", AccountFlow.MachineKey) |> ignore
                            callback.Parameters.AddWithValue("entity_id", flowId.ToString("D")) |> ignore
                            callback.Parameters.AddWithValue("event", startEvent) |> ignore
                            let! _ = callback.ExecuteNonQueryAsync(ct)

                            do! transaction.CommitAsync(ct)
                            return Ok(AccountFlowRequestResult.Created flowId)
        }

    /// <summary>Loads the active request metadata needed to complete a token-bearing flow.
    /// Secrets are never returned; the email address is restricted to the server-side caller.</summary>
    let tryLoadActive
        (dataSource: NpgsqlDataSource)
        (flowId: Guid)
        (ct: CancellationToken)
        : Task<AccountFlowRequestRow option> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command = new NpgsqlCommand(AccountFlowRequestSql.loadActiveRequest, connection)

            command.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            let request =
                if not found then
                    None
                else
                    FlowKind.tryParseWireName (reader.GetString 1)
                    |> Option.map (fun kind ->
                        { FlowId = reader.GetGuid 0
                          Kind = kind
                          UserId = reader.GetGuid 2
                          DestinationEmail = reader.GetString 3
                          Generation = reader.GetInt32 4 })

            reader.Dispose()
            return request
        }

    /// <summary>Finds the currently active flow for a destination and kind. Used only for
    /// uniform public resend requests; the caller always returns the same response whether
    /// the query found a flow or not.</summary>
    let tryFindActiveForEmail
        (dataSource: NpgsqlDataSource)
        (kind: FlowKind)
        (email: string)
        (ct: CancellationToken)
        : Task<Guid option> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(AccountFlowRequestSql.findActiveFlowByEmail, connection)

            command.Parameters.AddWithValue("flow_kind", FlowKind.wireName kind) |> ignore
            command.Parameters.AddWithValue("email", email) |> ignore
            let! result = command.ExecuteScalarAsync(ct)
            return if isNull result then None else Some(result :?> Guid)
        }
