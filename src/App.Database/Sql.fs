namespace App.Database

open System
open System.IO

/// <summary>
/// Application SQL lives in <c>Sql/&lt;area&gt;/&lt;name&gt;.sql</c>, embedded in this assembly (no
/// runtime file paths, so it works unchanged in the Nix package and the OCI image) and formatted
/// by treefmt's pg_format like the migrations. Parameters are still bound by <c>NpgsqlCommand</c>
/// (<c>@name</c>); files never contain interpolated values.
/// </summary>
[<RequireQualifiedAccess>]
module Sql =
    type private Marker = class end

    let private assembly = typeof<Marker>.Assembly

    /// <summary>Loads <c>Sql/{name}.sql</c>. Callers bind each query once, as a top-level value
    /// of an <c>&lt;Area&gt;Sql</c> module, so the file is read once per process and a missing
    /// file fails at module initialisation rather than in the middle of a request.</summary>
    let load (name: string) =
        match assembly.GetManifestResourceStream $"Sql/{name}.sql" with
        | null -> invalidOp $"Embedded SQL 'Sql/{name}.sql' does not exist."
        | stream ->
            use reader = new StreamReader(stream)
            reader.ReadToEnd()
