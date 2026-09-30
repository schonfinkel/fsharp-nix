namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open App.Invoices
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

/// <summary>How long a render may take before the render scanner re-requests it, and how many
/// times it does so before the invoice is parked in <c>RenderFailed</c>.</summary>
type InvoiceRenderPolicy =
    { CheckAfter: TimeSpan; MaxChecks: int }

[<RequireQualifiedAccess>]
module InvoiceRenderPolicy =
    let defaults =
        { CheckAfter = TimeSpan.FromMinutes 10.
          MaxChecks = 3 }

/// <summary>The issuing legal entity, snapshotted onto every invoice at issuance.</summary>
type InvoiceIssuer =
    {
        LegalEntity: string
        Series: string
        /// <summary>Series for credit notes, numbered in their own gapless sequence.</summary>
        CreditSeries: string
        Name: string
        Address: string
        TaxId: string
    }

[<RequireQualifiedAccess>]
module InvoiceIssuer =
    let validate (issuer: InvoiceIssuer) =
        if not (InvoiceNumber.validScope issuer.LegalEntity) then
            Error "Invoicing:LegalEntity must be a short upper-case code."
        elif not (InvoiceNumber.validScope issuer.Series) then
            Error "Invoicing:Series must be a short upper-case code."
        elif
            not (InvoiceNumber.validScope issuer.CreditSeries)
            || issuer.CreditSeries = issuer.Series
        then
            Error "Invoicing:CreditSeries must be a short upper-case code distinct from the invoice series."
        elif
            String.IsNullOrWhiteSpace issuer.Name
            || String.IsNullOrWhiteSpace issuer.Address
        then
            Error "Invoicing:SellerName and Invoicing:SellerAddress are required."
        else
            Ok issuer

    /// <summary>Fiscal periods are calendar months in UTC (<c>yyyy-MM</c>); each month's
    /// numbering restarts at 1.</summary>
    let fiscalPeriod (issuedAt: DateTimeOffset) =
        issuedAt.UtcDateTime.ToString("yyyy-MM", Globalization.CultureInfo.InvariantCulture)

[<RequireQualifiedAccess>]
module InvoiceSql =
    let allocateNumber = Sql.load "Invoices/allocate-number"
    let documentHeader = Sql.load "Invoices/document-header"
    let documentLines = Sql.load "Invoices/document-lines"
    let existingNumber = Sql.load "Invoices/existing-number"
    let exists = Sql.load "Invoices/exists"
    let forOrder = Sql.load "Invoices/for-order"
    let insertDocument = Sql.load "Invoices/insert-document"
    let insertRenderCheck = Sql.load "Invoices/insert-render-check"
    let insertSnapshot = Sql.load "Invoices/insert-snapshot"
    let latestDocument = Sql.load "Invoices/latest-document"
    let creditsForOrder = Sql.load "Invoices/credits-for-order"
    let list = Sql.load "Invoices/list"
    let snapshotCheck = Sql.load "Invoices/snapshot-check"

[<RequireQualifiedAccess>]
module CreditNoteSql =
    let cancellationLines = Sql.load "CreditNotes/cancellation-lines"
    let cancellationTotals = Sql.load "CreditNotes/cancellation-totals"
    let insertHeader = Sql.load "CreditNotes/insert-header"
    let insertLine = Sql.load "CreditNotes/insert-line"
    let original = Sql.load "CreditNotes/original"
    let returnLines = Sql.load "CreditNotes/return-lines"
    let returnTotals = Sql.load "CreditNotes/return-totals"
    let settledRefund = Sql.load "CreditNotes/settled-refund"

/// <summary>One rendered invoice line, read back from the immutable snapshot.</summary>
type InvoiceDocumentLine =
    { Sku: string
      Description: string
      Quantity: int
      UnitPrice: decimal
      Amount: decimal }

