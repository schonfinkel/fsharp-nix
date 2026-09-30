SELECT
    i.invoice_id,
    i.legal_entity,
    i.series,
    i.fiscal_period,
    i.number,
    i.issued_at,
    i.order_id,
    i.total_amount,
    i.currency,
    EXISTS (
        SELECT
            1
        FROM
            fsnix.invoice_documents d
        WHERE
            d.invoice_id = i.invoice_id), o.legal_entity, o.series, o.fiscal_period, o.number
FROM
    fsnix.invoices i
    LEFT JOIN fsnix.invoices o ON o.invoice_id = i.credits_invoice_id
WHERE (@customer IS NULL
    OR i.customer_id = @customer)
AND (@before IS NULL
    OR (i.issued_at,
        i.invoice_id) < (
        SELECT
            b.issued_at,
            b.invoice_id
        FROM
            fsnix.invoices b
        WHERE
            b.invoice_id = @before))
ORDER BY
    i.issued_at DESC,
    i.invoice_id DESC
LIMIT @limit
