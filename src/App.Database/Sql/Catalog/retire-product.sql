UPDATE
    fsnix.products
SET
    active = FALSE,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    product_id = @product_id
