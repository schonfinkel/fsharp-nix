-- Due provider calls with an unknown outcome. Returns what the scanner needs to route a
-- reconciliation event to the machine that owns the operation.
UPDATE
    fsnix.payment_operations AS target
SET
    lease_owner = @owner,
    lease_until = STATEMENT_TIMESTAMP() + (@lease_seconds * interval '1 second')
WHERE
    target.operation_id IN (
        SELECT
            candidate.operation_id
        FROM
            fsnix.payment_operations AS candidate
        WHERE
            candidate.status = 'unknown'
            AND candidate.next_check_at <= STATEMENT_TIMESTAMP()
            AND (candidate.lease_until IS NULL
                OR candidate.lease_until < STATEMENT_TIMESTAMP())
        ORDER BY
            candidate.next_check_at,
            candidate.operation_id
        LIMIT @batch
        FOR UPDATE
            OF candidate SKIP LOCKED)
RETURNING
    target.operation_id,
    target.reconcile_machine,
    target.reconcile_entity,
    target.checks
