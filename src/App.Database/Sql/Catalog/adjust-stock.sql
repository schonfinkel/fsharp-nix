UPDATE
    fsnix.product_stock
SET
    on_hand = on_hand + @delta,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    product_id = @product_id
    AND on_hand + @delta >= 0
