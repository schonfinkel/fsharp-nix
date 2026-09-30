-- The last four columns name the invoice a credit note credits (NULL for an invoice).
SELECT
    i.legal_entity,
    i.series,
    i.fiscal_period,
    i.number,
    i.issued_at,
    i.seller ->> 'name',
    i.seller ->> 'address',
    COALESCE(i.seller ->> 'taxId', ''),
    i.buyer ->> 'email',
    i.billing_address::text,
    i.subtotal_amount,
    i.shipping_amount,
    i.tax_amount,
    i.total_amount,
    i.currency,
    o.legal_entity,
    o.series,
    o.fiscal_period,
    o.number
FROM
    fsnix.invoices i
    LEFT JOIN fsnix.invoices o ON o.invoice_id = i.credits_invoice_id
WHERE
    i.invoice_id = @invoice
