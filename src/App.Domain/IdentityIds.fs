namespace App.Domain

open System

/// <summary>The non-secret user reference carried by account flows. Identity stays
/// authoritative for users; the flow stores only this non-secret id.</summary>
[<Struct>]
type UserId = private UserId of value: Guid

module UserId =

    let create (value: Guid) : Result<UserId, string> =
        if value = Guid.Empty then
            Error "A user id must not be empty."
        else
            Ok(UserId value)

    let value (UserId id) = id

    /// <summary>Decodes the wire representation (lowercase "D" GUID format) through the
    /// validating constructor, so a corrupt row cannot become a typed value.</summary>
    let tryParse (value: string) : Result<UserId, string> =
        match Guid.TryParseExact(value, "D") with
        | true, id -> create id
        | false, _ -> Error "A user id must be a GUID in \"D\" format."

    /// <summary>The canonical wire representation: lowercase "D" format, independent of
    /// platform GUID formatting.</summary>
    let wireString (id: UserId) = (value id).ToString("D")
