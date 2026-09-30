-- @outcome: 'done' | 'cancelled' | 'released'. @delay_seconds (nullable) keeps the lease
-- stamped until a retry backoff elapses, so the claim predicate skips the row until then.
UPDATE
    fsnix.flow_deadlines
SET
    status = CASE @outcome
    WHEN 'done' THEN
        'fired'
    WHEN 'cancelled' THEN
        'cancelled'
    ELSE
        'pending'
    END,
    fired_at = CASE WHEN @outcome = 'done' THEN
        STATEMENT_TIMESTAMP()
    ELSE
        fired_at
    END,
    lease_owner = NULL,
    lease_until = CASE WHEN @delay_seconds IS NULL THEN
        NULL
    ELSE
        STATEMENT_TIMESTAMP() + (@delay_seconds * interval '1 second')
    END
WHERE
    deadline_id = @id
    AND lease_owner = @owner
    AND status = 'pending'
