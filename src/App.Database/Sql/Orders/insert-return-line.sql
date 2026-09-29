INSERT INTO fsnix.return_lines (return_id, order_line_id, quantity, refunded_amount, currency)
    VALUES (@return, @line, @quantity, @amount, @currency)
ON CONFLICT (return_id, order_line_id)
    DO NOTHING
