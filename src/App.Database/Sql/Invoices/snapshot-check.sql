SELECT
    s.order_id,
    s.total_amount = s.subtotal_amount + s.shipping_amount + s.tax_amount,
    (
        SELECT
            COUNT(*)
        FROM
            JSONB_ARRAY_ELEMENTS(s.lines)),
    (
        SELECT
            BOOL_AND(l ->> 'currency' = s.currency
                AND (l ->> 'quantity')::int > 0)
        FROM
            JSONB_ARRAY_ELEMENTS(s.lines) l),
    (
        SELECT
            COALESCE(SUM((l ->> 'amount')::numeric * (l ->> 'quantity')::int), 0)
        FROM
            JSONB_ARRAY_ELEMENTS(s.lines) l) = s.subtotal_amount
FROM
    fsnix.order_snapshots s
WHERE
    s.snapshot_id = @snapshot
