namespace App.Database

open System
open App.Domain
open ByzantineSystems.Automata.Core

/// <summary>Validation shared by the explicit wire codecs. Decoding always goes through the
/// validated domain constructors, so a malformed durable value is a <c>CodecError</c> rather
/// than a silently coerced domain value.</summary>
[<RequireQualifiedAccess>]
module CodecSupport =
    let decodeError (name: string) (message: string) =
        CodecError.DecodeError(name, FormatException message)

    /// <summary>Decodes a persisted reason code.</summary>
    let reason (text: string) : Result<ReasonCode, CodecError> =
        ReasonCode.create text |> Result.mapError (decodeError "ReasonCode")
