UPDATE
    fsnix.account_email_outbox AS target
SET
    lease_owner = @owner,
    lease_until = STATEMENT_TIMESTAMP() + (@lease_seconds * interval '1 second'),
    attempts = target.attempts + 1
WHERE
    target.email_id IN (
        SELECT
            candidate.email_id
        FROM
            fsnix.account_email_outbox AS candidate
        WHERE
            candidate.status = 'pending'
            AND candidate.available_at <= STATEMENT_TIMESTAMP()
            AND (candidate.lease_until IS NULL
                OR candidate.lease_until < STATEMENT_TIMESTAMP())
        ORDER BY
            candidate.available_at,
            candidate.email_id
        LIMIT @batch
        FOR UPDATE
            OF candidate SKIP LOCKED)
RETURNING
    target.email_id,
    target.flow_id,
    target.generation,
    target.protected_payload,
    target.encryption_version,
    target.attempts,
    target.max_attempts
