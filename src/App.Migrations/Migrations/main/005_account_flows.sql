-- Account flows (P1). Restricted relational rows backing the flows machine: the request
-- (with destination email), the protected email outbox payload, the durable evidence that an
-- Identity mutation committed, and the expiry deadline ledger. FSM data carries only ids;
-- every secret and PII-bearing value stays here.
CREATE TABLE fsnix.account_flow_requests (
    flow_id uuid PRIMARY KEY,
    flow_kind text NOT NULL,
    user_id uuid NOT NULL REFERENCES fsnix.users (id) ON DELETE CASCADE,
    destination_email text NOT NULL,
    status text NOT NULL DEFAULT 'requested',
    generation integer NOT NULL DEFAULT 1,
    resend_count integer NOT NULL DEFAULT 0,
    expires_at timestamptz NOT NULL,
    superseded_by uuid,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_account_flow_requests_kind CHECK (flow_kind IN ('email-verification', 'password-reset', 'email-change')),
    CONSTRAINT ck_account_flow_requests_status CHECK (status IN ('requested', 'superseded', 'completed')),
    CONSTRAINT ck_account_flow_requests_generation_positive CHECK (generation >= 1),
    CONSTRAINT ck_account_flow_requests_resend_nonnegative CHECK (resend_count >= 0),
    CONSTRAINT ck_account_flow_requests_email_not_empty CHECK (LENGTH(TRIM(destination_email)) > 0),
    CONSTRAINT ck_account_flow_requests_expiry_after_creation CHECK (expires_at > created_at)
);

COMMENT ON TABLE fsnix.account_flow_requests IS 'One account-flow request per flow id: kind, user, destination email (the new address for email changes), expiry, and supersession. Status is the relational view; lifecycle decisions live in the flows machine.';

CREATE INDEX ix_account_flow_requests_active ON fsnix.account_flow_requests (user_id, flow_kind)
WHERE
    status = 'requested';

-- Protected notification payloads. The plaintext (including the raw Identity token link)
-- exists only here, encrypted at the application boundary; development exposes simulated
-- delivery, production hands rows to a provider integration.
CREATE TABLE fsnix.account_email_outbox (
    email_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    flow_id uuid NOT NULL REFERENCES fsnix.account_flow_requests (flow_id) ON DELETE CASCADE,
    generation integer NOT NULL,
    protected_payload bytea NOT NULL,
    encryption_version integer NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    attempts integer NOT NULL DEFAULT 0,
    max_attempts integer NOT NULL DEFAULT 10,
    available_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    lease_owner text,
    lease_until timestamptz,
    sent_at timestamptz,
    failed_at timestamptz,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_account_email_outbox_status CHECK (status IN ('pending', 'sent', 'dead')),
    CONSTRAINT ck_account_email_outbox_attempts_nonnegative CHECK (attempts >= 0),
    CONSTRAINT ck_account_email_outbox_max_attempts_positive CHECK (max_attempts > 0),
    CONSTRAINT ck_account_email_outbox_generation_positive CHECK (generation >= 1),
    CONSTRAINT ck_account_email_outbox_encryption_positive CHECK (encryption_version >= 1),
    CONSTRAINT ck_account_email_outbox_sent_requires_timestamp CHECK ((status = 'sent') = (sent_at IS NOT NULL)),
    CONSTRAINT ck_account_email_outbox_dead_has_timestamp CHECK ((status = 'dead') = (failed_at IS NOT NULL)),
    CONSTRAINT ck_account_email_outbox_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL))
);

CREATE INDEX ix_account_email_outbox_claimable ON fsnix.account_email_outbox (available_at, email_id)
WHERE
    status = 'pending';

-- Insert-once evidence that an Identity mutation committed together with its outbox
-- callback. Recovery after a crash distinguishes "operation performed" from "operation
-- unknown" by this row, not by security-stamp archaeology.
CREATE TABLE fsnix.account_operation_markers (
    operation_id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    flow_id uuid REFERENCES fsnix.account_flow_requests (flow_id) ON DELETE SET NULL,
    operation text NOT NULL,
    completed_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_account_operation_markers_operation CHECK (operation IN ('register', 'confirm-email', 'reset-password', 'change-email'))
);

COMMENT ON TABLE fsnix.account_operation_markers IS 'Durable completion evidence for account operations that mutate Identity and hand off to a machine: written in the same transaction as the user mutation and its integration-outbox callback.';

CREATE INDEX ix_account_operation_markers_user ON fsnix.account_operation_markers (user_id, completed_at);

-- Expiry deadline ledger. Each row gates on the callback key that must be durably sent
-- before the timer may fire, so expiry can never overtake the event that starts the flow.
CREATE TABLE fsnix.flow_deadlines (
    deadline_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    flow_id uuid NOT NULL REFERENCES fsnix.account_flow_requests (flow_id) ON DELETE CASCADE,
    timer_kind text NOT NULL DEFAULT 'flow-expiry',
    generation integer NOT NULL DEFAULT 1,
    deadline timestamptz NOT NULL,
    gate_callback_key text NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    lease_owner text,
    lease_until timestamptz,
    fired_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_flow_deadlines_kind CHECK (timer_kind = 'flow-expiry'),
    CONSTRAINT ck_flow_deadlines_status CHECK (status IN ('pending', 'fired', 'cancelled')),
    CONSTRAINT ck_flow_deadlines_generation_positive CHECK (generation >= 1),
    CONSTRAINT ck_flow_deadlines_gate_not_empty CHECK (LENGTH(TRIM(gate_callback_key)) > 0),
    CONSTRAINT ck_flow_deadlines_fired_requires_timestamp CHECK ((status = 'fired') = (fired_at IS NOT NULL)),
    CONSTRAINT ck_flow_deadlines_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL)),
    CONSTRAINT uq_flow_deadlines_flow_generation UNIQUE (flow_id, timer_kind, generation)
);

COMMENT ON TABLE fsnix.flow_deadlines IS 'Indexed expiry deadlines for account flows. The gate_callback_key names the integration-outbox row whose delivery starts (or advances) the flow; a deadline for a gate that never became sent is cancelled, never fired.';

CREATE INDEX ix_flow_deadlines_due ON fsnix.flow_deadlines (deadline, deadline_id)
WHERE
    status = 'pending';

