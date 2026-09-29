UPDATE
    fsnix.cart_deadlines
SET
    status = 'cancelled'
WHERE
    cart_id = @cart_id
    AND status = 'pending'
    AND generation < @generation
