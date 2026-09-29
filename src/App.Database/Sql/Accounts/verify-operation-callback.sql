SELECT
    machine_id,
    entity_id,
    event = @event::jsonb
FROM
    fsnix.integration_outbox
WHERE
    callback_key = @callback_key
