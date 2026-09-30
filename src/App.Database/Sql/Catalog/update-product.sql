UPDATE
    fsnix.products
SET
    name = @name,
    description = @description,
    price_amount = @price_amount,
    price_currency = @price_currency,
    price_version = price_version + 1,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    product_id = @product_id
