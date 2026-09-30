SELECT
    invoice_id,
    currency
FROM
    fsnix.invoices
WHERE
    order_id = @order
    AND kind = 'invoice'
