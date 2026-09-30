namespace App.Domain

open System

[<Struct>]
type ReturnId = private ReturnId of Guid

[<RequireQualifiedAccess>]
module ReturnId =
    /// <summary>A fresh, time-ordered id (never empty).</summary>
    let generate () : ReturnId = ReturnId(Guid.CreateVersion7())

    let create value =
        if value = Guid.Empty then
            Error "A return id is required."
        else
            Ok(ReturnId value)

    let value (ReturnId value) = value
    let wireString id = (value id).ToString("D")

    let tryParse (text: string) =
        match Guid.TryParseExact(text, "D") with
        | true, value -> create value
        | _ -> Error "A return id must be a GUID in D format."

[<Struct>]
type ReturnAuthorizationId = private ReturnAuthorizationId of Guid

[<RequireQualifiedAccess>]
module ReturnAuthorizationId =
    /// <summary>A fresh, time-ordered id (never empty).</summary>
    let generate () : ReturnAuthorizationId =
        ReturnAuthorizationId(Guid.CreateVersion7())

    let create value =
        if value = Guid.Empty then
            Error "A return authorization id is required."
        else
            Ok(ReturnAuthorizationId value)

    let value (ReturnAuthorizationId value) = value
    let wireString id = (value id).ToString("D")

    let tryParse (text: string) =
        match Guid.TryParseExact(text, "D") with
        | true, value -> create value
        | _ -> Error "A return authorization id must be a GUID in D format."

type ReturnTrackingEventId = private ReturnTrackingEventId of string

[<RequireQualifiedAccess>]
module ReturnTrackingEventId =
    let create (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error "A return tracking event id is required."
        elif value.Length > 128 || value |> Seq.exists Char.IsControl then
            Error "A return tracking event id must be bounded and contain no control characters."
        else
            Ok(ReturnTrackingEventId value)

    let value (ReturnTrackingEventId value) = value
    let tryParse = create
