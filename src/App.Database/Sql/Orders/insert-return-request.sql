INSERT INTO fsnix.return_requests (return_id, authorization_id, order_id, window_ends_at)
    VALUES (@id, @auth, @order, @deadline)
ON CONFLICT (return_id)
    DO NOTHING
