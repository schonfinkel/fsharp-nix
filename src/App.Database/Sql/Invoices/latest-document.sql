SELECT
    i.legal_entity,
    i.series,
    i.fiscal_period,
    i.number,
    d.sha256,
    d.content
FROM
    fsnix.invoices i
    JOIN fsnix.invoice_documents d ON d.invoice_id = i.invoice_id
WHERE
    i.invoice_id = @invoice
    AND (@customer IS NULL
        OR i.customer_id = @customer)
ORDER BY
    d.created_at DESC,
    d.sha256
LIMIT 1
