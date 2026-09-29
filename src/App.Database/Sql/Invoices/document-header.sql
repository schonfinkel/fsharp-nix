SELECT
    legal_entity,
    series,
    fiscal_period,
    number,
    issued_at,
    seller ->> 'name',
    seller ->> 'address',
    COALESCE(seller ->> 'taxId', ''),
    buyer ->> 'email',
    billing_address::text,
    subtotal_amount,
    shipping_amount,
    tax_amount,
    total_amount,
    currency
FROM
    fsnix.invoices
WHERE
    invoice_id = @invoice
