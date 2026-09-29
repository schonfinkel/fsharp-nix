SELECT
    STATEMENT_TIMESTAMP();

SELECT
    COUNT(*)::bigint,
    COUNT(*) FILTER (WHERE status = 'pending')::bigint,
    COUNT(*) FILTER (WHERE status = 'pending'
        AND available_at <= STATEMENT_TIMESTAMP()
        AND (lease_until IS NULL
        OR lease_until < STATEMENT_TIMESTAMP()))::bigint,
COUNT(*) FILTER (WHERE status = 'pending'
    AND lease_until >= STATEMENT_TIMESTAMP())::bigint,
COUNT(*) FILTER (WHERE status = 'sent')::bigint,
COUNT(*) FILTER (WHERE status = 'dead')::bigint,
MIN(created_at) FILTER (WHERE status = 'pending'),
MIN(available_at) FILTER (WHERE status = 'pending'),
MAX(attempts) FILTER (WHERE status = 'pending')
FROM
    fsnix.integration_outbox;

SELECT
    COUNT(*)::bigint,
    COUNT(*) FILTER (WHERE status = 'pending')::bigint,
    COUNT(*) FILTER (WHERE status = 'pending'
        AND available_at <= STATEMENT_TIMESTAMP()
        AND (lease_until IS NULL
        OR lease_until < STATEMENT_TIMESTAMP()))::bigint,
COUNT(*) FILTER (WHERE status = 'pending'
    AND lease_until >= STATEMENT_TIMESTAMP())::bigint,
COUNT(*) FILTER (WHERE status = 'sent')::bigint,
COUNT(*) FILTER (WHERE status = 'dead')::bigint,
MIN(created_at) FILTER (WHERE status = 'pending'),
MIN(available_at) FILTER (WHERE status = 'pending'),
MAX(attempts) FILTER (WHERE status = 'pending')
FROM
    fsnix.account_email_outbox;

SELECT
    flow_kind,
    status,
    COUNT(*)::bigint,
    COUNT(*) FILTER (WHERE status = 'requested'
        AND expires_at <= STATEMENT_TIMESTAMP())::bigint,
    MIN(created_at),
    MIN(expires_at) FILTER (WHERE status = 'requested'),
    MAX(updated_at)
FROM
    fsnix.account_flow_requests
GROUP BY
    flow_kind,
    status
ORDER BY
    flow_kind,
    status;

-- Every scanner-owned due-work ledger, grouped by kind and status.
WITH ledgers (
    kind,
    status,
    due_at,
    lease_until,
    fired_at
) AS (
    SELECT
        timer_kind,
        status,
        deadline,
        lease_until,
        fired_at
    FROM
        fsnix.flow_deadlines
    UNION ALL
    SELECT
        timer_kind,
        status,
        deadline,
        lease_until,
        fired_at
    FROM
        fsnix.cart_deadlines
    UNION ALL
    SELECT
        'reservation-expiry',
        status,
        deadline,
        lease_until,
        fired_at
    FROM
        fsnix.reservation_deadlines
    UNION ALL
    SELECT
        'authorization-expiry',
        status,
        deadline,
        lease_until,
        fired_at
    FROM
        fsnix.payment_deadlines
    UNION ALL
    SELECT
        'return-window',
        status,
        window_ends_at,
        lease_until,
        NULL::timestamptz
    FROM
        fsnix.return_requests
    UNION ALL
    SELECT
        'invoice-render',
        status,
        due_at,
        lease_until,
        NULL::timestamptz
    FROM
        fsnix.invoice_render_checks
    UNION ALL
    SELECT
        'shipment-lost',
        status,
        lost_due_at,
        lease_until,
        fired_at
    FROM
        fsnix.shipment_tracking
)
SELECT
    kind,
    status,
    COUNT(*)::bigint,
    COUNT(*) FILTER (WHERE status = 'pending'
        AND due_at <= STATEMENT_TIMESTAMP())::bigint,
    COUNT(*) FILTER (WHERE status = 'pending'
        AND lease_until >= STATEMENT_TIMESTAMP())::bigint,
    MIN(due_at) FILTER (WHERE status = 'pending'),
    MAX(fired_at)
FROM
    ledgers
GROUP BY
    kind,
    status
ORDER BY
    kind,
    status;

-- Provider calls whose outcome is still unknown: due for a check, or parked after the checks ran out.
SELECT
    COUNT(*)::bigint,
    COUNT(*) FILTER (WHERE next_check_at <= STATEMENT_TIMESTAMP())::bigint,
    COUNT(*) FILTER (WHERE next_check_at IS NULL)::bigint,
    MAX(checks)
FROM
    fsnix.payment_operations
WHERE
    status = 'unknown';

