UPDATE
    fsnix.cart_merge_snapshots
SET
    status = 'applied',
    applied_at = STATEMENT_TIMESTAMP()
WHERE
    merge_id = @merge_id
    AND status = 'captured'
