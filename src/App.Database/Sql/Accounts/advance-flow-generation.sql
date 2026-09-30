UPDATE
    fsnix.account_flow_requests
SET
    generation = @generation,
    resend_count = @generation - 1,
    expires_at = @expires_at,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    flow_id = @flow_id
    AND status = 'requested'
