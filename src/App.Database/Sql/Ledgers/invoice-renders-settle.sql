-- @outcome: 'done' | 'cancelled' finish the check. 'released' with @delay_seconds records one
-- more re-render request and schedules the next check; without a delay it only frees the lease.
UPDATE
    fsnix.invoice_render_checks
SET
    status = CASE WHEN @outcome IN ('done', 'cancelled') THEN
        'done'
    ELSE
        'pending'
    END,
    checks = checks + CASE WHEN @outcome = 'released'
        AND @delay_seconds IS NOT NULL THEN
        1
    ELSE
        0
    END,
    due_at = CASE WHEN @outcome = 'released'
        AND @delay_seconds IS NOT NULL THEN
        STATEMENT_TIMESTAMP() + (@delay_seconds * interval '1 second')
    ELSE
        due_at
    END,
    lease_owner = NULL,
    lease_until = NULL
WHERE
    invoice_id = @id
    AND lease_owner = @owner
    AND status = 'pending'
