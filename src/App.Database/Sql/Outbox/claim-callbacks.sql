UPDATE
    fsnix.integration_outbox AS target
SET
    lease_owner = @owner,
    lease_until = STATEMENT_TIMESTAMP() + (@lease_seconds * interval '1 second'),
    attempts = target.attempts + 1
WHERE
    target.outbox_id IN (
        SELECT
            candidate.outbox_id
        FROM
            fsnix.integration_outbox AS candidate
        WHERE
            candidate.status = 'pending'
            AND candidate.available_at <= STATEMENT_TIMESTAMP()
            AND (candidate.lease_until IS NULL
                OR candidate.lease_until < STATEMENT_TIMESTAMP())
        ORDER BY
            candidate.available_at,
            candidate.outbox_id
        LIMIT @batch
        FOR UPDATE
            OF candidate SKIP LOCKED)
RETURNING
    target.outbox_id,
    target.callback_key,
    target.machine_id,
    target.entity_id,
    target.event,
    target.attempts,
    target.max_attempts
