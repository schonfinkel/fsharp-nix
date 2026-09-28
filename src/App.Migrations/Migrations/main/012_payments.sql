-- Payment operation ledger (P4). One row per gateway call; the operation id is the provider
-- idempotency key, so redelivery re-emits the recorded outcome instead of calling again.
ALTER TABLE fsnix.authorization_intents
    ADD COLUMN updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP();

CREATE TABLE fsnix.payment_operations (
    operation_id text PRIMARY KEY,
    payment_entity_id text NOT NULL,
    order_id text NOT NULL,
    kind text NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    provider_reference text,
    result_code text,
    expires_at timestamptz,
    attempts integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_payment_operations_kind CHECK (kind IN ('authorize', 'void')),
    CONSTRAINT ck_payment_operations_status CHECK (status IN ('pending', 'succeeded', 'failed', 'unknown')),
    CONSTRAINT ck_payment_operations_terminal_result CHECK (status IN ('pending', 'unknown') OR result_code IS NOT NULL),
    CONSTRAINT ck_payment_operations_provider_reference CHECK (kind <> 'authorize' OR status <> 'succeeded' OR provider_reference IS NOT NULL),
    CONSTRAINT ck_payment_operations_authorize_expiry CHECK (kind <> 'authorize' OR status <> 'succeeded' OR expires_at IS NOT NULL),
    CONSTRAINT ck_payment_operations_provider_reference_shape CHECK (provider_reference IS NULL OR (CHAR_LENGTH(provider_reference) BETWEEN 1 AND 256 AND provider_reference ~ '^[A-Za-z0-9:/_-]+$')),
    CONSTRAINT ck_payment_operations_result_code_shape CHECK (result_code IS NULL OR (CHAR_LENGTH(result_code) BETWEEN 1 AND 64 AND result_code ~ '^[a-z0-9-]+$'))
);

COMMENT ON TABLE fsnix.payment_operations IS 'Gateway operation ledger. Sanitized outcome codes and opaque provider references only; never cardholder data.';

CREATE INDEX ix_payment_operations_entity ON fsnix.payment_operations (payment_entity_id, kind);

-- Claimable open operations for the P7 reconciliation scanner.
CREATE INDEX ix_payment_operations_open ON fsnix.payment_operations (updated_at, operation_id)
WHERE
    status IN ('pending', 'unknown');

