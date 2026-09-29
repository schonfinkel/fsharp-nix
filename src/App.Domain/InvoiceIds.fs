namespace App.Domain

open System

/// <summary>Identifies one invoice document. An order has exactly one invoice, so the id is
/// the order's snapshot id: re-requesting issuance for the same order resolves to the same
/// invoice business key instead of allocating a second number.</summary>
[<Struct>]
type InvoiceId = private InvoiceId of Guid

[<RequireQualifiedAccess>]
module InvoiceId =
    let create value =
        if value = Guid.Empty then
            Error "An invoice id is required."
        else
            Ok(InvoiceId value)

    let ofSnapshot (snapshotId: OrderSnapshotId) =
        InvoiceId(OrderSnapshotId.value snapshotId)

    let value (InvoiceId value) = value
    let wireString id = (value id).ToString("D")

    let tryParse (text: string) =
        match Guid.TryParseExact(text, "D") with
        | true, value -> create value
        | _ -> Error "An invoice id must be a GUID in D format."

/// <summary>A legally gapless invoice number: a sequence scoped to legal entity, series and
/// monthly fiscal period (<c>yyyy-MM</c>), displayed as <c>INV-2026-09-00000042</c>. Allocated only
/// inside the issuance transaction, never derived from <c>MAX(number)+1</c> or a database
/// sequence.</summary>
type InvoiceNumber =
    private
        { legalEntity: string
          series: string
          fiscalPeriod: string
          sequence: int64 }

    member this.LegalEntity = this.legalEntity
    member this.Series = this.series
    member this.FiscalPeriod = this.fiscalPeriod
    member this.Sequence = this.sequence

[<RequireQualifiedAccess>]
module InvoiceNumber =
    [<Literal>]
    let MaxScopeLength = 16

    /// <summary>Eight display digits per period.</summary>
    [<Literal>]
    let MaxSequence = 99999999L

    /// <summary>Scope components are upper-case ASCII letters, digits and inner hyphens, so a
    /// formatted number is unambiguous.</summary>
    let validScope (value: string) =
        not (isNull value)
        && value.Length >= 1
        && value.Length <= MaxScopeLength
        && value[0] <> '-'
        && value[value.Length - 1] <> '-'
        && value
           |> Seq.forall (fun c -> (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '-')

    /// <summary>A calendar month, <c>yyyy-MM</c> with month 01-12.</summary>
    let validFiscalPeriod (value: string) =
        not (isNull value)
        && value.Length = 7
        && value[4] = '-'
        && value
           |> Seq.indexed
           |> Seq.forall (fun (index, c) -> index = 4 || Char.IsAsciiDigit c)
        && (let month = int value[5..6] in month >= 1 && month <= 12)

    let create legalEntity series fiscalPeriod sequence =
        if not (validScope legalEntity) then
            Error "An invoice legal entity must be a short upper-case code."
        elif not (validScope series) then
            Error "An invoice series must be a short upper-case code."
        elif not (validFiscalPeriod fiscalPeriod) then
            Error "An invoice fiscal period must be a month, yyyy-MM."
        elif sequence < 1L || sequence > MaxSequence then
            Error "An invoice sequence must be between 1 and 99999999."
        else
            Ok
                { legalEntity = legalEntity
                  series = series
                  fiscalPeriod = fiscalPeriod
                  sequence = sequence }

    /// <summary>The customer-facing number, e.g. <c>INV-2026-09-00000042</c>.</summary>
    let display (number: InvoiceNumber) =
        $"{number.Series}-{number.FiscalPeriod}-{number.Sequence:D8}"

/// <summary>An order's request for its invoice. The order id and snapshot id are both carried
/// so issuance can verify the snapshot belongs to the requesting order.</summary>
type InvoiceRequest =
    { InvoiceId: InvoiceId
      OrderId: string
      SnapshotId: OrderSnapshotId }

[<RequireQualifiedAccess>]
module InvoiceRequest =
    let forOrder (snapshotId: OrderSnapshotId) =
        { InvoiceId = InvoiceId.ofSnapshot snapshotId
          OrderId = $"order:{OrderSnapshotId.value snapshotId:D}"
          SnapshotId = snapshotId }

    let validate (request: InvoiceRequest) =
        if InvoiceId.value request.InvoiceId <> OrderSnapshotId.value request.SnapshotId then
            Error "An invoice id must match its order snapshot."
        elif request.OrderId <> $"order:{OrderSnapshotId.value request.SnapshotId:D}" then
            Error "An invoice order id must match its order snapshot."
        else
            Ok request

/// <summary>The SHA-256 of a stored invoice document, as 64 lowercase hex characters. It is the
/// document's content address.</summary>
[<Struct>]
type DocumentDigest = private DocumentDigest of string

[<RequireQualifiedAccess>]
module DocumentDigest =
    let create (text: string) =
        if
            not (isNull text)
            && text.Length = 64
            && text |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
        then
            Ok(DocumentDigest text)
        else
            Error "A document digest must be a lowercase hex SHA-256."

    let ofBytes (sha256: byte array) =
        if isNull sha256 || sha256.Length <> 32 then
            Error "A document digest must be 32 bytes."
        else
            Ok(DocumentDigest(Convert.ToHexStringLower sha256))

    let value (DocumentDigest text) = text
    let bytes (DocumentDigest text) = Convert.FromHexString text
