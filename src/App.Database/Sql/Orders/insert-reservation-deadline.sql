INSERT INTO fsnix.reservation_deadlines (order_id, generation, deadline, gate_callback_key)
    VALUES (@order, @generation, @deadline, @gate)
ON CONFLICT
    DO NOTHING
