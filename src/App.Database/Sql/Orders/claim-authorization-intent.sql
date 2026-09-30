INSERT INTO fsnix.authorization_intents (order_id, amount, currency, status)
SELECT
    @order,
    @amount,
    @currency,
    'claimed'
WHERE
    EXISTS (
        SELECT
            1
        FROM
            fsnix.order_reservation_controls
        WHERE
            order_id = @order
            AND status = 'open')
ON CONFLICT (order_id)
    DO UPDATE SET
        status = 'claimed',
        updated_at = STATEMENT_TIMESTAMP()
    WHERE
        fsnix.authorization_intents.status = 'pending'
