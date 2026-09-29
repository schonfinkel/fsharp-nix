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

    /// <summary>Every embedded query name, e.g. <c>Invoices/list</c>.</summary>
    let names =
        assembly.GetManifestResourceNames()
        |> Array.filter (fun name -> name.StartsWith("Sql/", StringComparison.Ordinal) && name.EndsWith ".sql")
        |> Array.map (fun name -> name["Sql/".Length .. name.Length - ".sql".Length - 1])
        |> Array.sort

    /// <summary>Loads <c>Sql/{name}.sql</c>; a missing file fails at module initialisation, not
    /// in the middle of a request.</summary>
    let load (name: string) =
        match assembly.GetManifestResourceStream $"Sql/{name}.sql" with
        | null -> invalidOp $"Embedded SQL 'Sql/{name}.sql' does not exist."
        | stream ->
            use reader = new StreamReader(stream)
            reader.ReadToEnd()

[<RequireQualifiedAccess>]
module InvoiceSql =
    let allocateNumber = Sql.load "Invoices/allocate-number"
    let documentHeader = Sql.load "Invoices/document-header"
    let documentLines = Sql.load "Invoices/document-lines"
    let existingNumber = Sql.load "Invoices/existing-number"
    let exists = Sql.load "Invoices/exists"
    let forOrder = Sql.load "Invoices/for-order"
    let insertDocument = Sql.load "Invoices/insert-document"
    let insertSnapshot = Sql.load "Invoices/insert-snapshot"
    let latestDocument = Sql.load "Invoices/latest-document"
    let list = Sql.load "Invoices/list"
    let snapshotCheck = Sql.load "Invoices/snapshot-check"
