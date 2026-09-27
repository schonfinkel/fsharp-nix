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
                        new NpgsqlCommand(
                            "SELECT id FROM fsnix.users WHERE id = @user_id FOR UPDATE",
                            connection,
                            transaction
                        )

                    lockUser.Parameters.AddWithValue("user_id", userId) |> ignore
                    let! foundUser = lockUser.ExecuteScalarAsync(ct)

                    if isNull foundUser then
                        do! transaction.RollbackAsync(ct)
                        return Ok AccountFlowRequestResult.UserNotFound
                    else
                        use existing =
                            new NpgsqlCommand(
                                """SELECT flow_id, request_hash
                                   FROM fsnix.account_flow_requests
                                   WHERE user_id = @user_id
                                     AND flow_kind = @flow_kind
                                     AND idempotency_key_hash = @key_hash
                                   FOR UPDATE""",
                                connection,
                                transaction
                            )

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
                                new NpgsqlCommand(
                                    """UPDATE fsnix.account_flow_requests
                                       SET status = 'superseded', superseded_by = @flow_id,
                                           updated_at = statement_timestamp()
                                       WHERE user_id = @user_id
                                         AND flow_kind = @flow_kind
                                         AND status = 'requested'""",
                                    connection,
                                    transaction
                                )

                            supersede.Parameters.AddWithValue("flow_id", flowId) |> ignore
                            supersede.Parameters.AddWithValue("user_id", userId) |> ignore
                            supersede.Parameters.AddWithValue("flow_kind", FlowKind.wireName kind) |> ignore
                            let! _ = supersede.ExecuteNonQueryAsync(ct)

                            use cancelDeadlines =
                                new NpgsqlCommand(
                                    """UPDATE fsnix.flow_deadlines
                                       SET status = 'cancelled'
                                       WHERE flow_id IN (
                                           SELECT flow_id FROM fsnix.account_flow_requests
                                           WHERE user_id = @user_id AND flow_kind = @flow_kind AND status = 'superseded')
                                         AND status = 'pending'""",
                                    connection,
                                    transaction
                                )

                            cancelDeadlines.Parameters.AddWithValue("user_id", userId) |> ignore

                            cancelDeadlines.Parameters.AddWithValue("flow_kind", FlowKind.wireName kind)
                            |> ignore

                            let! _ = cancelDeadlines.ExecuteNonQueryAsync(ct)

                            use insertRequest =
                                new NpgsqlCommand(
                                    """INSERT INTO fsnix.account_flow_requests
                                           (flow_id, flow_kind, user_id, destination_email, status, generation,
                                            resend_count, expires_at, idempotency_key_hash, request_hash)
                                       VALUES (@flow_id, @flow_kind, @user_id, @destination, 'requested', 1, 0,
                                               @expires_at, @key_hash, @request_hash)""",
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
                                    """INSERT INTO fsnix.flow_deadlines
                                           (flow_id, timer_kind, generation, deadline, gate_callback_key)
                                       VALUES (@flow_id, 'flow-expiry', 1, @deadline, @callback_key)""",
                                    connection,
                                    transaction
                                )

                            deadline.Parameters.AddWithValue("flow_id", flowId) |> ignore
                            deadline.Parameters.AddWithValue("deadline", expiresAt) |> ignore
                            deadline.Parameters.AddWithValue("callback_key", callbackKey) |> ignore
                            let! _ = deadline.ExecuteNonQueryAsync(ct)

                            use callback =
                                new NpgsqlCommand(
                                    """INSERT INTO fsnix.integration_outbox (callback_key, machine_id, entity_id, event)
                                       VALUES (@callback_key, @machine_id, @entity_id, @event::jsonb)""",
                                    connection,
                                    transaction
                                )

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

            use command =
                new NpgsqlCommand(
                    """SELECT flow_id, flow_kind, user_id, destination_email, generation
                       FROM fsnix.account_flow_requests
                       WHERE flow_id = @flow_id AND status = 'requested'
                         AND expires_at > statement_timestamp()""",
                    connection
                )

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
                new NpgsqlCommand(
                    """SELECT flow_id FROM fsnix.account_flow_requests
                       WHERE flow_kind = @flow_kind AND status = 'requested'
                         AND expires_at > statement_timestamp()
                         AND lower(trim(destination_email)) = lower(trim(@email))
                       ORDER BY created_at DESC LIMIT 1""",
                    connection
                )

            command.Parameters.AddWithValue("flow_kind", FlowKind.wireName kind) |> ignore
            command.Parameters.AddWithValue("email", email) |> ignore
            let! result = command.ExecuteScalarAsync(ct)
            return if isNull result then None else Some(result :?> Guid)
        }
