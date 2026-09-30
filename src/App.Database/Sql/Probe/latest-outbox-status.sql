SELECT
    status
FROM
    fsnix.integration_outbox
ORDER BY
    outbox_id DESC
LIMIT 1
