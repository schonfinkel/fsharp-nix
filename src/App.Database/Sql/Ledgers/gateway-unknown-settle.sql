-- @advance false: the event was not accepted; release the lease and keep the row due.
-- @advance true: one more check was sent; @next_check_seconds schedules the next one, or NULL
-- parks the row after the final (exhausted) event. Fenced on the lease so an expired pass
-- cannot overwrite a newer one.
UPDATE
    fsnix.payment_operations
SET
    checks = checks + CASE WHEN @advance THEN
        1
    ELSE
        0
    END,
    next_check_at = CASE WHEN NOT @advance THEN
        next_check_at
    WHEN @next_check_seconds IS NULL THEN
        NULL
    ELSE
        STATEMENT_TIMESTAMP() + (@next_check_seconds * interval '1 second')
    END,
    lease_owner = NULL,
    lease_until = NULL
WHERE
    operation_id = @id
    AND lease_owner = @owner
    AND status = 'unknown'
