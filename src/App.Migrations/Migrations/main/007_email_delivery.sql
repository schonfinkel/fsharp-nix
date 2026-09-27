-- Email delivery lifecycle (P1). The protected token payload is only needed while the email
-- is waiting to be delivered; once the row reaches a terminal state (sent or dead) the relay
-- erases it, so raw Identity tokens never outlive their delivery window in the database.
ALTER TABLE fsnix.account_email_outbox
    ALTER COLUMN protected_payload DROP NOT NULL;

-- Existing terminal rows (development databases only) must satisfy the new lifecycle rule.
UPDATE
    fsnix.account_email_outbox
SET
    protected_payload = NULL
WHERE
    status IN ('sent', 'dead');

ALTER TABLE fsnix.account_email_outbox
    ADD CONSTRAINT ck_account_email_outbox_payload_lifecycle CHECK ((status = 'pending' AND protected_payload IS NOT NULL) OR (status IN ('sent', 'dead') AND protected_payload IS NULL));

COMMENT ON CONSTRAINT ck_account_email_outbox_payload_lifecycle ON fsnix.account_email_outbox IS 'The protected token payload exists only while the email is pending delivery; terminal rows always have it erased.';

