WITH released AS (
    UPDATE
        fsnix.stock_reservations
    SET
        status = 'released',
        settled_at = STATEMENT_TIMESTAMP()
    WHERE
        order_id = @order
        AND status = 'reserved'
    RETURNING
        product_id,
        quantity)
UPDATE
    fsnix.product_stock s
SET
    reserved = s.reserved - released.quantity,
    updated_at = STATEMENT_TIMESTAMP()
FROM
    released
WHERE
    s.product_id = released.product_id
