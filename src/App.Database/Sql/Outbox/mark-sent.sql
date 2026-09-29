UPDATE
    fsnix.integration_outbox
SET
    status = 'sent',
    sent_at = STATEMENT_TIMESTAMP(),
    lease_owner = NULL,
    lease_until = NULL,
    last_error = NULL
WHERE
    outbox_id = @outbox_id
    AND lease_owner = @owner
    AND status = 'pending'
