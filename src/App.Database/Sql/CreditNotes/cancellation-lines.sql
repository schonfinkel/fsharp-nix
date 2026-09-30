-- The shipment's allocated lines, priced as invoiced.
SELECT
    il.order_line_id,
    il.product_id,
    il.sku,
    il.description,
    il.unit_price,
    (l.line ->> 'quantity')::int
FROM
    fsnix.shipments s
    CROSS JOIN LATERAL JSONB_ARRAY_ELEMENTS(s.lines) AS l (line)
    JOIN fsnix.invoice_lines il ON il.invoice_id = @original
        AND il.order_line_id = (l.line ->> 'lineId')::uuid
WHERE
    s.capture_id = @refund
    AND s.order_id = @order
ORDER BY
    il.line_number
