INSERT INTO fsnix.order_reservation_controls (order_id, generation, status)
    VALUES (@order, 1, 'cancelled')
ON CONFLICT (order_id)
    DO UPDATE SET
        status = 'cancelled',
        updated_at = STATEMENT_TIMESTAMP()
