SELECT
    i.invoice_id,
    i.legal_entity,
    i.series,
    i.fiscal_period,
    i.number
FROM
    fsnix.invoices i
WHERE
    i.order_id = @order
    AND i.customer_id = @customer
    AND i.kind = 'credit-note'
    AND EXISTS (
        SELECT
            1
        FROM
            fsnix.invoice_documents d
        WHERE
            d.invoice_id = i.invoice_id)
ORDER BY
    i.issued_at,
    i.invoice_id
