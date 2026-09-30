namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open Npgsql

[<RequireQualifiedAccess>]
module CartCapabilitySql =
    let insertCapability = Sql.load "Carts/insert-capability"
    let resolveCapability = Sql.load "Carts/resolve-capability"
    let revokeCapabilities = Sql.load "Carts/revoke-capabilities"

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

            use command = new NpgsqlCommand(CartCapabilitySql.insertCapability, connection)

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

            use command = new NpgsqlCommand(CartCapabilitySql.resolveCapability, connection)

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

            use command = new NpgsqlCommand(CartCapabilitySql.revokeCapabilities, connection)

            command.Parameters.AddWithValue("entity_id", entityId) |> ignore
            let! _ = command.ExecuteNonQueryAsync(ct)
            ()
        }
