INSERT INTO fsnix.integration_outbox (callback_key, machine_id, entity_id, event)
    VALUES (@key, @machine, @entity, @event::jsonb)
ON CONFLICT (callback_key)
    DO NOTHING
