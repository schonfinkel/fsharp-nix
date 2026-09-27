namespace App.Database

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Cart
open App.Domain
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

/// <summary>
/// The durable work of the cart machine's actions: exactly-once receipts, the abandonment
/// deadline ledger, the merge snapshot, cross-machine apply/notify callbacks, and capability
/// revocation. No bearer capability or raw secret ever reaches these effects — merge actions
/// carry already-frozen line snapshots, and capability revocation is keyed by entity id.
/// </summary>
[<RequireQualifiedAccess>]
module CartEffects =

    let actionKind (action: CartAction) : string =
        match action with
        | RecordCartTouch _ -> "record-cart-touch"
        | CaptureMergeSnapshot _ -> "capture-merge-snapshot"
        | SubmitMergeSnapshot _ -> "submit-merge-snapshot"
        | NotifyMergeApplied _ -> "notify-merge-applied"
        | RevokeGuestCapability _ -> "revoke-guest-capability"

    let private encodeEvent (event: CartEvent) : Result<string, CartActionError> =
        CartCodec.event.Encode event
        |> Result.mapError (fun _ -> CartActionError.CallbackEncodingFailed)

    let private encodeAction (action: CartAction) : Result<string, CartActionError> =
        CartCodec.action.Encode action
        |> Result.mapError (fun _ -> CartActionError.CallbackEncodingFailed)

    let private payloadHash (action: CartAction) : byte[] =
        match encodeAction action with
        | Ok json -> SHA256.HashData(Encoding.UTF8.GetBytes json)
        | Error _ -> Array.empty

    let private callbackKey (record: ActionRecord<CartId, CartAction>) (purpose: string) : string =
        $"xmsg:v1:%s{MachineId.value record.MachineId}:%d{CommandId.value record.CommandId}:%d{record.Ordinal}:%s{purpose}"

    /// <summary>Inserts the action receipt, or verifies an existing one matches the canonical
    /// payload. Returns <c>true</c> on first delivery, <c>false</c> on a verified replay.</summary>
    let private settleReceipt
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (record: ActionRecord<CartId, CartAction>)
        (ct: CancellationToken)
        : Task<Result<bool, CartActionError>> =
        task {
            match encodeAction record.Action with
            | Error error -> return Error error
            | Ok encoded ->
                let hash = SHA256.HashData(Encoding.UTF8.GetBytes encoded)

                use insert =
                    new NpgsqlCommand(
                        """INSERT INTO fsnix.action_receipts (machine_id, command_id, ordinal, action_kind, payload_hash)
                           VALUES (@machine_id, @command_id, @ordinal, @action_kind, @payload_hash)
                           ON CONFLICT (machine_id, command_id, ordinal) DO NOTHING
                           RETURNING command_id""",
                        connection,
                        transaction
                    )

                insert.Parameters.AddWithValue("machine_id", MachineId.value record.MachineId)
                |> ignore

                insert.Parameters.AddWithValue("command_id", CommandId.value record.CommandId)
                |> ignore

                insert.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore

                insert.Parameters.AddWithValue("action_kind", actionKind record.Action)
                |> ignore

                insert.Parameters.AddWithValue("payload_hash", hash) |> ignore

                let! inserted = insert.ExecuteScalarAsync(ct)

                if not (isNull inserted) then
                    return Ok true
                else
                    use verify =
                        new NpgsqlCommand(
                            """SELECT action_kind, payload_hash
                               FROM fsnix.action_receipts
                               WHERE machine_id = @machine_id
                                 AND command_id = @command_id
                                 AND ordinal = @ordinal""",
                            connection,
                            transaction
                        )

                    verify.Parameters.AddWithValue("machine_id", MachineId.value record.MachineId)
                    |> ignore

                    verify.Parameters.AddWithValue("command_id", CommandId.value record.CommandId)
                    |> ignore

                    verify.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore

                    let! reader = verify.ExecuteReaderAsync(ct)
                    let! couldRead = reader.ReadAsync(ct)

                    let existing =
                        if couldRead then
                            let kind = reader.GetString 0
                            let storedHash = Convert.ToHexString(reader.GetValue(1) :?> byte[])
                            Some(kind, storedHash)
                        else
                            None

                    reader.Dispose()
                    let expected = Convert.ToHexString(hash)

                    let verified =
                        existing
                        |> Option.map (fun (kind, storedHash) ->
                            kind = actionKind record.Action && storedHash = expected)
                        |> Option.defaultValue false

                    if verified then
                        return Ok false
                    else
                        return Error CartActionError.ActionReceiptMismatch
        }

    let private insertCallback
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (key: string)
        (machineId: string)
        (entityId: string)
        (eventJson: string)
        (ct: CancellationToken)
        : Task =
        task {
            use command =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.integration_outbox (callback_key, machine_id, entity_id, event)
                       VALUES (@callback_key, @machine_id, @entity_id, @event::jsonb)
                       ON CONFLICT (callback_key) DO NOTHING""",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("callback_key", key) |> ignore
            command.Parameters.AddWithValue("machine_id", machineId) |> ignore
            command.Parameters.AddWithValue("entity_id", entityId) |> ignore
            command.Parameters.AddWithValue("event", eventJson) |> ignore
            let! _ = command.ExecuteNonQueryAsync(ct)
            ()
        }

    let private notify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cartId: string)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand("SELECT pg_notify(@channel, @cart_id)", connection, transaction)

            command.Parameters.AddWithValue("channel", CartChange.Channel) |> ignore
            command.Parameters.AddWithValue("cart_id", cartId) |> ignore
            let! _ = command.ExecuteNonQueryAsync(ct)
            ()
        }

    /// <summary>Records a cart touch: arms a new abandonment deadline for a non-empty cart or
    /// cancels pending deadlines for an emptied cart, and hints waiting SSE listeners.</summary>
    let applyRecordCartTouch
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<CartId, CartAction>)
        (deadline: DateTimeOffset)
        (ct: CancellationToken)
        : Task<Result<unit, CartActionError>> =
        task {
            let cartId = EntityId.value record.EntityId
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            match! settleReceipt connection transaction record ct with
            | Error error ->
                do! transaction.RollbackAsync(ct)
                return Error error
            | Ok first ->
                if first then
                    match record.Action with
                    | RecordCartTouch(generation, hasLines) ->
                        if hasLines then
                            use cancelPrior =
                                new NpgsqlCommand(
                                    """UPDATE fsnix.cart_deadlines
                                       SET status = 'cancelled'
                                       WHERE cart_id = @cart_id
                                         AND status = 'pending'
                                         AND generation < @generation""",
                                    connection,
                                    transaction
                                )

                            cancelPrior.Parameters.AddWithValue("cart_id", cartId) |> ignore
                            cancelPrior.Parameters.AddWithValue("generation", generation) |> ignore
                            let! _ = cancelPrior.ExecuteNonQueryAsync(ct)

                            use deadlineRow =
                                new NpgsqlCommand(
                                    """INSERT INTO fsnix.cart_deadlines
                                           (cart_id, timer_kind, generation, deadline, gate_callback_key)
                                       VALUES (@cart_id, 'cart-abandonment', @generation, @deadline, @gate)
                                       ON CONFLICT (cart_id, timer_kind, generation) DO NOTHING""",
                                    connection,
                                    transaction
                                )

                            deadlineRow.Parameters.AddWithValue("cart_id", cartId) |> ignore
                            deadlineRow.Parameters.AddWithValue("generation", generation) |> ignore
                            deadlineRow.Parameters.AddWithValue("deadline", deadline) |> ignore

                            deadlineRow.Parameters.AddWithValue("gate", $"cart-touch:v1:{cartId}:{generation}")
                            |> ignore

                            let! _ = deadlineRow.ExecuteNonQueryAsync(ct)
                            ()
                        else
                            use cancelAll =
                                new NpgsqlCommand(
                                    """UPDATE fsnix.cart_deadlines
                                       SET status = 'cancelled'
                                       WHERE cart_id = @cart_id AND status = 'pending'""",
                                    connection,
                                    transaction
                                )

                            cancelAll.Parameters.AddWithValue("cart_id", cartId) |> ignore
                            let! _ = cancelAll.ExecuteNonQueryAsync(ct)
                            ()

                        do! notify connection transaction cartId ct
                    | _ -> ()

                    do! transaction.CommitAsync(ct)
                    return Ok()
                else
                    do! transaction.CommitAsync(ct)
                    return Ok()
        }

    /// <summary>Captures the immutable merge snapshot and notifies the guest cart that it is
    /// captured, so the machine can submit the snapshot to the target customer cart.</summary>
    let applyCaptureMergeSnapshot
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<CartId, CartAction>)
        (ct: CancellationToken)
        : Task<Result<unit, CartActionError>> =
        task {
            let sourceCartId = EntityId.value record.EntityId
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            match! settleReceipt connection transaction record ct with
            | Error error ->
                do! transaction.RollbackAsync(ct)
                return Error error
            | Ok first ->
                if first then
                    match record.Action with
                    | CaptureMergeSnapshot(mergeId, target, lines) ->
                        match CartCodec.encodeLines lines with
                        | Error _ ->
                            do! transaction.RollbackAsync(ct)
                            return Error CartActionError.CallbackEncodingFailed
                        | Ok linesJson ->
                            let! outcome = CartMerge.capture dataSource mergeId sourceCartId target linesJson ct

                            match outcome with
                            | CartMerge.CaptureOutcome.AlreadyCaptured ->
                                do! transaction.RollbackAsync(ct)
                                return Error CartActionError.MergeSnapshotAlreadyCaptured
                            | CartMerge.CaptureOutcome.Captured ->
                                match encodeEvent (MergeSnapshotCaptured mergeId) with
                                | Error error ->
                                    do! transaction.RollbackAsync(ct)
                                    return Error error
                                | Ok eventJson ->
                                    do!
                                        insertCallback
                                            connection
                                            transaction
                                            (callbackKey record "merge-captured")
                                            Cart.MachineKey
                                            sourceCartId
                                            eventJson
                                            ct

                                    do! transaction.CommitAsync(ct)
                                    return Ok()
                    | _ ->
                        do! transaction.RollbackAsync(ct)
                        return Error CartActionError.InvalidCartEntityId
                else
                    do! transaction.CommitAsync(ct)
                    return Ok()
        }

    /// <summary>Submits the frozen snapshot to the target customer cart under a stable
    /// idempotency key; the relay enqueues <c>ApplyMerge</c> to the customer cart.</summary>
    let applySubmitMergeSnapshot
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<CartId, CartAction>)
        (ct: CancellationToken)
        : Task<Result<unit, CartActionError>> =
        task {
            let sourceCartId = EntityId.value record.EntityId
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            match! settleReceipt connection transaction record ct with
            | Error error ->
                do! transaction.RollbackAsync(ct)
                return Error error
            | Ok first ->
                if first then
                    match record.Action with
                    | SubmitMergeSnapshot(mergeId, target, lines) ->
                        match encodeEvent (ApplyMerge(mergeId, sourceCartId, lines)) with
                        | Error error ->
                            do! transaction.RollbackAsync(ct)
                            return Error error
                        | Ok eventJson ->
                            do!
                                insertCallback
                                    connection
                                    transaction
                                    (callbackKey record "apply-merge")
                                    Cart.MachineKey
                                    target
                                    eventJson
                                    ct

                            do! transaction.CommitAsync(ct)
                            return Ok()
                    | _ ->
                        do! transaction.RollbackAsync(ct)
                        return Error CartActionError.InvalidCartEntityId
                else
                    do! transaction.CommitAsync(ct)
                    return Ok()
        }

    /// <summary>Marks the snapshot applied and notifies the guest cart that the target applied
    /// it, so the guest can revoke its capability and become merged-into.</summary>
    let applyNotifyMergeApplied
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<CartId, CartAction>)
        (ct: CancellationToken)
        : Task<Result<unit, CartActionError>> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            match! settleReceipt connection transaction record ct with
            | Error error ->
                do! transaction.RollbackAsync(ct)
                return Error error
            | Ok first ->
                if first then
                    match record.Action with
                    | NotifyMergeApplied(mergeId, sourceCartId) ->
                        match encodeEvent (MergeApplied mergeId) with
                        | Error error ->
                            do! transaction.RollbackAsync(ct)
                            return Error error
                        | Ok eventJson ->
                            do! CartMerge.markApplied dataSource mergeId ct

                            do!
                                insertCallback
                                    connection
                                    transaction
                                    (callbackKey record "merge-applied")
                                    Cart.MachineKey
                                    sourceCartId
                                    eventJson
                                    ct

                            do! transaction.CommitAsync(ct)
                            return Ok()
                    | _ ->
                        do! transaction.RollbackAsync(ct)
                        return Error CartActionError.InvalidCartEntityId
                else
                    do! transaction.CommitAsync(ct)
                    return Ok()
        }

    /// <summary>Revokes the guest capability for the source cart, ending the guest's access.</summary>
    let applyRevokeGuestCapability
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<CartId, CartAction>)
        (ct: CancellationToken)
        : Task<Result<unit, CartActionError>> =
        task {
            let sourceCartId = EntityId.value record.EntityId
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            match! settleReceipt connection transaction record ct with
            | Error error ->
                do! transaction.RollbackAsync(ct)
                return Error error
            | Ok first ->
                if first then
                    do! CartGuestCapabilities.revokeForEntity dataSource sourceCartId ct
                    do! transaction.CommitAsync(ct)
                    return Ok()
                else
                    do! transaction.CommitAsync(ct)
                    return Ok()
        }
