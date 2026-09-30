-- A return refund reverses its lines' merchandise plus the tax recorded per line; no shipping.
SELECT
    0::numeric,
    COALESCE(SUM(rl.refunded_tax), 0),
    MAX(rl.currency),
    COUNT(*)
FROM
    fsnix.return_lines rl
    JOIN fsnix.return_requests rr USING (return_id)
WHERE
    rl.return_id = @return
    AND rr.order_id = @order
