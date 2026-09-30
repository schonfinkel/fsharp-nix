SELECT
    EXISTS (
        SELECT
            1
        FROM
            fsnix.return_requests
        WHERE
            return_id = @id
            AND authorization_id = @auth
            AND order_id = @order
            AND status = 'pending'
            AND window_ends_at > STATEMENT_TIMESTAMP())
