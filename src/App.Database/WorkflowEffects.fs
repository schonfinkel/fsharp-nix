namespace App.Database

open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

/// <summary>What the action-receipt row says about this delivery of an action.</summary>
[<RequireQualifiedAccess>]
type ReceiptStatus =
    /// <summary>First delivery: perform the local writes and the callback.</summary>
    | FirstRun
    /// <summary>A redelivery whose receipt, local writes and callback already committed in
    /// one transaction; nothing further to do.</summary>
    | Duplicate

/// <summary>Why an action receipt could not be recorded.</summary>
[<RequireQualifiedAccess>]
type ReceiptFailure =
    /// <summary>The action could not be encoded to its canonical payload.</summary>
    | EncodingFailed
    /// <summary>The same action identity was already recorded with a different kind or payload
    /// hash: a codec or chart defect, never retried.</summary>
    | Mismatch

/// <summary>
/// The single implementation of the local-effect protocol (PLAN "Local database effects"): one
/// PostgreSQL transaction holds the action receipt, payload-hash verification, all local writes
/// and the integration-outbox callback. Callers never re-run local writes on a duplicate, which
/// is what makes effects such as gapless invoice numbering safe under redelivery.
/// </summary>
[<RequireQualifiedAccess>]
module WorkflowEffects =
    /// <summary>The destination idempotency key for a callback derived from a source action:
    /// <c>xmsg:v1:{sourceMachine}:{sourceCommandId}:{ordinal}:{destinationPurpose}</c>.</summary>
    let key (record: ActionRecord<'Entity, 'Action>) purpose =
        $"xmsg:v1:{MachineId.value record.MachineId}:{CommandId.value record.CommandId}:{record.Ordinal}:{purpose}"

    /// <summary>Inserts or verifies the receipt for an already-encoded action payload.</summary>
    let receipt
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<'Entity, 'Action>)
        (kind: string)
        (json: string)
        (ct: CancellationToken)
        : Task<Result<ReceiptStatus, ReceiptFailure>> =
        task {
            let hash = SHA256.HashData(Encoding.UTF8.GetBytes json)

            use insert =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.action_receipts(machine_id,command_id,ordinal,action_kind,payload_hash) VALUES(@machine,@command,@ordinal,@kind,@hash) ON CONFLICT DO NOTHING RETURNING command_id",
                    connection,
                    tx
                )

            insert.Parameters.AddWithValue("machine", MachineId.value record.MachineId)
            |> ignore

            insert.Parameters.AddWithValue("command", CommandId.value record.CommandId)
            |> ignore

            insert.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore
            insert.Parameters.AddWithValue("kind", kind) |> ignore
            insert.Parameters.AddWithValue("hash", hash) |> ignore
            let! inserted = insert.ExecuteScalarAsync ct

            if not (isNull inserted) then
                return Ok ReceiptStatus.FirstRun
            else
                use verify =
                    new NpgsqlCommand(
                        "SELECT action_kind,payload_hash FROM fsnix.action_receipts WHERE machine_id=@machine AND command_id=@command AND ordinal=@ordinal",
                        connection,
                        tx
                    )

                verify.Parameters.AddWithValue("machine", MachineId.value record.MachineId)
                |> ignore

                verify.Parameters.AddWithValue("command", CommandId.value record.CommandId)
                |> ignore

                verify.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore
                use! reader = verify.ExecuteReaderAsync ct
                let! found = reader.ReadAsync ct

                let matches =
                    found
                    && reader.GetString 0 = kind
                    && CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte array>(1), hash)

                return
                    if matches then
                        Ok ReceiptStatus.Duplicate
                    else
                        Error ReceiptFailure.Mismatch
        }

    /// <summary>Encodes the action with its durable codec and records its receipt.</summary>
    let receiptFor
        (codec: Codec<'Action>)
        (actionKind: 'Action -> string)
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<'Entity, 'Action>)
        (ct: CancellationToken)
        : Task<Result<ReceiptStatus, ReceiptFailure>> =
        match codec.Encode record.Action with
        | Error _ -> Task.FromResult(Error ReceiptFailure.EncodingFailed)
        | Ok json -> receipt connection tx record (actionKind record.Action) json ct

    /// <summary>Inserts an already-encoded callback into the integration outbox. A repeat of the
    /// same stable key is a no-op.</summary>
    let callback
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        callbackKey
        machine
        entity
        eventJson
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.integration_outbox(callback_key,machine_id,entity_id,event) VALUES(@key,@machine,@entity,@event::jsonb) ON CONFLICT(callback_key) DO NOTHING",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("key", callbackKey) |> ignore
            command.Parameters.AddWithValue("machine", machine) |> ignore
            command.Parameters.AddWithValue("entity", entity) |> ignore
            command.Parameters.AddWithValue("event", eventJson) |> ignore
            let! _ = command.ExecuteNonQueryAsync ct
            return ()
        }

    /// <summary>Encodes a destination event with its codec and queues it as the callback for
    /// <paramref name="record"/> under <c>key record purpose</c>.</summary>
    let deliver
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<'Entity, 'Action>)
        (purpose: string)
        (machine: string)
        (entity: string)
        (codec: Codec<'Event>)
        (event: 'Event)
        (ct: CancellationToken)
        : Task<Result<unit, ReceiptFailure>> =
        task {
            match codec.Encode event with
            | Error _ -> return Error ReceiptFailure.EncodingFailed
            | Ok json ->
                do! callback connection tx (key record purpose) machine entity json ct
                return Ok()
        }

    /// <summary>
    /// Runs a local effect under <c>executeIdempotentTransaction</c>: record the receipt, then on
    /// first delivery perform <paramref name="firstRun"/> (local writes plus callbacks). A
    /// duplicate commits nothing new. Any <c>Error</c> rolls the whole transaction back.
    /// </summary>
    let runLocalEffect
        (dataSource: NpgsqlDataSource)
        (receiptOf: NpgsqlConnection -> NpgsqlTransaction -> CancellationToken -> Task<Result<ReceiptStatus, 'E>>)
        (firstRun: NpgsqlConnection -> NpgsqlTransaction -> CancellationToken -> Task<Result<unit, 'E>>)
        (ct: CancellationToken)
        : Task<Result<unit, 'E>> =
        Execution.executeIdempotentTransaction dataSource ct (fun connection tx token ->
            task {
                match! receiptOf connection tx token with
                | Error error -> return Error error
                | Ok ReceiptStatus.Duplicate -> return Ok()
                | Ok ReceiptStatus.FirstRun -> return! firstRun connection tx token
            })
