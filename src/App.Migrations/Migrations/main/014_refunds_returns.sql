-- Refund operations and immutable return allocations (P6).
ALTER TABLE fsnix.shipments
    ADD COLUMN delivered_at timestamptz;

ALTER TABLE fsnix.payment_operations
    DROP CONSTRAINT ck_payment_operations_kind;

ALTER TABLE fsnix.payment_operations
    ADD CONSTRAINT ck_payment_operations_kind CHECK (kind IN ('authorize', 'void', 'capture', 'refund'));

ALTER TABLE fsnix.payment_operations
    DROP CONSTRAINT ck_payment_operations_provider_reference;

ALTER TABLE fsnix.payment_operations
    ADD CONSTRAINT ck_payment_operations_provider_reference CHECK (kind NOT IN ('authorize', 'capture', 'refund') OR status <> 'succeeded' OR provider_reference IS NOT NULL);

ALTER TABLE fsnix.payment_operations
    ADD COLUMN amount numeric(20, 8);

ALTER TABLE fsnix.payment_operations
    ADD COLUMN currency text;

ALTER TABLE fsnix.payment_operations
    ADD CONSTRAINT ck_payment_refund_amount CHECK (kind <> 'refund' OR (amount > 0 AND currency ~ '^[A-Z]{3}$'));

CREATE TABLE fsnix.refund_allocations (
    allocation_id uuid PRIMARY KEY,
    refund_id uuid NOT NULL UNIQUE,
    order_id text NOT NULL,
    amount numeric(20, 8) NOT NULL CHECK (amount > 0),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    status text NOT NULL CHECK (status IN ('pending', 'settled', 'released')),
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP()
);

CREATE INDEX ix_refund_allocations_order ON fsnix.refund_allocations (order_id, status);

CREATE TABLE fsnix.return_requests (
    return_id uuid PRIMARY KEY,
    authorization_id uuid NOT NULL UNIQUE,
    order_id text NOT NULL,
    window_ends_at timestamptz NOT NULL,
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'expired', 'closed')),
    lease_owner text,
    lease_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP()
);

CREATE INDEX ix_return_requests_due ON fsnix.return_requests (window_ends_at, return_id)
WHERE
    status = 'pending';

CREATE TABLE fsnix.return_lines (
    return_id uuid NOT NULL REFERENCES fsnix.return_requests (return_id),
    order_line_id uuid NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0),
    refunded_amount numeric(20, 8) NOT NULL CHECK (refunded_amount >= 0),
    refunded_tax numeric(20, 8) NOT NULL CHECK (refunded_tax >= 0 AND refunded_tax <= refunded_amount),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    PRIMARY KEY (return_id, order_line_id)
);

CREATE TABLE fsnix.return_restock (
    return_id uuid NOT NULL REFERENCES fsnix.return_requests (return_id),
    order_line_id uuid NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0),
    source_command_id bigint NOT NULL,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    PRIMARY KEY (return_id, order_line_id),
    FOREIGN KEY (return_id, order_line_id) REFERENCES fsnix.return_lines (return_id, order_line_id)
);

CREATE TABLE fsnix.return_tracking_events (
    return_id uuid NOT NULL REFERENCES fsnix.return_requests (return_id),
    event_id text NOT NULL CHECK (CHAR_LENGTH(event_id) BETWEEN 1 AND 128),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (return_id, event_id)
);

