UPDATE
    fsnix.account_flow_requests
SET
    status = 'superseded',
    superseded_by = @flow_id,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    user_id = @user_id
    AND flow_kind = @flow_kind
    AND status = 'requested'
