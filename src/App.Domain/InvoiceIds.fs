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
/// fiscal period. Allocated only inside the issuance transaction, never derived from
/// <c>MAX(number)+1</c> or a database sequence.</summary>
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

    let create legalEntity series fiscalPeriod sequence =
        if not (validScope legalEntity) then
            Error "An invoice legal entity must be a short upper-case code."
        elif not (validScope series) then
            Error "An invoice series must be a short upper-case code."
        elif not (validScope fiscalPeriod) then
            Error "An invoice fiscal period must be a short upper-case code."
        elif sequence < 1L then
            Error "An invoice sequence must be positive."
        else
            Ok
                { legalEntity = legalEntity
                  series = series
                  fiscalPeriod = fiscalPeriod
                  sequence = sequence }

    /// <summary>The customer-facing number, e.g. <c>INV-2026-000042</c>.</summary>
    let display (number: InvoiceNumber) =
        $"{number.Series}-{number.FiscalPeriod}-{number.Sequence:D6}"

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
