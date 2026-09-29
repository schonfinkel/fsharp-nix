-- @outcome: 'done' | 'cancelled' | 'released'. @delay_seconds (nullable) keeps the lease
-- stamped until a retry backoff elapses, so the claim predicate skips the row until then.
UPDATE
    fsnix.return_requests
SET
    status = CASE @outcome
    WHEN 'done' THEN
        'expired'
    WHEN 'cancelled' THEN
        'closed'
    ELSE
        'pending'
    END,
    lease_owner = NULL,
    lease_until = CASE WHEN @delay_seconds IS NULL THEN
        NULL
    ELSE
        STATEMENT_TIMESTAMP() + (@delay_seconds * interval '1 second')
    END
WHERE
    return_id = @id
    AND lease_owner = @owner
    AND status = 'pending'
