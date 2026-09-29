INSERT INTO fsnix.account_flow_requests (flow_id, flow_kind, user_id, destination_email, status, generation, resend_count, expires_at, idempotency_key_hash, request_hash)
    VALUES (@flow_id, @flow_kind, @user_id, @destination, 'requested', 1, 0, @expires_at, @key_hash, @request_hash)
