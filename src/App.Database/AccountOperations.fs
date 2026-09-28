namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Auth
open App.Domain
open ByzantineSystems.Automata.Core
open Npgsql

/// <summary>
/// The durable work one Identity mutation must carry, precomputed so the store only executes
/// SQL inside the mutation's own transaction. The wire event JSON is encoded here (through the
/// versioned codec) and never inside a transaction, so a codec defect is an argument error,
/// not a mid-transaction failure.
/// </summary>
type AccountOperation =
    | RegisterFlow of RegisterFlowOperation
    | CompleteFlow of CompleteFlowOperation

/// <summary>
/// A registration together with the flow it starts: the restricted request row (destination
/// email lives only there), the expiry deadline gated on the start callback, the start
/// callback into the outbox, and the durable completion marker.
/// </summary>
and RegisterFlowOperation =
    { OperationId: Guid
      FlowId: Guid
      Kind: FlowKind
      UserId: Guid
      DestinationEmail: string
      ExpiresAt: DateTimeOffset
      StartCallbackKey: string
      StartEventJson: string }

/// <summary>
/// A confirmed Identity mutation (email confirmation, password reset, email change) together
/// with its marker and sanitized completion callback.
/// </summary>
and CompleteFlowOperation =
    { OperationId: Guid
      FlowId: Guid
      UserId: Guid
      Kind: FlowKind
      Operation: string
      CompletedAt: DateTimeOffset
      CompleteCallbackKey: string
      CompleteEventJson: string }

[<RequireQualifiedAccess>]
module AccountOperation =

    [<Literal>]
    let RegisterOperation = "register"

    [<Literal>]
    let ConfirmEmailOperation = "confirm-email"

    [<Literal>]
    let ResetPasswordOperation = "reset-password"

    [<Literal>]
    let ChangeEmailOperation = "change-email"

    let startCallbackKey (operationId: Guid) =
        let id = operationId.ToString("D")
        $"flow-op:v1:%s{id}:start"

    let completeCallbackKey (operationId: Guid) =
        let id = operationId.ToString("D")
        $"flow-op:v1:%s{id}:complete"

    let private encodeEvent (event: FlowEvent) : Result<string, string> =
        AccountFlowCodec.event.Encode event
        |> Result.mapError (fun error -> $"%A{error}")

    let private completionOperation =
        function
        | EmailVerification -> ConfirmEmailOperation
        | PasswordReset -> ResetPasswordOperation
        | EmailChange -> ChangeEmailOperation

    /// <summary>
    /// Builds a registration operation: new user, new flow id, request + deadline + start
    /// callback + marker, all applied atomically with the user insert. Supersedes any active
    /// request of the same kind for the same user (a re-registration before confirmation).
    /// </summary>
    let forRegistration
        (kind: FlowKind)
        (userId: Guid)
        (destinationEmail: string)
        (expiresAt: DateTimeOffset)
        : Result<AccountOperation, string> =
        UserId.create userId
        |> Result.bind (fun user ->
            encodeEvent (StartRequested(kind, user))
            |> Result.map (fun startEventJson ->
                let operationId = Guid.NewGuid()
                let flowId = Guid.NewGuid()

                RegisterFlow
                    { OperationId = operationId
                      FlowId = flowId
                      Kind = kind
                      UserId = userId
                      DestinationEmail = destinationEmail
                      ExpiresAt = expiresAt
                      StartCallbackKey = startCallbackKey operationId
                      StartEventJson = startEventJson }))

    /// <summary>
    /// Builds a completion operation: the marker proving the mutation committed, the request
    /// row moving to <c>completed</c>, and the sanitized callback to the flows machine.
    /// </summary>
    let forCompletion
        (flowId: Guid)
        (userId: Guid)
        (kind: FlowKind)
        (completedAt: DateTimeOffset)
        : Result<AccountOperation, string> =
        UserId.create userId
        |> Result.bind (fun user ->
            encodeEvent (CompletionSucceeded(kind, user, completedAt))
            |> Result.map (fun completeEventJson ->
                let operationId = Guid.NewGuid()

                CompleteFlow
                    { OperationId = operationId
                      FlowId = flowId
                      UserId = userId
                      Kind = kind
                      Operation = completionOperation kind
                      CompletedAt = completedAt
                      CompleteCallbackKey = completeCallbackKey operationId
                      CompleteEventJson = completeEventJson }))

    let internal describe (operation: AccountOperation) : string =
        match operation with
        | RegisterFlow op ->
            let id = op.FlowId.ToString("D")
            $"register flow %s{id}"
        | CompleteFlow op ->
            let id = op.FlowId.ToString("D")
            $"%s{op.Operation} flow %s{id}"

