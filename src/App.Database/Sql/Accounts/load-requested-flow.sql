SELECT
    flow_id,
    flow_kind,
    user_id,
    destination_email,
    generation
FROM
    fsnix.account_flow_requests
WHERE
    flow_id = @flow_id
    AND status = 'requested'
