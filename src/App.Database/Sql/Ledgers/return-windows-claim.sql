UPDATE
    fsnix.return_requests AS target
SET
    lease_owner = @owner,
    lease_until = STATEMENT_TIMESTAMP() + (@lease_seconds * interval '1 second')
WHERE
    target.return_id IN (
        SELECT
            candidate.return_id
        FROM
            fsnix.return_requests AS candidate
        WHERE
            candidate.status = 'pending'
            AND candidate.window_ends_at <= STATEMENT_TIMESTAMP()
            AND (candidate.lease_until IS NULL
                OR candidate.lease_until < STATEMENT_TIMESTAMP())
        ORDER BY
            candidate.window_ends_at,
            candidate.return_id
        LIMIT @batch
        FOR UPDATE
            OF candidate SKIP LOCKED)
RETURNING
    target.return_id,
    target.authorization_id,
    target.window_ends_at
