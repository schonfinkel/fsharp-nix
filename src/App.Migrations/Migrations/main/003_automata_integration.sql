-- Exactly-once local execution of machine actions (PLAN.md: distributed workflow
-- contract). The (machine_id, command_id, ordinal) identity names one action of one
-- committed command; payload_hash guards against the same identity carrying a
-- different payload. Written in the same transaction as the effect it guards.
CREATE TABLE fsnix.action_receipts (
    machine_id text NOT NULL,
    command_id bigint NOT NULL,
    ordinal integer NOT NULL,
    action_kind text NOT NULL,
    payload_hash bytea NOT NULL,
    outcome jsonb NOT NULL DEFAULT '{}'::jsonb,
    completed_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT pk_action_receipts PRIMARY KEY (machine_id, command_id, ordinal)
);

COMMENT ON TABLE fsnix.action_receipts IS 'Exactly-once local execution of machine actions: the durable identity of an action and the canonical hash of its payload, committed together with the effect it guards.';

-- Durable handoff from committed local effects back to machines. A relay enqueues
-- each row as a command keyed by callback_key, so a crash between committing an
-- effect and notifying the machine can neither lose nor duplicate the callback:
-- re-delivery resolves to AlreadySubmitted on the destination idempotency key.
CREATE TABLE fsnix.integration_outbox (
    outbox_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    callback_key text NOT NULL,
    machine_id text NOT NULL,
    entity_id text NOT NULL,
    event jsonb NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    attempts integer NOT NULL DEFAULT 0,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    sent_at timestamptz,
    CONSTRAINT uq_integration_outbox_callback UNIQUE (callback_key),
    CONSTRAINT ck_integration_outbox_status CHECK (status IN ('pending', 'sent', 'failed'))
);

COMMENT ON TABLE fsnix.integration_outbox IS 'Integration outbox: durable callbacks from committed local effects to machines, delivered by a relay under stable idempotency keys.';

CREATE INDEX ix_integration_outbox_pending ON fsnix.integration_outbox (outbox_id)
WHERE
    status = 'pending';

