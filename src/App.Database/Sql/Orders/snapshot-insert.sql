INSERT INTO fsnix.order_snapshots (snapshot_id, order_id, customer_id, customer_email, address, lines, subtotal_amount, shipping_amount, tax_amount, total_amount, currency)
    VALUES (@snapshot_id, @order_id, @customer_id, @email, @address::jsonb, @lines::jsonb, @subtotal, @shipping, @tax, @total, @currency)
ON CONFLICT (snapshot_id)
    DO NOTHING
RETURNING
    snapshot_id
