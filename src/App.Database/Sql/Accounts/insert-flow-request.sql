INSERT INTO fsnix.account_flow_requests (flow_id, flow_kind, user_id, destination_email, status, generation, resend_count, expires_at, created_at, updated_at)
    VALUES (@flow_id, @flow_kind, @user_id, @destination_email, 'requested', 1, 0, @expires_at, STATEMENT_TIMESTAMP(), STATEMENT_TIMESTAMP())
