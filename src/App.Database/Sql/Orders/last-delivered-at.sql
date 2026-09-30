SELECT
    MAX(delivered_at)
FROM
    fsnix.shipments
WHERE
    order_id = @order
