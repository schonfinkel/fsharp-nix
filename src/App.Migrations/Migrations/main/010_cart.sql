-- Cart machine backing tables (P2). The carts machine (fsm.carts) owns lifecycle decisions;
-- these relational tables own the secrets and invariants the FSM must not carry: the guest
-- capability (keyed hash only), the immutable merge snapshot, and the indexed abandonment
-- deadline ledger.
CREATE TABLE fsnix.cart_guest_capabilities (
    locate_digest bytea PRIMARY KEY,
    key_id text NOT NULL,
    purpose text NOT NULL,
    entity_id text NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked boolean NOT NULL DEFAULT FALSE,
    revoked_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_cart_guest_capabilities_purpose CHECK (purpose = 'guest-cart'),
    CONSTRAINT ck_cart_guest_capabilities_entity_not_empty CHECK (LENGTH(TRIM(entity_id)) > 0),
    CONSTRAINT ck_cart_guest_capabilities_expiry_after_creation CHECK (expires_at > created_at),
    CONSTRAINT ck_cart_guest_capabilities_revoked_pair CHECK (revoked OR revoked_at IS NULL),
    CONSTRAINT ck_cart_guest_capabilities_digest_length CHECK (OCTET_LENGTH(locate_digest) = 32)
);

COMMENT ON TABLE fsnix.cart_guest_capabilities IS 'Guest cart bearer capabilities: only the purpose-scoped keyed hash is stored, never the raw 256-bit token. The entity_id names the guest cart machine entity.';

CREATE INDEX ix_cart_guest_capabilities_entity ON fsnix.cart_guest_capabilities (entity_id);

-- Immutable source snapshot captured when a guest cart freezes for merge. Insert-once; the
-- customer cart applies it idempotently and the reconciler uses it to resume a lost handoff.
CREATE TABLE fsnix.cart_merge_snapshots (
    merge_id uuid PRIMARY KEY,
    source_cart_id text NOT NULL,
    target_customer_id text NOT NULL,
    lines jsonb NOT NULL,
    status text NOT NULL DEFAULT 'captured',
    captured_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    applied_at timestamptz,
    CONSTRAINT ck_cart_merge_snapshots_status CHECK (status IN ('captured', 'applied', 'failed')),
    CONSTRAINT ck_cart_merge_snapshots_source_not_empty CHECK (LENGTH(TRIM(source_cart_id)) > 0),
    CONSTRAINT ck_cart_merge_snapshots_target_not_empty CHECK (LENGTH(TRIM(target_customer_id)) > 0),
    CONSTRAINT ck_cart_merge_snapshots_applied_pair CHECK ((status = 'applied') = (applied_at IS NOT NULL))
);

COMMENT ON TABLE fsnix.cart_merge_snapshots IS 'Immutable guest-cart line snapshot for the merge saga, keyed by MergeId. Source and target entity ids live here; lines are the frozen source lines.';

CREATE INDEX ix_cart_merge_snapshots_source ON fsnix.cart_merge_snapshots (source_cart_id, status);

CREATE INDEX ix_cart_merge_snapshots_target ON fsnix.cart_merge_snapshots (target_customer_id, status);

-- Indexed cart abandonment deadlines. Mirrors flow_deadlines: each row gates on the callback
-- key that durably recorded the cart touch, so an abandonment timer can never fire before the
-- mutation that armed it became durable.
CREATE TABLE fsnix.cart_deadlines (
    deadline_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    cart_id text NOT NULL,
    timer_kind text NOT NULL DEFAULT 'cart-abandonment',
    generation bigint NOT NULL DEFAULT 1,
    deadline timestamptz NOT NULL,
    gate_callback_key text NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    lease_owner text,
    lease_until timestamptz,
    fired_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_cart_deadlines_kind CHECK (timer_kind = 'cart-abandonment'),
    CONSTRAINT ck_cart_deadlines_status CHECK (status IN ('pending', 'fired', 'cancelled')),
    CONSTRAINT ck_cart_deadlines_generation_positive CHECK (generation >= 1),
    CONSTRAINT ck_cart_deadlines_gate_not_empty CHECK (LENGTH(TRIM(gate_callback_key)) > 0),
    CONSTRAINT ck_cart_deadlines_fired_requires_timestamp CHECK ((status = 'fired') = (fired_at IS NOT NULL)),
    CONSTRAINT ck_cart_deadlines_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL)),
    CONSTRAINT uq_cart_deadlines_cart_generation UNIQUE (cart_id, timer_kind, generation)
);

COMMENT ON TABLE fsnix.cart_deadlines IS 'Indexed cart abandonment deadlines. The gate_callback_key names the integration-outbox row whose delivery records the cart touch that armed the timer.';

CREATE INDEX ix_cart_deadlines_due ON fsnix.cart_deadlines (deadline, deadline_id)
WHERE
    status = 'pending';