/// <summary>Everything a renderer needs, read from <c>fsnix.invoices</c>/<c>invoice_lines</c> only
/// (never from mutable order or catalog state), so re-rendering reproduces the issued content.</summary>
type InvoiceDocument =
    {
        InvoiceId: InvoiceId
        Number: InvoiceNumber
        IssuedAt: DateTimeOffset
        SellerName: string
        SellerAddress: string
        SellerTaxId: string
        BuyerEmail: string
        BillingAddress: string list
        Lines: InvoiceDocumentLine list
        Subtotal: decimal
        Shipping: decimal
        Tax: decimal
        Total: decimal
        Currency: string
        /// <summary>For a credit note, the number of the invoice it credits.</summary>
        Credits: InvoiceNumber option
    }

/// <summary>A named PDF template. Bump <c>Name</c> whenever the output changes; stored documents
/// keep the renderer that produced them.</summary>
type InvoiceRenderer =
    { Name: string
      Render: InvoiceDocument -> byte array }

[<RequireQualifiedAccess>]
module InvoiceDocuments =
    let private addressLines (json: string) =
        use document = Text.Json.JsonDocument.Parse json
        let root = document.RootElement

        let field (name: string) =
            match root.TryGetProperty name with
            | true, value when value.ValueKind = Text.Json.JsonValueKind.String -> value.GetString()
            | _ -> ""

        [ field "recipient"
          field "line1"
          field "line2"
          String.Join(" ", [ field "city"; field "region"; field "postalCode" ] |> List.filter ((<>) ""))
          field "countryCode" ]
        |> List.filter (not << String.IsNullOrWhiteSpace)

    /// <summary>Loads the issued snapshot, or <c>None</c> when the invoice does not exist.</summary>
    let load (dataSource: NpgsqlDataSource) (invoiceId: InvoiceId) (ct: CancellationToken) =
        Execution.executeRead dataSource ct (fun connection token ->
            task {
                use header = new NpgsqlCommand(InvoiceSql.documentHeader, connection)

                header.Parameters.AddWithValue("invoice", InvoiceId.value invoiceId) |> ignore
                use! reader = header.ExecuteReaderAsync(token: CancellationToken)
                let! found = reader.ReadAsync token

                if not found then
                    return None
                else
                    let number =
                        InvoiceNumber.create
                            (reader.GetString 0)
                            (reader.GetString 1)
                            (reader.GetString 2)
                            (reader.GetInt64 3)
                        |> Result.defaultWith invalidOp

                    let document =
                        { InvoiceId = invoiceId
                          Number = number
                          IssuedAt = reader.GetFieldValue<DateTimeOffset> 4
                          SellerName = reader.GetString 5
                          SellerAddress = reader.GetString 6
                          SellerTaxId = reader.GetString 7
                          BuyerEmail = reader.GetString 8
                          BillingAddress = addressLines (reader.GetString 9)
                          Lines = []
                          Subtotal = reader.GetDecimal 10
                          Shipping = reader.GetDecimal 11
                          Tax = reader.GetDecimal 12
                          Total = reader.GetDecimal 13
                          Currency = reader.GetString 14
                          Credits =
                            if reader.IsDBNull 15 then
                                None
                            else
                                InvoiceNumber.create
                                    (reader.GetString 15)
                                    (reader.GetString 16)
                                    (reader.GetString 17)
                                    (reader.GetInt64 18)
                                |> Result.toOption }

                    do! reader.CloseAsync()

                    use lines = new NpgsqlCommand(InvoiceSql.documentLines, connection)

                    lines.Parameters.AddWithValue("invoice", InvoiceId.value invoiceId) |> ignore
                    use! lineReader = lines.ExecuteReaderAsync(token: CancellationToken)
                    let rows = Collections.Generic.List<InvoiceDocumentLine>()

                    while! lineReader.ReadAsync token do
                        rows.Add
                            { Sku = lineReader.GetString 0
                              Description = lineReader.GetString 1
                              Quantity = lineReader.GetInt32 2
                              UnitPrice = lineReader.GetDecimal 3
                              Amount = lineReader.GetDecimal 4 }

                    return
                        Some
                            { document with
                                Lines = List.ofSeq rows }
            })

