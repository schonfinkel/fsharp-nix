INSERT INTO fsnix.return_lines (return_id, order_line_id, quantity, refunded_amount, refunded_tax, currency)
    VALUES (@return, @line, @quantity, @amount, @tax, @currency)
ON CONFLICT (return_id, order_line_id)
    DO NOTHING
