UPDATE
    fsnix.account_email_outbox
SET
    available_at = STATEMENT_TIMESTAMP() + (@backoff_seconds * interval '1 second'),
    lease_owner = NULL,
    lease_until = NULL,
    last_error =
    LEFT (@classification,
        500)
WHERE
    email_id = @email_id
    AND lease_owner = @owner
    AND status = 'pending'
