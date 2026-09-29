UPDATE
    fsnix.payment_operations
SET
    status = @status,
    provider_reference = @reference,
    result_code = @result,
    expires_at = @expires,
    attempts = attempts + 1,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    operation_id = @operation
    AND status IN ('pending', 'unknown')
