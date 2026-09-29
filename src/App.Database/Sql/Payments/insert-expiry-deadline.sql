INSERT INTO fsnix.payment_deadlines (payment_entity_id, operation_id, deadline, gate_callback_key)
    VALUES (@entity, @operation, @deadline, @gate)
ON CONFLICT (payment_entity_id, operation_id)
    DO NOTHING
