-- Catalog and stock (P2). Relational consistency boundary: products are not a machine, and
-- stock is a plain ledger-owned table (the reservation ledger arrives in P3). Prices carry a
-- monotonically increasing version so a later reservation can reject a stale snapshot. Money
-- is stored as an exact numeric amount plus an ISO 4217 code; the tsvector search column is a
-- generated, GIN-indexed projection for full-text search.
CREATE TABLE fsnix.products (
    product_id uuid PRIMARY KEY,
    sku text NOT NULL,
    name text NOT NULL,
    description text,
    price_amount numeric(20, 8) NOT NULL,
    price_currency text NOT NULL,
    price_version bigint NOT NULL DEFAULT 1,
    active boolean NOT NULL DEFAULT TRUE,
    search tsvector GENERATED ALWAYS AS (TO_TSVECTOR('english', name || ' ' || COALESCE(description, ''))) STORED,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_products_sku_uppercase CHECK (sku = UPPER(sku)),
    CONSTRAINT ck_products_sku_not_empty CHECK (LENGTH(TRIM(sku)) > 0),
    CONSTRAINT ck_products_name_not_empty CHECK (LENGTH(TRIM(name)) > 0),
    CONSTRAINT ck_products_price_nonnegative CHECK (price_amount >= 0),
    CONSTRAINT ck_products_currency_code CHECK (price_currency ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_products_price_version_positive CHECK (price_version >= 1)
);

COMMENT ON TABLE fsnix.products IS 'Catalog products. Immutable sku identity; mutable name/description/price/active. price_version bumps on every price change so cart and reservation snapshots stay detectably stale.';

CREATE UNIQUE INDEX ux_products_sku ON fsnix.products (sku);

CREATE INDEX ix_products_browse ON fsnix.products (active, name);

CREATE INDEX ix_products_price ON fsnix.products (active, price_amount);

CREATE INDEX ix_products_search ON fsnix.products USING GIN (search);

-- Physical stock. on_hand is the operator-owned truth; reserved is claimed by the P3
-- reservation ledger (kept zero in P2, decremented/incremented only by that ledger).
CREATE TABLE fsnix.product_stock (
    product_id uuid PRIMARY KEY REFERENCES fsnix.products (product_id) ON DELETE CASCADE,
    on_hand integer NOT NULL DEFAULT 0,
    reserved integer NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_product_stock_on_hand_nonnegative CHECK (on_hand >= 0),
    CONSTRAINT ck_product_stock_reserved_nonnegative CHECK (reserved >= 0),
    CONSTRAINT ck_product_stock_reserved_within_hand CHECK (reserved <= on_hand)
);

COMMENT ON TABLE fsnix.product_stock IS 'Available physical stock per product. reserved is maintained by the reservation ledger (P3); the operator adjusts only on_hand.';

