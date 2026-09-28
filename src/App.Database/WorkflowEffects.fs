namespace App.Database

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

[<RequireQualifiedAccess>]
module WorkflowEffects =
    let key (record: ActionRecord<'Entity, 'Action>) purpose =
        $"xmsg:v1:{MachineId.value record.MachineId}:{CommandId.value record.CommandId}:{record.Ordinal}:{purpose}"

    let receipt
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<'Entity, 'Action>)
        (kind: string)
        (json: string)
        (ct: CancellationToken)
        =
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
                return true
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

                if
                    not found
                    || reader.GetString 0 <> kind
                    || not (CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte array>(1), hash))
                then
                    invalidOp "Action receipt payload mismatch."

                return false
        }

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
