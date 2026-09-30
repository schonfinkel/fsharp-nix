SELECT
    merge_id,
    source_cart_id,
    target_customer_id,
    lines::text,
    status
FROM
    fsnix.cart_merge_snapshots
WHERE
    merge_id = @merge_id
