UPDATE
    fsnix.payment_operations
SET
    status = @status,
    provider_reference = @ref,
    result_code = @result,
    attempts = attempts + 1,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    operation_id = @id
    AND status IN ('pending', 'unknown')
