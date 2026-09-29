INSERT INTO fsnix.invoices (invoice_id, order_id, snapshot_id, customer_id, legal_entity, series, fiscal_period, number, issued_at, seller, buyer, billing_address, subtotal_amount, shipping_amount, tax_amount, total_amount, currency, source_machine_id, source_command_id, source_ordinal)
SELECT
    @invoice,
    s.order_id,
    s.snapshot_id,
    s.customer_id,
    @entity,
    @series,
    @period,
    @number,
    @issued_at,
    JSONB_BUILD_OBJECT('legalEntity', @entity, 'name', @seller_name, 'address', @seller_address, 'taxId', @seller_tax_id),
    JSONB_BUILD_OBJECT('customerId', s.customer_id, 'email', s.customer_email),
    s.address,
    s.subtotal_amount,
    s.shipping_amount,
    s.tax_amount,
    s.total_amount,
    s.currency,
    @machine,
    @command,
    @ordinal
FROM
    fsnix.order_snapshots s
WHERE
    s.snapshot_id = @snapshot;

INSERT INTO fsnix.invoice_lines (invoice_id, line_number, order_line_id, product_id, sku, description, unit_price, quantity, line_amount, currency)
SELECT
    @invoice,
    l.n::int,
    (l.line ->> 'lineId')::uuid,
    (l.line ->> 'productId')::uuid,
    l.line ->> 'sku',
    l.line ->> 'name',
    (l.line ->> 'amount')::numeric,
    (l.line ->> 'quantity')::int,
    (l.line ->> 'amount')::numeric * (l.line ->> 'quantity')::int,
    l.line ->> 'currency'
FROM
    fsnix.order_snapshots s,
    JSONB_ARRAY_ELEMENTS(s.lines)
    WITH ORDINALITY AS l (line, n)
WHERE
    s.snapshot_id = @snapshot
