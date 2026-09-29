SELECT
    EXISTS (
        SELECT
            1
        FROM
            fsnix.order_snapshots
        WHERE
            snapshot_id = @snapshot_id
            AND order_id = @order_id
            AND customer_id = @customer_id
            AND customer_email = @email
            AND address = @address::jsonb
            AND lines = @lines::jsonb
            AND subtotal_amount = @subtotal
            AND shipping_amount = @shipping
            AND tax_amount = @tax
            AND total_amount = @total
            AND currency = @currency)
