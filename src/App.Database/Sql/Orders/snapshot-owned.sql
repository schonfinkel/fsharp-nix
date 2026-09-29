SELECT
    EXISTS (
        SELECT
            1
        FROM
            fsnix.order_snapshots
        WHERE
            order_id = @order_id
            AND customer_id = @customer_id)