/// <summary>
/// The invoice issuance transaction (PLAN "Issuance transaction"). One
/// <c>executeIdempotentTransaction</c> holds the action receipt, the scope-counter row lock and
/// increment, the insert-only header and lines, and the <c>SnapshotIssued</c> callback, so a
/// rollback at any point releases the number. An ambiguous commit is resolved by the invoice
/// business key (<c>invoice_id</c>/<c>order_id</c>), never by incrementing the counter again.
/// </summary>
[<RequireQualifiedAccess>]
module InvoiceEffects =
    let actionKind =
        function
        | IssueSnapshot _ -> "issue-invoice-snapshot"
        | RenderDocument _ -> "render-invoice-document"

    let private invoiceError (result: Task<Result<'T, ReceiptFailure>>) =
        task {
            let! value = result

            return
                value
                |> Result.mapError (function
                    | ReceiptFailure.EncodingFailed -> InvoiceActionError.CallbackEncodingFailed
                    | ReceiptFailure.Mismatch -> InvoiceActionError.ActionReceiptMismatch)
        }

    let private tryExistingNumber (connection: NpgsqlConnection) tx (request: InvoiceRequest) ct =
        task {
            use command = new NpgsqlCommand(InvoiceSql.existingNumber, connection, tx)

            command.Parameters.AddWithValue("invoice", InvoiceId.value request.InvoiceId)
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct: CancellationToken)
            let! found = reader.ReadAsync ct

            if not found then
                return None
            elif reader.GetString 0 <> request.OrderId then
                return Some(Error(ReasonCode.ofLiteral "invoice-order-mismatch"))
            else
                return
                    InvoiceNumber.create
                        (reader.GetString 1)
                        (reader.GetString 2)
                        (reader.GetString 3)
                        (reader.GetInt64 4)
                    |> Result.mapError (fun _ -> ReasonCode.ofLiteral "invoice-number-invalid")
                    |> Some
        }

    /// <summary>Checks the order snapshot before a number is allocated: it must exist, belong
    /// to the order, carry at least one line in the header currency, and its lines must sum to
    /// the subtotal. Returns the failure reason, if any.</summary>
    let private snapshotProblem (connection: NpgsqlConnection) tx (request: InvoiceRequest) ct =
        task {
            use command = new NpgsqlCommand(InvoiceSql.snapshotCheck, connection, tx)

            command.Parameters.AddWithValue("snapshot", OrderSnapshotId.value request.SnapshotId)
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct: CancellationToken)
            let! found = reader.ReadAsync ct

            if not found || reader.GetString 0 <> request.OrderId then
                return Some(ReasonCode.ofLiteral "snapshot-missing")
            elif not (reader.GetBoolean 1) then
                return Some(ReasonCode.ofLiteral "snapshot-totals-inconsistent")
            elif reader.GetInt64 2 = 0L || reader.IsDBNull 3 || not (reader.GetBoolean 3) then
                return Some(ReasonCode.ofLiteral "snapshot-lines-invalid")
            elif not (reader.GetBoolean 4) then
                return Some(ReasonCode.ofLiteral "snapshot-subtotal-mismatch")
            else
                return None
        }

    /// <summary>Locks the scope's counter row (creating it on first use) and returns the next
    /// number. The row lock serializes concurrent issuers in the same scope until commit.</summary>
    let private allocate (connection: NpgsqlConnection) tx (issuer: InvoiceIssuer) (series: string) fiscalPeriod ct =
        task {
            use command = new NpgsqlCommand(InvoiceSql.allocateNumber, connection, tx)

            command.Parameters.AddWithValue("entity", issuer.LegalEntity) |> ignore
            command.Parameters.AddWithValue("series", series) |> ignore
            command.Parameters.AddWithValue("period", fiscalPeriod) |> ignore
            let! value = command.ExecuteScalarAsync ct
            return value :?> int64
        }

    let private insertSnapshot
        (connection: NpgsqlConnection)
        tx
        (issuer: InvoiceIssuer)
        (record: ActionRecord<InvoiceEntityId, InvoiceAction>)
        (request: InvoiceRequest)
        (number: InvoiceNumber)
        (issuedAt: DateTimeOffset)
        ct
        =
        task {
            use command = new NpgsqlCommand(InvoiceSql.insertSnapshot, connection, tx)

            command.Parameters.AddWithValue("invoice", InvoiceId.value request.InvoiceId)
            |> ignore

            command.Parameters.AddWithValue("snapshot", OrderSnapshotId.value request.SnapshotId)
            |> ignore

            command.Parameters.AddWithValue("entity", number.LegalEntity) |> ignore
            command.Parameters.AddWithValue("series", number.Series) |> ignore
            command.Parameters.AddWithValue("period", number.FiscalPeriod) |> ignore
            command.Parameters.AddWithValue("number", number.Sequence) |> ignore
            command.Parameters.AddWithValue("issued_at", issuedAt) |> ignore
            command.Parameters.AddWithValue("seller_name", issuer.Name) |> ignore
            command.Parameters.AddWithValue("seller_address", issuer.Address) |> ignore
            command.Parameters.AddWithValue("seller_tax_id", issuer.TaxId) |> ignore

            command.Parameters.AddWithValue("machine", MachineId.value record.MachineId)
            |> ignore

            command.Parameters.AddWithValue("command", CommandId.value record.CommandId)
            |> ignore

            command.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore
            let! _ = command.ExecuteNonQueryAsync ct
            return ()
        }

    /// <summary>One credited line, priced exactly as the original invoice priced it.</summary>
    type private CreditLine =
        { OrderLineId: Guid
          ProductId: Guid
          Sku: string
          Description: string
          UnitPrice: decimal
          Quantity: int }

    type private CreditDraft =
        { OriginalInvoiceId: Guid
          RefundId: Guid
          Lines: CreditLine list
          Shipping: decimal
          Tax: decimal
          Currency: string }

    let private readCreditLines (command: NpgsqlCommand) ct =
        task {
            use! reader = command.ExecuteReaderAsync(ct: CancellationToken)
            let lines = Collections.Generic.List<CreditLine>()

            while! reader.ReadAsync ct do
                lines.Add
                    { OrderLineId = reader.GetGuid 0
                      ProductId = reader.GetGuid 1
                      Sku = reader.GetString 2
                      Description = reader.GetString 3
                      UnitPrice = reader.GetDecimal 4
                      Quantity = reader.GetInt32 5 }

            return List.ofSeq lines
        }

    /// <summary>
    /// Assembles and checks a credit note before any number is allocated: the original invoice
    /// must exist, the refund must be settled, the reversed lines must all be on the original
    /// invoice, and merchandise plus the recorded shipping and tax must equal the refunded amount
    /// exactly. Returns the failure reason otherwise.
    /// </summary>
    let private prepareCredit (connection: NpgsqlConnection) tx (request: InvoiceRequest) (credit: CreditSource) ct =
        task {
            let refund = RefundId.value credit.RefundId

            let command sql (bind: NpgsqlCommand -> unit) =
                let command = new NpgsqlCommand(sql, connection, tx)
                command.Parameters.AddWithValue("order", request.OrderId) |> ignore
                bind command
                command

            use original = command CreditNoteSql.original ignore
            let! originalRow = original.ExecuteReaderAsync(ct: CancellationToken)
            let! hasOriginal = originalRow.ReadAsync ct

            let originalInvoice =
                if hasOriginal then
                    Some(originalRow.GetGuid 0, originalRow.GetString 1)
                else
                    None

            do! originalRow.DisposeAsync()

            use settled =
                command CreditNoteSql.settledRefund (fun c -> c.Parameters.AddWithValue("refund", refund) |> ignore)

            let! settledRow = settled.ExecuteReaderAsync(ct: CancellationToken)
            let! hasSettled = settledRow.ReadAsync ct

            let refunded =
                if hasSettled then
                    Some(settledRow.GetDecimal 0, settledRow.GetString 1)
                else
                    None

            do! settledRow.DisposeAsync()

            match originalInvoice, refunded with
            | None, _ -> return Error(ReasonCode.ofLiteral "original-invoice-missing")
            | _, None -> return Error(ReasonCode.ofLiteral "refund-not-settled")
            | Some(originalId, invoiceCurrency), Some(refundedAmount, refundCurrency) ->
                let totalsSql, linesSql, bindSource =
                    match credit.Origin with
                    | OrderCancellation _ ->
                        CreditNoteSql.cancellationTotals,
                        CreditNoteSql.cancellationLines,
                        (fun (c: NpgsqlCommand) -> c.Parameters.AddWithValue("refund", refund) |> ignore)
                    | InspectedReturn(_, returnId) ->
                        CreditNoteSql.returnTotals,
                        CreditNoteSql.returnLines,
                        (fun (c: NpgsqlCommand) -> c.Parameters.AddWithValue("return", returnId) |> ignore)

                use totals = command totalsSql bindSource
                let! totalsRow = totals.ExecuteReaderAsync(ct: CancellationToken)
                let! hasTotals = totalsRow.ReadAsync ct

                let source =
                    if hasTotals && not (totalsRow.IsDBNull 2) then
                        Some(totalsRow.GetDecimal 0, totalsRow.GetDecimal 1, totalsRow.GetString 2)
                    else
                        None

                do! totalsRow.DisposeAsync()

                match source with
                | None -> return Error(ReasonCode.ofLiteral "credit-source-missing")
                | Some(shipping, tax, sourceCurrency) ->
                    use lines =
                        command linesSql (fun c ->
                            bindSource c
                            c.Parameters.AddWithValue("original", originalId) |> ignore)

                    let! creditLines = readCreditLines lines ct

                    let merchandise =
                        creditLines |> List.sumBy (fun line -> line.UnitPrice * decimal line.Quantity)

                    if creditLines.IsEmpty then
                        return Error(ReasonCode.ofLiteral "credit-lines-missing")
                    elif sourceCurrency <> invoiceCurrency || refundCurrency <> invoiceCurrency then
                        return Error(ReasonCode.ofLiteral "credit-currency-mismatch")
                    elif merchandise + shipping + tax <> refundedAmount then
                        return Error(ReasonCode.ofLiteral "credit-amount-mismatch")
                    else
                        return
                            Ok
                                { OriginalInvoiceId = originalId
                                  RefundId = refund
                                  Lines = creditLines
                                  Shipping = shipping
                                  Tax = tax
                                  Currency = invoiceCurrency }
        }

    let private insertCredit
        (connection: NpgsqlConnection)
        tx
        (record: ActionRecord<InvoiceEntityId, InvoiceAction>)
        (request: InvoiceRequest)
        (draft: CreditDraft)
        (number: InvoiceNumber)
        (issuedAt: DateTimeOffset)
        ct
        =
        task {
            let subtotal =
                draft.Lines |> List.sumBy (fun line -> line.UnitPrice * decimal line.Quantity)

            use header = new NpgsqlCommand(CreditNoteSql.insertHeader, connection, tx)

            let add (name: string) (value: obj) =
                header.Parameters.AddWithValue(name, value) |> ignore

            add "invoice" (InvoiceId.value request.InvoiceId)
            add "original" draft.OriginalInvoiceId
            add "refund" draft.RefundId
            add "entity" number.LegalEntity
            add "series" number.Series
            add "period" number.FiscalPeriod
            add "number" number.Sequence
            add "issued_at" issuedAt
            add "subtotal" subtotal
            add "shipping" draft.Shipping
            add "tax" draft.Tax
            add "total" (subtotal + draft.Shipping + draft.Tax)
            add "machine" (MachineId.value record.MachineId)
            add "command" (CommandId.value record.CommandId)
            add "ordinal" record.Ordinal
            let! _ = header.ExecuteNonQueryAsync ct

            for index, line in List.indexed draft.Lines do
                use row = new NpgsqlCommand(CreditNoteSql.insertLine, connection, tx)

                let add (name: string) (value: obj) =
                    row.Parameters.AddWithValue(name, value) |> ignore

                add "invoice" (InvoiceId.value request.InvoiceId)
                add "line_number" (index + 1)
                add "order_line" line.OrderLineId
                add "product" line.ProductId
                add "sku" line.Sku
                add "description" line.Description
                add "unit_price" line.UnitPrice
                add "quantity" line.Quantity
                add "line_amount" (line.UnitPrice * decimal line.Quantity)
                add "currency" draft.Currency
                let! _ = row.ExecuteNonQueryAsync ct
                ()
        }

    let private report connection tx record purpose (invoiceId: InvoiceId) event ct =
        WorkflowEffects.deliver
            connection
            tx
            record
            purpose
            Invoices.MachineKey
            (EntityId.value (Invoices.invoiceEntityId invoiceId))
            InvoiceCodec.event
            event
            ct
        |> invoiceError

    let private receipt record =
        fun connection tx token ->
            WorkflowEffects.receiptFor InvoiceCodec.action actionKind connection tx record token
            |> invoiceError

    let applyIssue
        (dataSource: NpgsqlDataSource)
        (issuer: InvoiceIssuer)
        (renderPolicy: InvoiceRenderPolicy)
        (issuedAt: DateTimeOffset)
        (record: ActionRecord<InvoiceEntityId, InvoiceAction>)
        (ct: CancellationToken)
        : Task<Result<unit, InvoiceActionError>> =
        match record.Action with
        | IssueSnapshot request when
            Result.isOk (InvoiceRequest.validate request)
            && Invoices.invoiceEntityId request.InvoiceId = record.EntityId
            ->
            WorkflowEffects.runLocalEffect
                dataSource
                (receipt record)
                (fun connection tx token ->
                    task {
                        let report = report connection tx record "invoice-issuance" request.InvoiceId

                        let failed reason =
                            report (IssuanceFailed(request.InvoiceId, reason)) token

                        let issued number =
                            report (SnapshotIssued(request.InvoiceId, number)) token

                        match! tryExistingNumber connection tx request token with
                        | Some(Ok number) -> return! issued number
                        | Some(Error reason) -> return! failed reason
                        | None ->
                            // Both kinds share numbering, render-check arming and the callback;
                            // only the checks and the rows written differ.
                            let! prepared =
                                match request.Credit with
                                | None ->
                                    task {
                                        match! snapshotProblem connection tx request token with
                                        | Some reason -> return Error reason
                                        | None ->
                                            return
                                                Ok(
                                                    issuer.Series,
                                                    fun number ->
                                                        insertSnapshot
                                                            connection
                                                            tx
                                                            issuer
                                                            record
                                                            request
                                                            number
                                                            issuedAt
                                                            token
                                                )
                                    }
                                | Some credit ->
                                    task {
                                        match! prepareCredit connection tx request credit token with
                                        | Error reason -> return Error reason
                                        | Ok draft ->
                                            return
                                                Ok(
                                                    issuer.CreditSeries,
                                                    fun number ->
                                                        insertCredit
                                                            connection
                                                            tx
                                                            record
                                                            request
                                                            draft
                                                            number
                                                            issuedAt
                                                            token
                                                )
                                    }

                            match prepared with
                            | Error reason -> return! failed reason
                            | Ok(series, write) ->
                                let period = InvoiceIssuer.fiscalPeriod issuedAt
                                let! sequence = allocate connection tx issuer series period token

                                match InvoiceNumber.create issuer.LegalEntity series period sequence with
                                | Error _ when sequence > InvoiceNumber.MaxSequence ->
                                    return! failed (ReasonCode.ofLiteral "invoice-sequence-exhausted")
                                | Error _ -> return! failed (ReasonCode.ofLiteral "invoice-number-invalid")
                                | Ok number ->
                                    do! write number

                                    use check = new NpgsqlCommand(InvoiceSql.insertRenderCheck, connection, tx)

                                    check.Parameters.AddWithValue("invoice", InvoiceId.value request.InvoiceId)
                                    |> ignore

                                    check.Parameters.AddWithValue("due", issuedAt + renderPolicy.CheckAfter)
                                    |> ignore

                                    let! _ = check.ExecuteNonQueryAsync token
                                    return! issued number
                    })
                ct
        | _ -> Task.FromResult(Error InvoiceActionError.InvalidAction)

    /// <summary>
    /// Renders the stored snapshot outside any transaction, then records the receipt, inserts the
    /// content-addressed document and queues the result callback in one transaction. Rendering is
    /// deterministic, so a redelivered action lands on the same <c>(invoice_id, sha256)</c> row.
    /// A renderer exception is reported as a bounded <c>render-failed</c> reason, never its text.
    /// </summary>
    let applyRender
        (dataSource: NpgsqlDataSource)
        (renderer: InvoiceRenderer)
        (record: ActionRecord<InvoiceEntityId, InvoiceAction>)
        (ct: CancellationToken)
        : Task<Result<unit, InvoiceActionError>> =
        match record.Action with
        | RenderDocument issued when Invoices.invoiceEntityId issued.Request.InvoiceId = record.EntityId ->
            let invoiceId = issued.Request.InvoiceId

            task {
                let! document = InvoiceDocuments.load dataSource invoiceId ct

                let outcome =
                    match document with
                    | None -> Error(ReasonCode.ofLiteral "invoice-missing")
                    | Some document when document.Number <> issued.Number ->
                        Error(ReasonCode.ofLiteral "invoice-number-mismatch")
                    | Some document ->
                        try
                            match renderer.Render document with
                            | bytes when isNull bytes || bytes.Length = 0 -> Error(ReasonCode.ofLiteral "render-empty")
                            | bytes -> Ok bytes
                        with _ ->
                            Error(ReasonCode.ofLiteral "render-failed")

                return!
                    WorkflowEffects.runLocalEffect
                        dataSource
                        (receipt record)
                        (fun connection tx token ->
                            task {
                                let report = report connection tx record "invoice-render" invoiceId

                                match outcome with
                                | Error reason -> return! report (DocumentRenderFailed(invoiceId, reason)) token
                                | Ok bytes ->
                                    let sha256 = Security.Cryptography.SHA256.HashData(bytes: byte array)

                                    use insert = new NpgsqlCommand(InvoiceSql.insertDocument, connection, tx)

                                    insert.Parameters.AddWithValue("invoice", InvoiceId.value invoiceId) |> ignore
                                    insert.Parameters.AddWithValue("sha256", sha256) |> ignore
                                    insert.Parameters.AddWithValue("renderer", renderer.Name) |> ignore
                                    insert.Parameters.AddWithValue("content", bytes) |> ignore
                                    insert.Parameters.AddWithValue("size", bytes.Length) |> ignore
                                    let! _ = insert.ExecuteNonQueryAsync token

                                    let digest = DocumentDigest.ofBytes sha256 |> Result.defaultWith invalidOp
                                    return! report (DocumentRendered(invoiceId, digest)) token
                            })
                        ct
            }
        | _ -> Task.FromResult(Error InvoiceActionError.InvalidAction)
