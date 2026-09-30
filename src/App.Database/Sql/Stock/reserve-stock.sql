UPDATE
    fsnix.product_stock
SET
    reserved = reserved + @quantity,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    product_id = @product
