UPDATE
    fsnix.integration_outbox
SET
    available_at = STATEMENT_TIMESTAMP() + (@backoff_seconds * interval '1 second'),
    lease_owner = NULL,
    lease_until = NULL,
    last_error =
    LEFT (@message,
        500)
WHERE
    outbox_id = @outbox_id
    AND lease_owner = @owner
    AND status = 'pending'
