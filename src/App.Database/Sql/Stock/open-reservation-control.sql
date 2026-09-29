INSERT INTO fsnix.order_reservation_controls (order_id, generation, status)
    VALUES (@order, @generation, 'open')
ON CONFLICT (order_id)
    DO NOTHING
