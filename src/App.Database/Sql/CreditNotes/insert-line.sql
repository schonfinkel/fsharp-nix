INSERT INTO fsnix.invoice_lines (invoice_id, line_number, order_line_id, product_id, sku, description, unit_price, quantity, line_amount, currency)
    VALUES (@invoice, @line_number, @order_line, @product, @sku, @description, @unit_price, @quantity, @line_amount, @currency)
