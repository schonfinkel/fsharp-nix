SELECT
    sku,
    description,
    quantity,
    unit_price,
    line_amount
FROM
    fsnix.invoice_lines
WHERE
    invoice_id = @invoice
ORDER BY
    line_number
