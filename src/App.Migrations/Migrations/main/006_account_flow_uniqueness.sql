-- Concurrency invariants for account-flow creation and notification generation.
DROP INDEX fsnix.ix_account_flow_requests_active;

CREATE UNIQUE INDEX ux_account_flow_requests_active ON fsnix.account_flow_requests (user_id, flow_kind)
WHERE
    status = 'requested';

ALTER TABLE fsnix.account_email_outbox
    ADD CONSTRAINT uq_account_email_outbox_flow_generation UNIQUE (flow_id, generation);

