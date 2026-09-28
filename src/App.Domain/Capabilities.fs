namespace App.Domain

open System
open System.IO
open System.Security.Cryptography
open System.Text

type CapabilityPurpose = private CapabilityPurpose of string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CapabilityPurpose =
    let create (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error "A capability purpose is required."
        elif
            value.Length > 64
            || value |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c || c = '-'))
        then
            Error "A capability purpose must be at most 64 ASCII letters, digits, or hyphens."
        else
            Ok(CapabilityPurpose(value.ToLowerInvariant()))

    let value (CapabilityPurpose value) = value

type CapabilityEntity = private CapabilityEntity of string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CapabilityEntity =
    let create (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error "A capability entity is required."
        elif value.Length > 256 then
            Error "A capability entity must be at most 256 characters."
        else
            Ok(CapabilityEntity value)

    let value (CapabilityEntity value) = value

[<Sealed>]
type CapabilityHashKey internal (keyId: string, bytes: byte array) =
    member _.KeyId = keyId
    member internal _.Bytes = bytes

    override _.ToString() =
        $"CapabilityHashKey({keyId}, [REDACTED])"

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CapabilityHashKey =
    let create (keyId: string) (bytes: byte array) =
        if String.IsNullOrWhiteSpace keyId then
            Error "A capability hash key id is required."
        elif
            keyId.Length > 64
            || keyId
               |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c || ".-_".Contains c))
        then
            Error "A capability hash key id must be at most 64 ASCII letters, digits, dots, hyphens, or underscores."
        elif isNull bytes || bytes.Length < 32 then
            Error "A capability hash key must contain at least 256 bits."
        else
            Ok(CapabilityHashKey(keyId, Array.copy bytes))

[<Sealed>]
type RawCapability internal (bytes: byte array) =
    member internal _.Bytes = bytes
    override _.ToString() = "[REDACTED CAPABILITY]"

[<RequireQualifiedAccess>]
type CapabilityVersion = V1

type CapabilityDigest =
    { Version: CapabilityVersion
      KeyId: string
      Purpose: CapabilityPurpose
      Entity: CapabilityEntity
      Digest: byte array }

[<RequireQualifiedAccess>]
module Capability =
    [<Literal>]
    let private Prefix = "cap_v1_"

    [<Literal>]
    let private ByteLength = 32

    let private base64UrlEncode (bytes: byte array) =
        Convert.ToBase64String bytes
        |> fun value -> value.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private tryBase64UrlDecode (value: string) =
        try
            let padded =
                value.Replace('-', '+').Replace('_', '/')
                |> fun encoded -> encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=')

            Some(Convert.FromBase64String padded)
        with :? FormatException ->
            None

    let generate () =
        RawCapability(RandomNumberGenerator.GetBytes ByteLength)

    let encode (capability: RawCapability) =
        Prefix + base64UrlEncode capability.Bytes

    let tryParse (value: string) =
        if isNull value || not (value.StartsWith(Prefix, StringComparison.Ordinal)) then
            None
        else
            value[Prefix.Length ..]
            |> tryBase64UrlDecode
            |> Option.filter (fun bytes -> bytes.Length = ByteLength)
            |> Option.map RawCapability

    let private writeField (writer: BinaryWriter) (value: byte array) =
        writer.Write value.Length
        writer.Write value

    /// <summary>A purpose-scoped keyed hash that does not bind the entity. It locates the row
    /// for an opaque bearer capability (guest cart) whose entity id is only known after lookup.
    /// The entity-bound <c>digest</c>/<c>verify</c> remain the authority for entity-scoped
    /// capabilities such as order tracking.</summary>
    let locateDigest (key: CapabilityHashKey) (purpose: CapabilityPurpose) (capability: RawCapability) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        writeField writer (Encoding.UTF8.GetBytes "capability-locate-v1")
        writeField writer (Encoding.UTF8.GetBytes(CapabilityPurpose.value purpose))
        writeField writer capability.Bytes
        writer.Flush()

        use hmac = new HMACSHA256(key.Bytes)
        hmac.ComputeHash(stream.GetBuffer(), 0, int stream.Length)

    let digest
        (key: CapabilityHashKey)
        (purpose: CapabilityPurpose)
        (entity: CapabilityEntity)
        (capability: RawCapability)
        =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        writeField writer (Encoding.UTF8.GetBytes "capability-digest-v1")
        writeField writer (Encoding.UTF8.GetBytes(CapabilityPurpose.value purpose))
        writeField writer (Encoding.UTF8.GetBytes(CapabilityEntity.value entity))
        writeField writer capability.Bytes
        writer.Flush()

        use hmac = new HMACSHA256(key.Bytes)

        { Version = CapabilityVersion.V1
          KeyId = key.KeyId
          Purpose = purpose
          Entity = entity
          Digest = hmac.ComputeHash(stream.GetBuffer(), 0, int stream.Length) }

    let verify
        (key: CapabilityHashKey)
        (purpose: CapabilityPurpose)
        (entity: CapabilityEntity)
        (capability: RawCapability)
        (expected: CapabilityDigest)
        =
        expected.Version = CapabilityVersion.V1
        && String.Equals(expected.KeyId, key.KeyId, StringComparison.Ordinal)
        && expected.Purpose = purpose
        && expected.Entity = entity
        && CryptographicOperations.FixedTimeEquals((digest key purpose entity capability).Digest, expected.Digest)
