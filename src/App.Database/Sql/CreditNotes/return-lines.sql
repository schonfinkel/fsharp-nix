SELECT
    il.order_line_id,
    il.product_id,
    il.sku,
    il.description,
    il.unit_price,
    rl.quantity
FROM
    fsnix.return_lines rl
    JOIN fsnix.return_requests rr USING (return_id)
    JOIN fsnix.invoice_lines il ON il.invoice_id = @original
        AND il.order_line_id = rl.order_line_id
WHERE
    rl.return_id = @return
    AND rr.order_id = @order
ORDER BY
    il.line_number
