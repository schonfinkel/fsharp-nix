UPDATE
    fsnix.authorization_intents
SET
    status = 'cancelled'
WHERE
    order_id = @order
    AND status IN ('pending', 'claimed')
