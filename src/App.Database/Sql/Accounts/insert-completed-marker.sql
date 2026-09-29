INSERT INTO fsnix.account_operation_markers (operation_id, user_id, flow_id, operation, completed_at)
    VALUES (@operation_id, @user_id, @flow_id, @operation, @completed_at)
ON CONFLICT (operation_id)
    DO NOTHING
