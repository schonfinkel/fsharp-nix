namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open Npgsql

/// <summary>Guest-cart bearer capability store. Only the purpose-scoped keyed hash is stored;
/// the raw 256-bit token never reaches the database. Entity ids are the cart machine's entity
/// id string.</summary>
[<RequireQualifiedAccess>]
module CartGuestCapabilities =

    [<Literal>]
    let PurposeName = "guest-cart"

    let purpose: CapabilityPurpose =
        CapabilityPurpose.create PurposeName
        |> Result.defaultWith (fun _ -> invalidOp "the guest-cart purpose is fixed and valid")

    let private locateDigest (key: CapabilityHashKey) (capability: RawCapability) : byte[] =
        Capability.locateDigest key purpose capability

    let issue
        (dataSource: NpgsqlDataSource)
        (key: CapabilityHashKey)
        (entityId: string)
        (capability: RawCapability)
        (expiresAt: DateTimeOffset)
        (ct: CancellationToken)
        : Task =
        task {
            let digest = locateDigest key capability
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.cart_guest_capabilities (locate_digest, key_id, purpose, entity_id, expires_at)
                       VALUES (@locate_digest, @key_id, @purpose, @entity_id, @expires_at)
                       ON CONFLICT (locate_digest) DO NOTHING""",
                    connection
                )

            command.Parameters.AddWithValue("locate_digest", digest) |> ignore
            command.Parameters.AddWithValue("key_id", key.KeyId) |> ignore
            command.Parameters.AddWithValue("purpose", PurposeName) |> ignore
            command.Parameters.AddWithValue("entity_id", entityId) |> ignore
            command.Parameters.AddWithValue("expires_at", expiresAt) |> ignore
            let! _ = command.ExecuteNonQueryAsync(ct)
            ()
        }

    let tryResolve
        (dataSource: NpgsqlDataSource)
        (key: CapabilityHashKey)
        (capability: RawCapability)
        (ct: CancellationToken)
        : Task<string option> =
        task {
            let digest = locateDigest key capability
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """SELECT entity_id
                       FROM fsnix.cart_guest_capabilities
                       WHERE locate_digest = @locate_digest
                         AND key_id = @key_id
                         AND purpose = @purpose
                         AND NOT revoked
                         AND expires_at > statement_timestamp()""",
                    connection
                )

            command.Parameters.AddWithValue("locate_digest", digest) |> ignore
            command.Parameters.AddWithValue("key_id", key.KeyId) |> ignore
            command.Parameters.AddWithValue("purpose", PurposeName) |> ignore
            let! result = command.ExecuteScalarAsync(ct)
            return if isNull result then None else Some(result :?> string)
        }

    let revokeForEntity (dataSource: NpgsqlDataSource) (entityId: string) (ct: CancellationToken) : Task =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.cart_guest_capabilities
                       SET revoked = TRUE, revoked_at = statement_timestamp()
                       WHERE entity_id = @entity_id AND NOT revoked""",
                    connection
                )

            command.Parameters.AddWithValue("entity_id", entityId) |> ignore
            let! _ = command.ExecuteNonQueryAsync(ct)
            ()
        }
