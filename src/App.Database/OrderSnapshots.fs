namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

type OrderSnapshotRow =
    { SnapshotId: Guid
      OrderId: string
      CustomerId: Guid
      CustomerEmail: string
      AddressJson: string
      LinesJson: string
      Subtotal: decimal
      Shipping: decimal
      Tax: decimal
      Total: decimal
      Currency: string }

[<RequireQualifiedAccess>]
module OrderSnapshots =
    let insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (snapshot: OrderSnapshotRow)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.order_snapshots
                           (snapshot_id, order_id, customer_id, customer_email, address, lines,
                            subtotal_amount, shipping_amount, tax_amount, total_amount, currency)
                       VALUES (@snapshot_id, @order_id, @customer_id, @email, @address::jsonb, @lines::jsonb,
                               @subtotal, @shipping, @tax, @total, @currency)
                       ON CONFLICT (snapshot_id) DO NOTHING
                       RETURNING snapshot_id""",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("snapshot_id", snapshot.SnapshotId) |> ignore
            command.Parameters.AddWithValue("order_id", snapshot.OrderId) |> ignore
            command.Parameters.AddWithValue("customer_id", snapshot.CustomerId) |> ignore
            command.Parameters.AddWithValue("email", snapshot.CustomerEmail) |> ignore
            command.Parameters.AddWithValue("address", snapshot.AddressJson) |> ignore
            command.Parameters.AddWithValue("lines", snapshot.LinesJson) |> ignore
            command.Parameters.AddWithValue("subtotal", snapshot.Subtotal) |> ignore
            command.Parameters.AddWithValue("shipping", snapshot.Shipping) |> ignore
            command.Parameters.AddWithValue("tax", snapshot.Tax) |> ignore
            command.Parameters.AddWithValue("total", snapshot.Total) |> ignore
            command.Parameters.AddWithValue("currency", snapshot.Currency) |> ignore

            let! inserted = command.ExecuteScalarAsync(ct)

            if not (isNull inserted) then
                return Ok()
            else
                use verify =
                    new NpgsqlCommand(
                        """SELECT EXISTS (SELECT 1 FROM fsnix.order_snapshots
                           WHERE snapshot_id=@snapshot_id AND order_id=@order_id AND customer_id=@customer_id
                             AND customer_email=@email AND address=@address::jsonb AND lines=@lines::jsonb
                             AND subtotal_amount=@subtotal AND shipping_amount=@shipping AND tax_amount=@tax
                             AND total_amount=@total AND currency=@currency)""",
                        connection,
                        transaction
                    )

                verify.Parameters.AddWithValue("snapshot_id", snapshot.SnapshotId) |> ignore
                verify.Parameters.AddWithValue("order_id", snapshot.OrderId) |> ignore
                verify.Parameters.AddWithValue("customer_id", snapshot.CustomerId) |> ignore
                verify.Parameters.AddWithValue("email", snapshot.CustomerEmail) |> ignore
                verify.Parameters.AddWithValue("address", snapshot.AddressJson) |> ignore
                verify.Parameters.AddWithValue("lines", snapshot.LinesJson) |> ignore
                verify.Parameters.AddWithValue("subtotal", snapshot.Subtotal) |> ignore
                verify.Parameters.AddWithValue("shipping", snapshot.Shipping) |> ignore
                verify.Parameters.AddWithValue("tax", snapshot.Tax) |> ignore
                verify.Parameters.AddWithValue("total", snapshot.Total) |> ignore
                verify.Parameters.AddWithValue("currency", snapshot.Currency) |> ignore
                let! same = verify.ExecuteScalarAsync ct

                return
                    if same :?> bool then
                        Ok()
                    else
                        Error "The order idempotency key was reused with different checkout data."
        }

    let tryLoad
        (dataSource: NpgsqlDataSource)
        (snapshotId: Guid)
        (ct: CancellationToken)
        : Task<OrderSnapshotRow option> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """SELECT snapshot_id, order_id, customer_id, customer_email, address::text, lines::text,
                              subtotal_amount, shipping_amount, tax_amount, total_amount, currency
                       FROM fsnix.order_snapshots WHERE snapshot_id = @id""",
                    connection
                )

            command.Parameters.AddWithValue("id", snapshotId) |> ignore
            let! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            let row =
                if found then
                    Some
                        { SnapshotId = reader.GetGuid 0
                          OrderId = reader.GetString 1
                          CustomerId = reader.GetGuid 2
                          CustomerEmail = reader.GetString 3
                          AddressJson = reader.GetString 4
                          LinesJson = reader.GetString 5
                          Subtotal = reader.GetDecimal 6
                          Shipping = reader.GetDecimal 7
                          Tax = reader.GetDecimal 8
                          Total = reader.GetDecimal 9
                          Currency = reader.GetString 10 }
                else
                    None

            reader.Dispose()
            return row
        }

    let isOwnedBy (dataSource: NpgsqlDataSource) (orderId: string) (customerId: Guid) (ct: CancellationToken) =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct

            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM fsnix.order_snapshots WHERE order_id = @order_id AND customer_id = @customer_id)",
                    connection
                )

            command.Parameters.AddWithValue("order_id", orderId) |> ignore
            command.Parameters.AddWithValue("customer_id", customerId) |> ignore
            let! owned = command.ExecuteScalarAsync ct
            return owned :?> bool
        }
