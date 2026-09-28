namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Orders
open Npgsql

[<RequireQualifiedAccess>]
module StockReservations =
    /// Locks all involved product rows in canonical UUID order, then validates and reserves
    /// the complete order or none of it. Call only inside the caller's transaction.
    let private reserveRows
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (orderId: string)
        (generation: int64)
        (expiresAt: DateTimeOffset)
        (sourceMachine: string)
        (sourceCommand: int64)
        (sourceOrdinal: int)
        (lines: OrderLine list)
        (ct: CancellationToken)
        : Task<Result<Guid list, ReservationFailure>> =
        task {
            if
                lines.IsEmpty
                || (lines
                    |> List.map (fun line -> App.Domain.ProductId.value line.ProductId)
                    |> List.distinct)
                    .Length
                   <> lines.Length
            then
                return Error ReservationFailure.InvalidReservation
            else
                let products =
                    lines
                    |> List.map (fun line -> App.Domain.ProductId.value line.ProductId)
                    |> List.distinct
                    |> List.sort

                use lockRows =
                    new NpgsqlCommand(
                        "SELECT product_id FROM fsnix.product_stock WHERE product_id = ANY(@ids) ORDER BY product_id FOR UPDATE",
                        connection,
                        transaction
                    )

                lockRows.Parameters.AddWithValue("ids", products |> List.toArray) |> ignore
                let! reader = lockRows.ExecuteReaderAsync(ct)
                let locked = ResizeArray<Guid>()

                while! reader.ReadAsync(ct) do
                    locked.Add(reader.GetGuid 0)

                reader.Dispose()

                if locked.Count <> products.Length then
                    return Error ReservationFailure.InvalidReservation
                else
                    let mutable failure = None

                    for line in lines do
                        use check =
                            new NpgsqlCommand(
                                """SELECT p.active, p.price_version, s.on_hand - s.reserved
                                                           FROM fsnix.products p JOIN fsnix.product_stock s USING(product_id)
                                                           WHERE p.product_id = @id""",
                                connection,
                                transaction
                            )

                        check.Parameters.AddWithValue("id", App.Domain.ProductId.value line.ProductId)
                        |> ignore

                        let! checkReader = check.ExecuteReaderAsync(ct)

                        if not (checkReader.Read()) then
                            failure <- Some ReservationFailure.InvalidReservation
                        else
                            let active = checkReader.GetBoolean 0
                            let version = checkReader.GetInt64 1
                            let available = checkReader.GetInt32 2

                            if not active then
                                failure <- Some ReservationFailure.ProductInactive
                            elif version <> App.Domain.PriceVersion.value line.PriceVersion then
                                failure <- Some ReservationFailure.PriceVersionMismatch
                            elif available < App.Domain.Quantity.value line.Quantity then
                                failure <- Some ReservationFailure.InsufficientStock

                        checkReader.Dispose()

                    match failure with
                    | Some reason -> return Error reason
                    | None ->
                        let ids = ResizeArray<Guid>()

                        for line in lines do
                            let reservationId = Guid.NewGuid()

                            use insert =
                                new NpgsqlCommand(
                                    """INSERT INTO fsnix.stock_reservations
                                (reservation_id, order_id, order_line_id, product_id, quantity, expires_at,
                                 source_machine_id, source_command_id, source_ordinal)
                                VALUES (@reservation, @order, @line, @product, @quantity, @expiry, @machine, @command, @ordinal)""",
                                    connection,
                                    transaction
                                )

                            insert.Parameters.AddWithValue("reservation", reservationId) |> ignore
                            insert.Parameters.AddWithValue("order", orderId) |> ignore

                            insert.Parameters.AddWithValue("line", App.Domain.OrderLineId.value line.LineId)
                            |> ignore

                            insert.Parameters.AddWithValue("product", App.Domain.ProductId.value line.ProductId)
                            |> ignore

                            insert.Parameters.AddWithValue("quantity", App.Domain.Quantity.value line.Quantity)
                            |> ignore

                            insert.Parameters.AddWithValue("expiry", expiresAt) |> ignore
                            insert.Parameters.AddWithValue("machine", sourceMachine) |> ignore
                            insert.Parameters.AddWithValue("command", sourceCommand) |> ignore
                            insert.Parameters.AddWithValue("ordinal", sourceOrdinal) |> ignore
                            let! _ = insert.ExecuteNonQueryAsync(ct)

                            use update =
                                new NpgsqlCommand(
                                    "UPDATE fsnix.product_stock SET reserved = reserved + @quantity, updated_at = statement_timestamp() WHERE product_id = @product",
                                    connection,
                                    transaction
                                )

                            update.Parameters.AddWithValue("quantity", App.Domain.Quantity.value line.Quantity)
                            |> ignore

                            update.Parameters.AddWithValue("product", App.Domain.ProductId.value line.ProductId)
                            |> ignore

                            let! _ = update.ExecuteNonQueryAsync(ct)
                            ids.Add reservationId

                        return Ok(List.ofSeq ids)
        }

    let reserve connection transaction orderId generation expiresAt sourceMachine sourceCommand sourceOrdinal lines ct =
        task {
            use insert =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.order_reservation_controls(order_id,generation,status) VALUES(@order,@generation,'open') ON CONFLICT(order_id) DO NOTHING",
                    connection,
                    transaction
                )

            insert.Parameters.AddWithValue("order", orderId) |> ignore
            insert.Parameters.AddWithValue("generation", generation) |> ignore
            let! _ = insert.ExecuteNonQueryAsync ct

            use check =
                new NpgsqlCommand(
                    "SELECT status,generation FROM fsnix.order_reservation_controls WHERE order_id=@order FOR UPDATE",
                    connection,
                    transaction
                )

            check.Parameters.AddWithValue("order", orderId) |> ignore
            let! reader = check.ExecuteReaderAsync ct

            let allowed =
                reader.Read() && reader.GetString(0) = "open" && reader.GetInt64(1) = generation

            reader.Dispose()

            if not allowed then
                return Error ReservationFailure.InvalidReservation
            else
                return!
                    reserveRows
                        connection
                        transaction
                        orderId
                        generation
                        expiresAt
                        sourceMachine
                        sourceCommand
                        sourceOrdinal
                        lines
                        ct
        }

    /// <summary>Commits every currently reserved ledger row for the order, moving quantities
    /// out of both the reserved counter and the on-hand balance exactly once. Product rows are
    /// locked in canonical order first, mirroring reserve. Returns the number of committed
    /// ledger rows; zero means there was nothing left to commit.</summary>
    let commit
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (orderId: string)
        (ct: CancellationToken)
        : Task<int> =
        task {
            use products =
                new NpgsqlCommand(
                    "SELECT DISTINCT product_id FROM fsnix.stock_reservations WHERE order_id=@order AND status='reserved'",
                    connection,
                    transaction
                )

            products.Parameters.AddWithValue("order", orderId) |> ignore
            let! reader = products.ExecuteReaderAsync ct
            let ids = ResizeArray<Guid>()

            while! reader.ReadAsync ct do
                ids.Add(reader.GetGuid 0)

            reader.Dispose()

            if ids.Count = 0 then
                return 0
            else
                use lockRows =
                    new NpgsqlCommand(
                        "SELECT product_id FROM fsnix.product_stock WHERE product_id = ANY(@ids) ORDER BY product_id FOR UPDATE",
                        connection,
                        transaction
                    )

                lockRows.Parameters.AddWithValue("ids", ids.ToArray()) |> ignore
                let! lockReader = lockRows.ExecuteReaderAsync ct

                while! lockReader.ReadAsync ct do
                    ()

                lockReader.Dispose()

                use ledger =
                    new NpgsqlCommand(
                        "UPDATE fsnix.stock_reservations SET status='committed', settled_at=statement_timestamp() WHERE order_id=@order AND status='reserved' RETURNING product_id, quantity",
                        connection,
                        transaction
                    )

                ledger.Parameters.AddWithValue("order", orderId) |> ignore
                let! ledgerReader = ledger.ExecuteReaderAsync ct
                let totals = System.Collections.Generic.Dictionary<Guid, int>()
                let mutable committed = 0

                while! ledgerReader.ReadAsync ct do
                    let product = ledgerReader.GetGuid 0
                    let quantity = ledgerReader.GetInt32 1
                    committed <- committed + 1

                    totals[product] <-
                        (match totals.TryGetValue product with
                         | true, existing -> existing + quantity
                         | false, _ -> quantity)

                ledgerReader.Dispose()

                for KeyValue(product, quantity) in totals do
                    use update =
                        new NpgsqlCommand(
                            "UPDATE fsnix.product_stock SET on_hand = on_hand - @quantity, reserved = reserved - @quantity, updated_at = statement_timestamp() WHERE product_id = @product",
                            connection,
                            transaction
                        )

                    update.Parameters.AddWithValue("quantity", quantity) |> ignore
                    update.Parameters.AddWithValue("product", product) |> ignore
                    let! _ = update.ExecuteNonQueryAsync ct
                    ()

                return committed
        }

    let release
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (orderId: string)
        (ct: CancellationToken)
        : Task =
        task {
            use fence =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.order_reservation_controls(order_id,generation,status) VALUES(@order,1,'cancelled') ON CONFLICT(order_id) DO UPDATE SET status='cancelled',updated_at=statement_timestamp()",
                    connection,
                    transaction
                )

            fence.Parameters.AddWithValue("order", orderId) |> ignore
            let! _ = fence.ExecuteNonQueryAsync ct

            use command =
                new NpgsqlCommand(
                    """WITH released AS (
                UPDATE fsnix.stock_reservations SET status = 'released', settled_at = statement_timestamp()
                WHERE order_id = @order AND status = 'reserved' RETURNING product_id, quantity)
                UPDATE fsnix.product_stock s SET reserved = s.reserved - released.quantity, updated_at = statement_timestamp()
                FROM released WHERE s.product_id = released.product_id""",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("order", orderId) |> ignore
            let! _ = command.ExecuteNonQueryAsync(ct)

            use intent =
                new NpgsqlCommand(
                    "UPDATE fsnix.authorization_intents SET status='cancelled' WHERE order_id=@order AND status IN ('pending','claimed')",
                    connection,
                    transaction
                )

            intent.Parameters.AddWithValue("order", orderId) |> ignore
            let! _ = intent.ExecuteNonQueryAsync ct
            ()
        }
