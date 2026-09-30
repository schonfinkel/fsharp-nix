UPDATE
    fsnix.account_flow_requests
SET
    status = 'completed',
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    flow_id = @flow_id
    AND user_id = @user_id
    AND flow_kind = @flow_kind
    AND status = 'requested'
