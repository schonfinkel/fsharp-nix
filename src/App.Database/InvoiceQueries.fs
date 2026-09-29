namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open Npgsql

type InvoiceListRow =
    { InvoiceId: InvoiceId
      Number: InvoiceNumber
      IssuedAt: DateTimeOffset
      OrderId: string
      Total: decimal
      Currency: string
      HasDocument: bool }

/// <summary>A keyset page, newest first. <c>Next</c> is the cursor for the following page.</summary>
type InvoicePage =
    { Rows: InvoiceListRow list
      Next: InvoiceId option }

type StoredInvoiceDocument =
    { Number: InvoiceNumber
      Digest: DocumentDigest
      Content: byte array }

/// <summary>Read side of the invoice tables. Customer-scoped queries filter on
/// <c>customer_id</c> in SQL, so another customer's invoice is indistinguishable from a missing
/// one.</summary>
[<RequireQualifiedAccess>]
module InvoiceQueries =
    [<Literal>]
    let MaxPageSize = 100

    let private number (reader: NpgsqlDataReader) offset =
        InvoiceNumber.create
            (reader.GetString offset)
            (reader.GetString(offset + 1))
            (reader.GetString(offset + 2))
            (reader.GetInt64(offset + 3))
        |> Result.defaultWith invalidOp

    let private addCustomer (command: NpgsqlCommand) (customerId: Guid option) =
        command.Parameters.Add(
            NpgsqlParameter(
                "customer",
                NpgsqlTypes.NpgsqlDbType.Uuid,
                Value = (customerId |> Option.map box |> Option.defaultValue DBNull.Value)
            )
        )
        |> ignore

    /// <summary>Newest first; <paramref name="before"/> is the last invoice of the previous page.
    /// <paramref name="customerId"/> <c>None</c> lists every customer's invoices (operators).</summary>
    let list
        (dataSource: NpgsqlDataSource)
        (customerId: Guid option)
        (before: InvoiceId option)
        (pageSize: int)
        (ct: CancellationToken)
        : Task<InvoicePage> =
        let size = Math.Clamp(pageSize, 1, MaxPageSize)

        Execution.executeRead dataSource ct (fun connection token ->
            task {
                use command = new NpgsqlCommand(InvoiceSql.list, connection)

                addCustomer command customerId

                command.Parameters.Add(
                    NpgsqlParameter(
                        "before",
                        NpgsqlTypes.NpgsqlDbType.Uuid,
                        Value =
                            (before
                             |> Option.map (InvoiceId.value >> box)
                             |> Option.defaultValue DBNull.Value)
                    )
                )
                |> ignore

                command.Parameters.AddWithValue("limit", size + 1) |> ignore
                use! reader = command.ExecuteReaderAsync(token: CancellationToken)
                let rows = Collections.Generic.List<InvoiceListRow>()

                while! reader.ReadAsync token do
                    rows.Add
                        { InvoiceId = InvoiceId.create (reader.GetGuid 0) |> Result.defaultWith invalidOp
                          Number = number reader 1
                          IssuedAt = reader.GetFieldValue<DateTimeOffset> 5
                          OrderId = reader.GetString 6
                          Total = reader.GetDecimal 7
                          Currency = reader.GetString 8
                          HasDocument = reader.GetBoolean 9 }

                let page = rows |> Seq.truncate size |> List.ofSeq

                return
                    { Rows = page
                      Next =
                        if rows.Count > size then
                            page |> List.tryLast |> Option.map _.InvoiceId
                        else
                            None }
            })

    /// <summary>The order's invoice, if issued and visible to <paramref name="customerId"/>.</summary>
    let tryForOrder (dataSource: NpgsqlDataSource) (orderId: string) (customerId: Guid) (ct: CancellationToken) =
        Execution.executeRead dataSource ct (fun connection token ->
            task {
                use command = new NpgsqlCommand(InvoiceSql.forOrder, connection)

                command.Parameters.AddWithValue("order", orderId) |> ignore
                command.Parameters.AddWithValue("customer", customerId) |> ignore
                use! reader = command.ExecuteReaderAsync(token: CancellationToken)
                let! found = reader.ReadAsync token

                if found then
                    return
                        Some(
                            InvoiceId.create (reader.GetGuid 0) |> Result.defaultWith invalidOp,
                            number reader 1,
                            reader.GetBoolean 5
                        )
                else
                    return None
            })

    /// <summary>The newest stored PDF of an invoice. <paramref name="customerId"/> <c>None</c>
    /// skips the ownership filter (operators).</summary>
    let tryDocument
        (dataSource: NpgsqlDataSource)
        (invoiceId: InvoiceId)
        (customerId: Guid option)
        (ct: CancellationToken)
        : Task<StoredInvoiceDocument option> =
        Execution.executeRead dataSource ct (fun connection token ->
            task {
                use command = new NpgsqlCommand(InvoiceSql.latestDocument, connection)

                command.Parameters.AddWithValue("invoice", InvoiceId.value invoiceId) |> ignore
                addCustomer command customerId
                use! reader = command.ExecuteReaderAsync(token: CancellationToken)
                let! found = reader.ReadAsync token

                if found then
                    return
                        Some
                            { Number = number reader 0
                              Digest =
                                DocumentDigest.ofBytes (reader.GetFieldValue<byte array> 4)
                                |> Result.defaultWith invalidOp
                              Content = reader.GetFieldValue<byte array> 5 }
                else
                    return None
            })

    /// <summary>Whether the invoice exists (and belongs to <paramref name="customerId"/>).</summary>
    let exists (dataSource: NpgsqlDataSource) (invoiceId: InvoiceId) (customerId: Guid option) (ct: CancellationToken) =
        Execution.executeRead dataSource ct (fun connection token ->
            task {
                use command = new NpgsqlCommand(InvoiceSql.exists, connection)

                command.Parameters.AddWithValue("invoice", InvoiceId.value invoiceId) |> ignore
                addCustomer command customerId
                let! value = command.ExecuteScalarAsync token
                return value :?> bool
            })
