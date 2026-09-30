UPDATE
    fsnix.product_stock
SET
    on_hand = on_hand + @quantity
FROM
    fsnix.stock_reservations r
WHERE
    r.order_line_id = @line
    AND r.product_id = fsnix.product_stock.product_id
    AND r.status = 'committed'
