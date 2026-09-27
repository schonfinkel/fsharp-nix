-- Outbox delivery hardening (P1). The probe-era relay held row locks while enqueueing and
-- had no lease, backoff, or dead-letter state; multi-machine delivery needs all three.
-- Delivery is now: claim under a short transaction (lease + attempts increment), enqueue
-- outside the transaction, then complete with a lease-fenced update.
ALTER TABLE fsnix.integration_outbox
    ADD COLUMN available_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    ADD COLUMN lease_owner text,
    ADD COLUMN lease_until timestamptz,
    ADD COLUMN max_attempts integer NOT NULL DEFAULT 10,
    ADD COLUMN failed_at timestamptz;

-- 'failed' is dropped from the vocabulary: a row that failed delivery stays 'pending' with a
-- future available_at (bounded exponential backoff); 'dead' is the terminal state after
-- max_attempts. Every intermediate state remains claimable, so recovery never loses rows.
ALTER TABLE fsnix.integration_outbox
    DROP CONSTRAINT ck_integration_outbox_status;

-- Earlier relays used 'failed' as a retryable state. Preserve those rows by putting them back
-- into the pending queue before narrowing the status vocabulary.
UPDATE
    fsnix.integration_outbox
SET
    status = 'pending',
    available_at = STATEMENT_TIMESTAMP()
WHERE
    status = 'failed';

ALTER TABLE fsnix.integration_outbox
    ADD CONSTRAINT ck_integration_outbox_status CHECK (status IN ('pending', 'sent', 'dead')),
    ADD CONSTRAINT ck_integration_outbox_attempts_nonnegative CHECK (attempts >= 0),
    ADD CONSTRAINT ck_integration_outbox_max_attempts_positive CHECK (max_attempts > 0),
    ADD CONSTRAINT ck_integration_outbox_sent_requires_timestamp CHECK ((status = 'sent') = (sent_at IS NOT NULL)),
    ADD CONSTRAINT ck_integration_outbox_dead_has_timestamp CHECK ((status = 'dead') = (failed_at IS NOT NULL)),
    ADD CONSTRAINT ck_integration_outbox_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL));

DROP INDEX ix_integration_outbox_pending;

CREATE INDEX ix_integration_outbox_claimable ON fsnix.integration_outbox (available_at, outbox_id)
WHERE
    status = 'pending';

