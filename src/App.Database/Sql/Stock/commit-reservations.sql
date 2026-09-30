UPDATE
    fsnix.stock_reservations
SET
    status = 'committed',
    settled_at = STATEMENT_TIMESTAMP()
WHERE
    order_id = @order
    AND status = 'reserved'
RETURNING
    product_id,
    quantity
