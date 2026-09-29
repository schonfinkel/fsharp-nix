namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open App.Invoices
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

/// <summary>The issuing legal entity, snapshotted onto every invoice at issuance.</summary>
type InvoiceIssuer =
    { LegalEntity: string
      Series: string
      Name: string
      Address: string
      TaxId: string }

[<RequireQualifiedAccess>]
module InvoiceIssuer =
    let validate (issuer: InvoiceIssuer) =
        if not (InvoiceNumber.validScope issuer.LegalEntity) then
            Error "Invoicing:LegalEntity must be a short upper-case code."
        elif not (InvoiceNumber.validScope issuer.Series) then
            Error "Invoicing:Series must be a short upper-case code."
        elif
            String.IsNullOrWhiteSpace issuer.Name
            || String.IsNullOrWhiteSpace issuer.Address
        then
            Error "Invoicing:SellerName and Invoicing:SellerAddress are required."
        else
            Ok issuer

    /// <summary>Fiscal periods are calendar years in UTC.</summary>
    let fiscalPeriod (issuedAt: DateTimeOffset) =
        issuedAt.UtcDateTime.Year.ToString("D4", Globalization.CultureInfo.InvariantCulture)

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
            use command =
                new NpgsqlCommand(
                    "SELECT order_id,legal_entity,series,fiscal_period,number FROM fsnix.invoices WHERE invoice_id=@invoice",
                    connection,
                    tx
                )

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
            use command =
                new NpgsqlCommand(
                    """SELECT s.order_id,
                              s.total_amount = s.subtotal_amount + s.shipping_amount + s.tax_amount,
                              (SELECT count(*) FROM jsonb_array_elements(s.lines)),
                              (SELECT bool_and(l->>'currency' = s.currency AND (l->>'quantity')::int > 0)
                                 FROM jsonb_array_elements(s.lines) l),
                              (SELECT coalesce(sum((l->>'amount')::numeric * (l->>'quantity')::int), 0)
                                 FROM jsonb_array_elements(s.lines) l) = s.subtotal_amount
                       FROM fsnix.order_snapshots s WHERE s.snapshot_id=@snapshot""",
                    connection,
                    tx
                )

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
    let private allocate (connection: NpgsqlConnection) tx (issuer: InvoiceIssuer) fiscalPeriod ct =
        task {
            use command =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.invoice_counters(legal_entity,series,fiscal_period)
                       VALUES(@entity,@series,@period) ON CONFLICT DO NOTHING;
                       UPDATE fsnix.invoice_counters
                          SET last_number = last_number + 1, updated_at = STATEMENT_TIMESTAMP()
                        WHERE legal_entity=@entity AND series=@series AND fiscal_period=@period
                       RETURNING last_number""",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("entity", issuer.LegalEntity) |> ignore
            command.Parameters.AddWithValue("series", issuer.Series) |> ignore
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
            use command =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.invoices
                           (invoice_id, order_id, snapshot_id, legal_entity, series, fiscal_period, number,
                            issued_at, seller, buyer, billing_address,
                            subtotal_amount, shipping_amount, tax_amount, total_amount, currency,
                            source_machine_id, source_command_id, source_ordinal)
                       SELECT @invoice, s.order_id, s.snapshot_id, @entity, @series, @period, @number,
                              @issued_at,
                              jsonb_build_object('legalEntity', @entity, 'name', @seller_name,
                                                 'address', @seller_address, 'taxId', @seller_tax_id),
                              jsonb_build_object('customerId', s.customer_id, 'email', s.customer_email),
                              s.address,
                              s.subtotal_amount, s.shipping_amount, s.tax_amount, s.total_amount, s.currency,
                              @machine, @command, @ordinal
                         FROM fsnix.order_snapshots s WHERE s.snapshot_id=@snapshot;
                       INSERT INTO fsnix.invoice_lines
                           (invoice_id, line_number, order_line_id, product_id, sku, description,
                            unit_price, quantity, line_amount, currency)
                       SELECT @invoice, l.n::int, (l.line->>'lineId')::uuid, (l.line->>'productId')::uuid,
                              l.line->>'sku', l.line->>'name',
                              (l.line->>'amount')::numeric, (l.line->>'quantity')::int,
                              (l.line->>'amount')::numeric * (l.line->>'quantity')::int,
                              l.line->>'currency'
                         FROM fsnix.order_snapshots s,
                              jsonb_array_elements(s.lines) WITH ORDINALITY AS l(line, n)
                        WHERE s.snapshot_id=@snapshot""",
                    connection,
                    tx
                )

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

    let private report connection tx record (request: InvoiceRequest) event ct =
        WorkflowEffects.deliver
            connection
            tx
            record
            "invoice-issuance"
            Invoices.MachineKey
            (EntityId.value (Invoices.invoiceEntityId request.InvoiceId))
            InvoiceCodec.event
            event
            ct
        |> invoiceError

    let applyIssue
        (dataSource: NpgsqlDataSource)
        (issuer: InvoiceIssuer)
        (issuedAt: DateTimeOffset)
        (record: ActionRecord<InvoiceEntityId, InvoiceAction>)
        (ct: CancellationToken)
        : Task<Result<unit, InvoiceActionError>> =
        let (IssueSnapshot request) = record.Action

        if
            Result.isError (InvoiceRequest.validate request)
            || Invoices.invoiceEntityId request.InvoiceId <> record.EntityId
        then
            Task.FromResult(Error InvoiceActionError.InvalidAction)
        else
            WorkflowEffects.runLocalEffect
                dataSource
                (fun connection tx token ->
                    WorkflowEffects.receiptFor InvoiceCodec.action actionKind connection tx record token
                    |> invoiceError)
                (fun connection tx token ->
                    task {
                        let failed reason =
                            report connection tx record request (IssuanceFailed(request.InvoiceId, reason)) token

                        let issued number =
                            report connection tx record request (SnapshotIssued(request.InvoiceId, number)) token

                        match! tryExistingNumber connection tx request token with
                        | Some(Ok number) -> return! issued number
                        | Some(Error reason) -> return! failed reason
                        | None ->
                            match! snapshotProblem connection tx request token with
                            | Some reason -> return! failed reason
                            | None ->
                                let period = InvoiceIssuer.fiscalPeriod issuedAt
                                let! sequence = allocate connection tx issuer period token

                                match InvoiceNumber.create issuer.LegalEntity issuer.Series period sequence with
                                | Error _ -> return! failed (ReasonCode.ofLiteral "invoice-number-invalid")
                                | Ok number ->
                                    do! insertSnapshot connection tx issuer record request number issuedAt token
                                    return! issued number
                    })
                ct