/// <summary>
/// Scoped one-shot carrier between an HTTP handler and the Identity store: the handler
/// attaches the operation, then calls the <c>UserManager</c> API whose store call consumes it
/// inside the mutation's transaction. An attached-but-unconsumed operation is discarded with
/// the scope; it can never silently apply later.
/// </summary>
type AccountOperationContext() =

    let mutable pending: AccountOperation option = None

    member _.Attach(operation: AccountOperation) : Result<unit, string> =
        match pending with
        | Some _ -> Error "An account operation is already attached to this scope."
        | None ->
            pending <- Some operation
            Ok()

    member internal _.TryTake() : AccountOperation option =
        let taken = pending
        pending <- None
        taken

[<RequireQualifiedAccess>]
module internal AccountOperationSql =

    /// <summary>
    /// Inserts one outbox callback under its stable key, verifying that an existing row with
    /// the same key carries the same machine, entity, and canonical event rather than treating
    /// a collision as success.
    /// </summary>
    let private insertCallback
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (callbackKey: string)
        (machineId: string)
        (entityId: string)
        (eventJson: string)
        (ct: CancellationToken)
        : Task<Result<unit, string>> =
        task {
            use command =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.integration_outbox (callback_key, machine_id, entity_id, event)
                       VALUES (@callback_key, @machine_id, @entity_id, @event::jsonb)
                       ON CONFLICT (callback_key) DO NOTHING
                       RETURNING outbox_id""",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("callback_key", callbackKey) |> ignore
            command.Parameters.AddWithValue("machine_id", machineId) |> ignore
            command.Parameters.AddWithValue("entity_id", entityId) |> ignore
            command.Parameters.AddWithValue("event", eventJson) |> ignore

            let! inserted = command.ExecuteScalarAsync(ct)

            if not (isNull inserted) then
                return Ok()
            else
                use verify =
                    new NpgsqlCommand(
                        """SELECT machine_id, entity_id, event = @event::jsonb
                           FROM fsnix.integration_outbox
                           WHERE callback_key = @callback_key""",
                        connection,
                        transaction
                    )

                verify.Parameters.AddWithValue("callback_key", callbackKey) |> ignore
                verify.Parameters.AddWithValue("event", eventJson) |> ignore
                let! reader = verify.ExecuteReaderAsync(ct)
                let! couldRead = reader.ReadAsync(ct)

                let result =
                    if not couldRead then
                        Error $"callback key '{callbackKey}' conflicted with an unreadable row"
                    else
                        let storedMachine = reader.GetString 0
                        let storedEntity = reader.GetString 1
                        let eventMatches = reader.GetBoolean 2

                        if storedMachine = machineId && storedEntity = entityId && eventMatches then
                            Ok()
                        else
                            Error $"callback key '{callbackKey}' exists with different content"

                reader.Dispose()
                return result
        }

    /// <summary>Applies one operation inside the caller's open transaction. Every statement
    /// either belongs to this transaction or fails it; there is no partial application.</summary>
    let apply
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operation: AccountOperation)
        (ct: CancellationToken)
        : Task<Result<unit, string>> =
        task {
            match operation with
            | RegisterFlow op ->
                use supersede =
                    new NpgsqlCommand(
                        """UPDATE fsnix.account_flow_requests
                           SET status = 'superseded', superseded_by = @flow_id, updated_at = statement_timestamp()
                           WHERE user_id = @user_id
                             AND flow_kind = @flow_kind
                             AND status = 'requested'
                             AND flow_id <> @flow_id""",
                        connection,
                        transaction
                    )

                supersede.Parameters.AddWithValue("flow_id", op.FlowId) |> ignore
                supersede.Parameters.AddWithValue("user_id", op.UserId) |> ignore

                supersede.Parameters.AddWithValue("flow_kind", FlowKind.wireName op.Kind)
                |> ignore

                let! _ = supersede.ExecuteNonQueryAsync(ct)

                use cancelDeadlines =
                    new NpgsqlCommand(
                        """UPDATE fsnix.flow_deadlines
                           SET status = 'cancelled'
                           WHERE status = 'pending'
                             AND flow_id IN (
                                 SELECT flow_id FROM fsnix.account_flow_requests
                                 WHERE user_id = @user_id AND flow_kind = @flow_kind AND status = 'superseded')""",
                        connection,
                        transaction
                    )

                cancelDeadlines.Parameters.AddWithValue("user_id", op.UserId) |> ignore

                cancelDeadlines.Parameters.AddWithValue("flow_kind", FlowKind.wireName op.Kind)
                |> ignore

                let! _ = cancelDeadlines.ExecuteNonQueryAsync(ct)

                use request =
                    new NpgsqlCommand(
                        """INSERT INTO fsnix.account_flow_requests
                               (flow_id, flow_kind, user_id, destination_email, status, generation,
                                resend_count, expires_at, created_at, updated_at)
                           VALUES (@flow_id, @flow_kind, @user_id, @destination_email, 'requested', 1, 0,
                                   @expires_at, statement_timestamp(), statement_timestamp())""",
                        connection,
                        transaction
                    )

                request.Parameters.AddWithValue("flow_id", op.FlowId) |> ignore

                request.Parameters.AddWithValue("flow_kind", FlowKind.wireName op.Kind)
                |> ignore

                request.Parameters.AddWithValue("user_id", op.UserId) |> ignore

                request.Parameters.AddWithValue("destination_email", op.DestinationEmail)
                |> ignore

                request.Parameters.AddWithValue("expires_at", op.ExpiresAt) |> ignore
                let! _ = request.ExecuteNonQueryAsync(ct)

                use deadline =
                    new NpgsqlCommand(
                        """INSERT INTO fsnix.flow_deadlines
                               (flow_id, timer_kind, generation, deadline, gate_callback_key)
                           VALUES (@flow_id, 'flow-expiry', 1, @deadline, @gate)""",
                        connection,
                        transaction
                    )

                deadline.Parameters.AddWithValue("flow_id", op.FlowId) |> ignore
                deadline.Parameters.AddWithValue("deadline", op.ExpiresAt) |> ignore
                deadline.Parameters.AddWithValue("gate", op.StartCallbackKey) |> ignore
                let! _ = deadline.ExecuteNonQueryAsync(ct)

                use marker =
                    new NpgsqlCommand(
                        """INSERT INTO fsnix.account_operation_markers (operation_id, user_id, flow_id, operation)
                           VALUES (@operation_id, @user_id, @flow_id, @operation)""",
                        connection,
                        transaction
                    )

                marker.Parameters.AddWithValue("operation_id", op.OperationId) |> ignore
                marker.Parameters.AddWithValue("user_id", op.UserId) |> ignore
                marker.Parameters.AddWithValue("flow_id", op.FlowId) |> ignore

                marker.Parameters.AddWithValue("operation", AccountOperation.RegisterOperation)
                |> ignore

                let! _ = marker.ExecuteNonQueryAsync(ct)

                return!
                    insertCallback
                        connection
                        transaction
                        op.StartCallbackKey
                        AccountFlow.MachineKey
                        (op.FlowId.ToString("D"))
                        op.StartEventJson
                        ct
            | CompleteFlow op ->
                use request =
                    new NpgsqlCommand(
                        """UPDATE fsnix.account_flow_requests
                           SET status = 'completed', updated_at = statement_timestamp()
                           WHERE flow_id = @flow_id
                             AND user_id = @user_id
                             AND flow_kind = @flow_kind
                             AND status = 'requested'""",
                        connection,
                        transaction
                    )

                request.Parameters.AddWithValue("flow_id", op.FlowId) |> ignore
                request.Parameters.AddWithValue("user_id", op.UserId) |> ignore

                request.Parameters.AddWithValue("flow_kind", FlowKind.wireName op.Kind)
                |> ignore

                let! updated = request.ExecuteNonQueryAsync(ct)

                if updated <> 1 then
                    return Error "the requested account flow was not active for this user and kind"
                else
                    use marker =
                        new NpgsqlCommand(
                            """INSERT INTO fsnix.account_operation_markers
                               (operation_id, user_id, flow_id, operation, completed_at)
                            VALUES (@operation_id, @user_id, @flow_id, @operation, @completed_at)
                            ON CONFLICT (operation_id) DO NOTHING""",
                            connection,
                            transaction
                        )

                    marker.Parameters.AddWithValue("operation_id", op.OperationId) |> ignore
                    marker.Parameters.AddWithValue("user_id", op.UserId) |> ignore
                    marker.Parameters.AddWithValue("flow_id", op.FlowId) |> ignore
                    marker.Parameters.AddWithValue("operation", op.Operation) |> ignore
                    marker.Parameters.AddWithValue("completed_at", op.CompletedAt) |> ignore
                    let! _ = marker.ExecuteNonQueryAsync(ct)

                    use cancelDeadline =
                        new NpgsqlCommand(
                            """UPDATE fsnix.flow_deadlines
                            SET status = 'cancelled'
                            WHERE flow_id = @flow_id AND status = 'pending'""",
                            connection,
                            transaction
                        )

                    cancelDeadline.Parameters.AddWithValue("flow_id", op.FlowId) |> ignore
                    let! _ = cancelDeadline.ExecuteNonQueryAsync(ct)

                    return!
                        insertCallback
                            connection
                            transaction
                            op.CompleteCallbackKey
                            AccountFlow.MachineKey
                            (op.FlowId.ToString("D"))
                            op.CompleteEventJson
                            ct
        }
