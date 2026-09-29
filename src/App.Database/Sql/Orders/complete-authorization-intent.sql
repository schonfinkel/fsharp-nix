UPDATE
    fsnix.authorization_intents
SET
    status = 'completed'
WHERE
    order_id = @order
    AND status = 'claimed'
