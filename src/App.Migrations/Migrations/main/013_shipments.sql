-- Shipment ledger and capture-kind support (P5).
ALTER TABLE fsnix.payment_operations
    DROP CONSTRAINT ck_payment_operations_kind;

ALTER TABLE fsnix.payment_operations
    ADD CONSTRAINT ck_payment_operations_kind CHECK (kind IN ('authorize', 'void', 'capture'));

ALTER TABLE fsnix.payment_operations
    DROP CONSTRAINT ck_payment_operations_provider_reference;

ALTER TABLE fsnix.payment_operations
    ADD CONSTRAINT ck_payment_operations_provider_reference CHECK (kind NOT IN ('authorize', 'capture') OR status <> 'succeeded' OR provider_reference IS NOT NULL);

CREATE TABLE fsnix.shipments (
    shipment_id uuid PRIMARY KEY,
    allocation_id uuid NOT NULL UNIQUE,
    order_id text NOT NULL,
    carrier_reference text,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_shipments_carrier_reference_shape CHECK (carrier_reference IS NULL OR (CHAR_LENGTH(carrier_reference) BETWEEN 1 AND 256 AND carrier_reference ~ '^[A-Za-z0-9:/_-]+$'))
);

COMMENT ON TABLE fsnix.shipments IS 'One row per package; the order machine owns the allocation plan, the shipments machine owns lifecycle.';

CREATE INDEX ix_shipments_order ON fsnix.shipments (order_id);

CREATE INDEX ix_payment_operations_capture ON fsnix.payment_operations (order_id, kind)
WHERE
    kind = 'capture';

-- Lost-shipment detection: the last carrier scan per shipment. No newer scan by lost_due_at fires
-- MarkLost with exactly this generation and scan, so a scan that races the scanner makes it a
-- no-op in the shipment machine.
CREATE TABLE fsnix.shipment_tracking (
    shipment_id uuid PRIMARY KEY,
    tracking_generation bigint NOT NULL,
    last_scan_at timestamptz,
    lost_due_at timestamptz NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    lease_owner text,
    lease_until timestamptz,
    fired_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_shipment_tracking_generation CHECK (tracking_generation >= 0),
    CONSTRAINT ck_shipment_tracking_status CHECK (status IN ('pending', 'fired', 'cancelled')),
    CONSTRAINT ck_shipment_tracking_fired_pair CHECK ((status = 'fired') = (fired_at IS NOT NULL)),
    CONSTRAINT ck_shipment_tracking_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL))
);

CREATE INDEX ix_shipment_tracking_due ON fsnix.shipment_tracking (lost_due_at, shipment_id)
WHERE
    status = 'pending';

