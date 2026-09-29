INSERT INTO fsnix.cart_merge_snapshots (merge_id, source_cart_id, target_customer_id, lines, status)
    VALUES (@merge_id, @source_cart_id, @target_customer_id, @lines::jsonb, 'captured')
ON CONFLICT (merge_id)
    DO NOTHING
RETURNING
    merge_id
