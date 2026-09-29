UPDATE
    fsnix.flow_deadlines
SET
    status = 'cancelled'
WHERE
    status = 'pending'
    AND flow_id IN (
        SELECT
            flow_id
        FROM
            fsnix.account_flow_requests
        WHERE
            user_id = @user_id
            AND flow_kind = @flow_kind
            AND status = 'superseded')
