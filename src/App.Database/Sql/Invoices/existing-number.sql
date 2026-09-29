SELECT
    order_id,
    legal_entity,
    series,
    fiscal_period,
    number
FROM
    fsnix.invoices
WHERE
    invoice_id = @invoice
