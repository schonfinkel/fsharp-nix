UPDATE
    fsnix.payment_operations
SET
    status = @status,
    provider_reference = @ref,
    result_code = @result,
    attempts = attempts + 1,
    -- The first unknown outcome schedules reconciliation; later ones keep the scanner's backoff.
    next_check_at = CASE WHEN @status = 'unknown' THEN
        COALESCE(next_check_at, STATEMENT_TIMESTAMP() + interval '30 seconds')
    END,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    operation_id = @id
    AND status IN ('pending', 'unknown')
