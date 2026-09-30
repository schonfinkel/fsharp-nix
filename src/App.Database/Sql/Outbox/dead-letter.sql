UPDATE
    fsnix.integration_outbox
SET
    status = 'dead',
    failed_at = STATEMENT_TIMESTAMP(),
    lease_owner = NULL,
    lease_until = NULL,
    last_error =
    LEFT (@message,
        500)
WHERE
    outbox_id = @outbox_id
    AND lease_owner = @owner
    AND status = 'pending'
