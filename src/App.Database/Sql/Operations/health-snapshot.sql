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

SELECT
    timer_kind,
    status,
    COUNT(*)::bigint,
    COUNT(*) FILTER (WHERE status = 'pending'
        AND deadline <= STATEMENT_TIMESTAMP())::bigint,
    COUNT(*) FILTER (WHERE status = 'pending'
        AND lease_until >= STATEMENT_TIMESTAMP())::bigint,
    MIN(deadline) FILTER (WHERE status = 'pending'),
    MAX(fired_at) FILTER (WHERE status = 'fired')
FROM
    fsnix.flow_deadlines
GROUP BY
    timer_kind,
    status
ORDER BY
    timer_kind,
    status;

