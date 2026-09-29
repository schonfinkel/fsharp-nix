INSERT INTO fsnix.integration_outbox (callback_key, machine_id, entity_id, event)
    VALUES (@callback_key, @machine_id, @entity_id, @event::jsonb)
ON CONFLICT (callback_key)
    DO NOTHING
RETURNING
    outbox_id
