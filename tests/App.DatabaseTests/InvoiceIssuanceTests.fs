namespace App.DatabaseTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open App.Database
open App.Domain
open App.Invoices
open App.Tests
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

[<RequireQualifiedAccess>]
module InvoiceIssuanceTests =
    let private issuer =
        { LegalEntity = "FSNIX"
          Series = "INV"
          Name = "fsnix Store"
          Address = "1 Example Street"
          TaxId = "TAX-1" }

    let private issuedAt = DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)

    let private line (amount: string) quantity =
        {| lineId = Guid.NewGuid().ToString("D")
           productId = "00000000-0000-0000-0000-000000000001"
           sku = "INV-ITEM"
           name = "Invoice item"
           amount = amount
           currency = "USD"
           quantity = quantity
           priceVersion = 1L |}

    /// <summary>Inserts an order snapshot whose header is consistent with its lines.</summary>
    let private insertSnapshot (dataSource: NpgsqlDataSource) lines subtotal =
        task {
            let snapshotId = Guid.NewGuid()
            let shipping = 5m
            let tax = 2m

            let row =
                { SnapshotId = snapshotId
                  OrderId = $"order:{snapshotId:D}"
                  CustomerId = Guid.NewGuid()
                  CustomerEmail = "buyer@example.test"
                  AddressJson =
                    """{"recipient":"Buyer","line1":"2 Road","city":"Town","postalCode":"12345","countryCode":"US"}"""
                  LinesJson = JsonSerializer.Serialize lines
                  Subtotal = subtotal
                  Shipping = shipping
                  Tax = tax
                  Total = subtotal + shipping + tax
                  Currency = "USD" }

            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()
            use! tx = connection.BeginTransactionAsync()
            let! inserted = OrderSnapshots.insert connection tx row CancellationToken.None
            Assert.Equal(Ok(), inserted)
            do! tx.CommitAsync()

            return
                OrderSnapshotId.create snapshotId
                |> Result.defaultWith Assert.Fail
                |> InvoiceRequest.forOrder
        }

    let private record commandId (request: InvoiceRequest) =
        { MachineId = machineId Invoices.MachineKey
          EntityId = Invoices.invoiceEntityId request.InvoiceId
          CommandId = CommandId.ofInt64 commandId
          Epoch = Epoch.ofUInt64 1UL
          Ordinal = 0
          Action = IssueSnapshot request }

    let private issue dataSource commandId request =
        InvoiceEffects.applyIssue dataSource issuer issuedAt (record commandId request) CancellationToken.None

    let private scalar (dataSource: NpgsqlDataSource) (sql: string) (request: InvoiceRequest option) =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)

            request
            |> Option.iter (fun request ->
                command.Parameters.AddWithValue("invoice", InvoiceId.value request.InvoiceId)
                |> ignore)

            let! value = command.ExecuteScalarAsync()
            return value
        }

    let private numberOf dataSource request =
        task {
            let! value = scalar dataSource "SELECT number FROM fsnix.invoices WHERE invoice_id=@invoice" (Some request)
            return value :?> int64
        }

    let private counter dataSource =
        task {
            let! value =
                scalar
                    dataSource
                    "SELECT coalesce((SELECT last_number FROM fsnix.invoice_counters WHERE legal_entity='FSNIX' AND series='INV' AND fiscal_period='2026'), 0)"
                    None

            return value :?> int64
        }

    /// <summary>The callback events queued for an invoice entity, oldest first.</summary>
    let private callbacks (dataSource: NpgsqlDataSource) (request: InvoiceRequest) =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    "SELECT event::text FROM fsnix.integration_outbox WHERE machine_id='invoices' AND entity_id=@entity ORDER BY callback_key",
                    connection
                )

            command.Parameters.AddWithValue("entity", EntityId.value (Invoices.invoiceEntityId request.InvoiceId))
            |> ignore

            use! reader = command.ExecuteReaderAsync()
            let events = Collections.Generic.List<InvoiceEvent>()

            while! reader.ReadAsync() do
                events.Add(
                    InvoiceCodec.event.Decode(reader.GetString 0)
                    |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")
                )

            return List.ofSeq events
        }

    let ``issuance allocates gapless numbers and is idempotent under redelivery`` (fixture: PostgreSqlFixture) =
        task {
            use dataSource = AutomataStore.createDataSource fixture.ConnectionString
            let! first = insertSnapshot dataSource [ line "10.00" 2 ] 20m
            let! second = insertSnapshot dataSource [ line "7.5" 1; line "2.5" 2 ] 12.5m

            let! issuedFirst = issue dataSource 1L first
            let! issuedSecond = issue dataSource 2L second
            Assert.Equal(Ok(), issuedFirst)
            Assert.Equal(Ok(), issuedSecond)

            let! firstNumber = numberOf dataSource first
            let! secondNumber = numberOf dataSource second
            Assert.Equal(1L, firstNumber)
            Assert.Equal(2L, secondNumber)

            // Redelivery of the same action is a receipt duplicate: nothing new commits.
            let! redelivered = issue dataSource 1L first
            Assert.Equal(Ok(), redelivered)

            // A different command for the same invoice resolves by business key, never renumbers.
            let! reissued = issue dataSource 99L first
            Assert.Equal(Ok(), reissued)

            let! last = counter dataSource
            Assert.Equal(2L, last)

            let! events = callbacks dataSource first

            let expected =
                InvoiceNumber.create "FSNIX" "INV" "2026" 1L |> Result.defaultWith Assert.Fail

            Assert.Equal<InvoiceEvent list>(
                [ SnapshotIssued(first.InvoiceId, expected)
                  SnapshotIssued(first.InvoiceId, expected) ],
                events
            )

            let! lines =
                scalar
                    dataSource
                    "SELECT string_agg(line_number || ':' || quantity || ':' || line_amount::text, ',' ORDER BY line_number) FROM fsnix.invoice_lines WHERE invoice_id=@invoice"
                    (Some second)

            Assert.Equal("1:1:7.50000000,2:2:5.00000000", lines :?> string)

            let! seller =
                scalar dataSource "SELECT seller->>'taxId' FROM fsnix.invoices WHERE invoice_id=@invoice" (Some first)

            Assert.Equal("TAX-1", seller :?> string)
        }

    let ``failed issuance never consumes a number`` (fixture: PostgreSqlFixture) =
        task {
            use dataSource = AutomataStore.createDataSource fixture.ConnectionString

            // Missing snapshot: reported as a business failure before any allocation.
            let missing =
                OrderSnapshotId.create (Guid.NewGuid())
                |> Result.defaultWith Assert.Fail
                |> InvoiceRequest.forOrder

            let! reported = issue dataSource 1L missing
            Assert.Equal(Ok(), reported)
            let! events = callbacks dataSource missing

            Assert.Equal<InvoiceEvent list>(
                [ IssuanceFailed(missing.InvoiceId, ReasonCode.ofLiteral "snapshot-missing") ],
                events
            )

            // A line the immutable table rejects fails after the counter increment; the whole
            // transaction, counter included, rolls back.
            let! broken = insertSnapshot dataSource [ line "30" 1; line "-10" 1 ] 20m
            let! crashed = Assert.ThrowsAsync<PostgresException>(fun () -> issue dataSource 2L broken :> Task)
            Assert.Equal("23514", crashed.SqlState)

            let! afterCrash = counter dataSource
            Assert.Equal(0L, afterCrash)

            let! receipts =
                scalar dataSource "SELECT count(*) FROM fsnix.action_receipts WHERE machine_id='invoices'" None

            Assert.Equal(1L, receipts :?> int64)

            let! valid = insertSnapshot dataSource [ line "4" 1 ] 4m
            let! issued = issue dataSource 3L valid
            Assert.Equal(Ok(), issued)
            let! number = numberOf dataSource valid
            Assert.Equal(1L, number)
        }

    let ``concurrent issuers share one gapless sequence`` (fixture: PostgreSqlFixture) =
        task {
            use dataSource = AutomataStore.createDataSource fixture.ConnectionString
            let count = 12

            let! requests =
                [ 1..count ]
                |> List.map (fun _ -> insertSnapshot dataSource [ line "1" 1 ] 1m)
                |> Task.WhenAll

            let! results =
                requests
                |> Array.mapi (fun index request ->
                    Task.Run<Result<unit, InvoiceActionError>>(fun () -> issue dataSource (int64 (index + 1)) request))
                |> Task.WhenAll

            for result in results do
                Assert.Equal(Ok(), result)

            let! numbers =
                scalar dataSource "SELECT string_agg(number::text, ',' ORDER BY number) FROM fsnix.invoices" None

            Assert.Equal(String.Join(",", [ 1..count ]), numbers :?> string)
        }

    let ``invoice rows are insert-only and numbers must be allocated`` (fixture: PostgreSqlFixture) =
        task {
            use dataSource = AutomataStore.createDataSource fixture.ConnectionString
            let! request = insertSnapshot dataSource [ line "3" 1 ] 3m
            let! issued = issue dataSource 1L request
            Assert.Equal(Ok(), issued)

            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()

            let expectRejected (sql: string) =
                task {
                    use command = new NpgsqlCommand(sql, connection)

                    command.Parameters.AddWithValue("invoice", InvoiceId.value request.InvoiceId)
                    |> ignore

                    let! error = Assert.ThrowsAsync<PostgresException>(fun () -> command.ExecuteNonQueryAsync() :> Task)
                    return error.MessageText
                }

            let! update = expectRejected "UPDATE fsnix.invoices SET total_amount=0 WHERE invoice_id=@invoice"
            Assert.Equal("invoices is insert-only", update)
            let! delete = expectRejected "DELETE FROM fsnix.invoice_lines WHERE invoice_id=@invoice"
            Assert.Equal("invoice_lines is insert-only", delete)
            let! truncate = expectRejected "TRUNCATE fsnix.invoice_lines"
            Assert.Equal("invoice_lines is insert-only", truncate)

            // A hand-picked number that the counter never allocated is refused, even when unused.
            let! other = insertSnapshot dataSource [ line "3" 1 ] 3m

            let! forged =
                expectRejected (
                    $"""INSERT INTO fsnix.invoices
                           (invoice_id, order_id, snapshot_id, legal_entity, series, fiscal_period, number, issued_at,
                            seller, buyer, billing_address, subtotal_amount, shipping_amount, tax_amount, total_amount,
                            currency, source_machine_id, source_command_id, source_ordinal)
                       VALUES ('{InvoiceId.wireString other.InvoiceId}', '{other.OrderId}', '{InvoiceId.wireString other.InvoiceId}',
                               'FSNIX', 'INV', '2026', 5, now(), '{{}}', '{{}}', '{{}}', 3, 5, 2, 10, 'USD', 'manual', 1, 0)"""
                )

            Assert.Equal("invoice number 5 was not allocated by its scope counter", forged)
        }
