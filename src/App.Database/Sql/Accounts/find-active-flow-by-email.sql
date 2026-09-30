SELECT
    flow_id
FROM
    fsnix.account_flow_requests
WHERE
    flow_kind = @flow_kind
    AND status = 'requested'
    AND expires_at > STATEMENT_TIMESTAMP()
    AND LOWER(TRIM(destination_email)) = LOWER(TRIM(@email))
ORDER BY
    created_at DESC
LIMIT 1
