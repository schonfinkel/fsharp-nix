SELECT
    status
FROM
    fsnix.integration_outbox
WHERE
    callback_key = @callback_key
