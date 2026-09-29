INSERT INTO fsnix.stock_reservations (reservation_id, order_id, order_line_id, product_id, quantity, expires_at, source_machine_id, source_command_id, source_ordinal)
    VALUES (@reservation, @order, @line, @product, @quantity, @expiry, @machine, @command, @ordinal)
