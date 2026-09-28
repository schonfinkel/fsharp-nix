-- Order snapshots and the stock reservation ledger (P3).
CREATE TABLE fsnix.order_snapshots (
    snapshot_id uuid PRIMARY KEY,
    order_id text NOT NULL UNIQUE,
    customer_id uuid NOT NULL,
    customer_email text NOT NULL,
    address jsonb NOT NULL,
    lines jsonb NOT NULL,
    subtotal_amount numeric(20, 8) NOT NULL,
    shipping_amount numeric(20, 8) NOT NULL,
    tax_amount numeric(20, 8) NOT NULL,
    total_amount numeric(20, 8) NOT NULL,
    currency text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_order_snapshots_amounts_nonnegative CHECK (subtotal_amount >= 0 AND shipping_amount >= 0 AND tax_amount >= 0 AND total_amount >= 0),
    CONSTRAINT ck_order_snapshots_currency CHECK (currency ~ '^[A-Z]{3}$')
);

COMMENT ON TABLE fsnix.order_snapshots IS 'Restricted immutable checkout PII and commercial snapshot. FSM beliefs carry only snapshot_id.';

CREATE FUNCTION fsnix.reject_order_snapshot_mutation ()
    RETURNS TRIGGER
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'order snapshots are immutable';
END;
$$;

CREATE TRIGGER tr_order_snapshots_immutable
    BEFORE UPDATE OR DELETE ON fsnix.order_snapshots
    FOR EACH ROW
    EXECUTE FUNCTION fsnix.reject_order_snapshot_mutation ();

CREATE TABLE fsnix.stock_reservations (
    reservation_id uuid PRIMARY KEY,
    order_id text NOT NULL,
    order_line_id uuid NOT NULL UNIQUE,
    product_id uuid NOT NULL REFERENCES fsnix.products (product_id),
    quantity integer NOT NULL,
    status text NOT NULL DEFAULT 'reserved',
    expires_at timestamptz NOT NULL,
    source_machine_id text NOT NULL,
    source_command_id bigint NOT NULL,
    source_ordinal integer NOT NULL,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    settled_at timestamptz,
    CONSTRAINT ck_stock_reservations_quantity CHECK (quantity > 0),
    CONSTRAINT ck_stock_reservations_status CHECK (status IN ('reserved', 'committed', 'released')),
    CONSTRAINT ck_stock_reservations_settled_pair CHECK ((status = 'reserved') = (settled_at IS NULL)),
    CONSTRAINT uq_stock_reservations_action UNIQUE (source_machine_id, source_command_id, source_ordinal, order_line_id)
);

CREATE INDEX ix_stock_reservations_order ON fsnix.stock_reservations (order_id, status);

CREATE INDEX ix_stock_reservations_product_status ON fsnix.stock_reservations (product_id, status);

CREATE INDEX ix_stock_reservations_expiry ON fsnix.stock_reservations (expires_at, order_id)
WHERE
    status = 'reserved';

-- Fences unordered reserve/release actions: an early release permanently prevents a delayed
-- reserve action from claiming stock.
CREATE TABLE fsnix.order_reservation_controls (
    order_id text PRIMARY KEY,
    generation bigint NOT NULL,
    status text NOT NULL DEFAULT 'open',
    updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_order_reservation_controls_generation CHECK (generation > 0),
    CONSTRAINT ck_order_reservation_controls_status CHECK (status IN ('open', 'cancelled'))
);

CREATE TABLE fsnix.reservation_deadlines (
    deadline_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id text NOT NULL,
    generation bigint NOT NULL,
    deadline timestamptz NOT NULL,
    gate_callback_key text NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    lease_owner text,
    lease_until timestamptz,
    fired_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_reservation_deadlines_generation CHECK (generation > 0),
    CONSTRAINT ck_reservation_deadlines_status CHECK (status IN ('pending', 'fired', 'cancelled')),
    CONSTRAINT ck_reservation_deadlines_fired_pair CHECK ((status = 'fired') = (fired_at IS NOT NULL)),
    CONSTRAINT ck_reservation_deadlines_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL)),
    CONSTRAINT uq_reservation_deadlines_order_generation UNIQUE (order_id, generation)
);

CREATE INDEX ix_reservation_deadlines_due ON fsnix.reservation_deadlines (deadline, deadline_id)
WHERE
    status = 'pending';

CREATE TABLE fsnix.authorization_intents (
    order_id text PRIMARY KEY,
    amount numeric(20, 8) NOT NULL,
    currency text NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_authorization_intents_amount CHECK (amount >= 0),
    CONSTRAINT ck_authorization_intents_currency CHECK (currency ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_authorization_intents_status CHECK (status IN ('pending', 'claimed', 'completed', 'cancelled'))
);

