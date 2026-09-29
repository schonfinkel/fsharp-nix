SELECT
    snapshot_id,
    order_id,
    customer_id,
    customer_email,
    address::text,
    lines::text,
    subtotal_amount,
    shipping_amount,
    tax_amount,
    total_amount,
    currency
FROM
    fsnix.order_snapshots
WHERE
    snapshot_id = @id
