UPDATE
    fsnix.payment_deadlines AS target
SET
    lease_owner = @owner,
    lease_until = STATEMENT_TIMESTAMP() + (@lease_seconds * interval '1 second')
WHERE
    target.deadline_id IN (
        SELECT
            candidate.deadline_id
        FROM
            fsnix.payment_deadlines AS candidate
        WHERE
            candidate.status = 'pending'
            AND candidate.deadline <= STATEMENT_TIMESTAMP()
            AND (candidate.lease_until IS NULL
                OR candidate.lease_until < STATEMENT_TIMESTAMP())
        ORDER BY
            candidate.deadline,
            candidate.deadline_id
        LIMIT @batch
        FOR UPDATE
            OF candidate SKIP LOCKED)
RETURNING
    target.deadline_id,
    target.payment_entity_id,
    target.operation_id,
    target.deadline,
    target.gate_callback_key
