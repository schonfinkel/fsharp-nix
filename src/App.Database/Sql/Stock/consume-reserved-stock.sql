UPDATE
    fsnix.product_stock
SET
    on_hand = on_hand - @quantity,
    reserved = reserved - @quantity,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    product_id = @product
