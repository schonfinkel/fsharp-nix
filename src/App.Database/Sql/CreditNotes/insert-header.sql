-- Seller, buyer and billing address are the original invoice's, never the live order's.
INSERT INTO fsnix.invoices (invoice_id, order_id, snapshot_id, customer_id, legal_entity, series, fiscal_period, number, issued_at, seller, buyer, billing_address, subtotal_amount, shipping_amount, tax_amount, total_amount, currency, source_machine_id, source_command_id, source_ordinal, kind, credits_invoice_id, refund_id)
SELECT
    @invoice,
    o.order_id,
    o.snapshot_id,
    o.customer_id,
    @entity,
    @series,
    @period,
    @number,
    @issued_at,
    o.seller,
    o.buyer,
    o.billing_address,
    @subtotal,
    @shipping,
    @tax,
    @total,
    o.currency,
    @machine,
    @command,
    @ordinal,
    'credit-note',
    o.invoice_id,
    @refund
FROM
    fsnix.invoices o
WHERE
    o.invoice_id = @original
