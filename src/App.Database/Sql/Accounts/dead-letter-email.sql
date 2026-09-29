UPDATE
    fsnix.account_email_outbox
SET
    status = 'dead',
    failed_at = STATEMENT_TIMESTAMP(),
    protected_payload = NULL,
    lease_owner = NULL,
    lease_until = NULL,
    last_error =
    LEFT (@classification,
        500)
WHERE
    email_id = @email_id
    AND lease_owner = @owner
    AND status = 'pending'
