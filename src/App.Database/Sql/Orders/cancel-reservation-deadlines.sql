UPDATE
    fsnix.reservation_deadlines
SET
    status = 'cancelled',
    lease_owner = NULL,
    lease_until = NULL
WHERE
    order_id = @order
    AND status = 'pending'
