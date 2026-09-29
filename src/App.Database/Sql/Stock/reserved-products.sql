SELECT DISTINCT
    product_id
FROM
    fsnix.stock_reservations
WHERE
    order_id = @order
    AND status = 'reserved'
