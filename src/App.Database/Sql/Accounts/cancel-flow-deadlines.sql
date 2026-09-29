UPDATE
    fsnix.flow_deadlines
SET
    status = 'cancelled'
WHERE
    flow_id = @flow_id
    AND status = 'pending'
