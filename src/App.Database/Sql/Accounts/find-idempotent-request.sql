SELECT
    flow_id,
    request_hash
FROM
    fsnix.account_flow_requests
WHERE
    user_id = @user_id
    AND flow_kind = @flow_kind
    AND idempotency_key_hash = @key_hash
FOR UPDATE
