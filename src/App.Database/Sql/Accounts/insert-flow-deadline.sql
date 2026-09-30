INSERT INTO fsnix.flow_deadlines (flow_id, timer_kind, generation, deadline, gate_callback_key)
    VALUES (@flow_id, 'flow-expiry', 1, @deadline, @gate)
