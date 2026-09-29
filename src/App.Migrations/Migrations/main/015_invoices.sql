-- Gapless scoped invoice numbering and the immutable invoice snapshot (P7).
CREATE TABLE fsnix.invoice_counters (
    legal_entity text NOT NULL,
    series text NOT NULL,
    fiscal_period text NOT NULL,
    last_number bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    PRIMARY KEY (legal_entity, series, fiscal_period),
    CONSTRAINT ck_invoice_counters_last_number CHECK (last_number >= 0),
    CONSTRAINT ck_invoice_counters_scope CHECK (legal_entity ~ '^[A-Z0-9]([A-Z0-9-]{0,14}[A-Z0-9])?$' AND series ~ '^[A-Z0-9]([A-Z0-9-]{0,14}[A-Z0-9])?$' AND fiscal_period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$')
);

COMMENT ON TABLE fsnix.invoice_counters IS 'One row per numbering scope. A number is allocated only by incrementing this row inside the issuance transaction, so a rolled-back issuance never consumes a number. Never use MAX(number)+1 or a sequence.';

CREATE TABLE fsnix.invoices (
    invoice_id uuid PRIMARY KEY,
    order_id text NOT NULL,
    snapshot_id uuid NOT NULL REFERENCES fsnix.order_snapshots (snapshot_id),
    customer_id uuid NOT NULL,
    legal_entity text NOT NULL,
    series text NOT NULL,
    fiscal_period text NOT NULL,
    number bigint NOT NULL,
    issued_at timestamptz NOT NULL,
    seller jsonb NOT NULL,
    buyer jsonb NOT NULL,
    billing_address jsonb NOT NULL,
    subtotal_amount numeric(20, 8) NOT NULL,
    shipping_amount numeric(20, 8) NOT NULL,
    tax_amount numeric(20, 8) NOT NULL,
    total_amount numeric(20, 8) NOT NULL,
    currency text NOT NULL,
    source_machine_id text NOT NULL,
    source_command_id bigint NOT NULL,
    source_ordinal integer NOT NULL,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT uq_invoices_order UNIQUE (order_id),
    CONSTRAINT uq_invoices_number UNIQUE (legal_entity, series, fiscal_period, number),
    CONSTRAINT fk_invoices_counter FOREIGN KEY (legal_entity, series, fiscal_period) REFERENCES fsnix.invoice_counters (legal_entity, series, fiscal_period),
    CONSTRAINT ck_invoices_number CHECK (number BETWEEN 1 AND 99999999),
    CONSTRAINT ck_invoices_amounts CHECK (subtotal_amount >= 0 AND shipping_amount >= 0 AND tax_amount >= 0 AND total_amount = subtotal_amount + shipping_amount + tax_amount),
    CONSTRAINT ck_invoices_currency CHECK (currency ~ '^[A-Z]{3}$')
);

CREATE INDEX ix_invoices_customer ON fsnix.invoices (customer_id, issued_at DESC, invoice_id DESC);

CREATE INDEX ix_invoices_issued ON fsnix.invoices (issued_at DESC, invoice_id DESC);

COMMENT ON TABLE fsnix.invoices IS 'Insert-only legal invoice header. Seller, buyer and billing address are snapshotted at issuance; corrections are separate adjustment documents, never edits.';

CREATE TABLE fsnix.invoice_lines (
    invoice_id uuid NOT NULL REFERENCES fsnix.invoices (invoice_id),
    line_number integer NOT NULL,
    order_line_id uuid NOT NULL,
    product_id uuid NOT NULL,
    sku text NOT NULL,
    description text NOT NULL,
    unit_price numeric(20, 8) NOT NULL,
    quantity integer NOT NULL,
    line_amount numeric(20, 8) NOT NULL,
    currency text NOT NULL,
    PRIMARY KEY (invoice_id, line_number),
    CONSTRAINT uq_invoice_lines_order_line UNIQUE (invoice_id, order_line_id),
    CONSTRAINT ck_invoice_lines_line_number CHECK (line_number > 0),
    CONSTRAINT ck_invoice_lines_amounts CHECK (unit_price >= 0 AND quantity > 0 AND line_amount = unit_price * quantity),
    CONSTRAINT ck_invoice_lines_currency CHECK (currency ~ '^[A-Z]{3}$')
);

-- Rendered PDFs, content-addressed and insert-only: a template change adds a new document under
-- a new digest and renderer, it never rewrites an issued invoice's stored PDF.
CREATE TABLE fsnix.invoice_documents (
    invoice_id uuid NOT NULL REFERENCES fsnix.invoices (invoice_id),
    sha256 bytea NOT NULL,
    renderer text NOT NULL,
    content bytea NOT NULL,
    size integer NOT NULL,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    PRIMARY KEY (invoice_id, sha256),
    CONSTRAINT ck_invoice_documents_sha256 CHECK (OCTET_LENGTH(sha256) = 32),
    CONSTRAINT ck_invoice_documents_size CHECK (size > 0 AND size = OCTET_LENGTH(content)),
    CONSTRAINT ck_invoice_documents_renderer CHECK (renderer ~ '^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$')
);

CREATE INDEX ix_invoice_documents_latest ON fsnix.invoice_documents (invoice_id, created_at DESC);

-- Render recovery: armed at issuance, checked by the invoice render scanner. A render that
-- has not produced a document by due_at is re-requested a bounded number of times.
CREATE TABLE fsnix.invoice_render_checks (
    invoice_id uuid PRIMARY KEY REFERENCES fsnix.invoices (invoice_id),
    due_at timestamptz NOT NULL,
    checks integer NOT NULL DEFAULT 0,
    status text NOT NULL DEFAULT 'pending',
    lease_owner text,
    lease_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT STATEMENT_TIMESTAMP(),
    CONSTRAINT ck_invoice_render_checks_checks CHECK (checks >= 0),
    CONSTRAINT ck_invoice_render_checks_status CHECK (status IN ('pending', 'done')),
    CONSTRAINT ck_invoice_render_checks_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL))
);

CREATE INDEX ix_invoice_render_checks_due ON fsnix.invoice_render_checks (due_at, invoice_id)
WHERE
    status = 'pending';

-- An invoice may only take the number its scope counter currently holds. Issuance increments
-- the counter and inserts in one transaction, so this rejects any number not allocated there
-- (a hand-picked, reused or MAX+1 number).
CREATE FUNCTION fsnix.check_invoice_number_allocated ()
    RETURNS TRIGGER
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NOT EXISTS (
        SELECT
            1
        FROM
            fsnix.invoice_counters
        WHERE
            legal_entity = NEW.legal_entity
            AND series = NEW.series
            AND fiscal_period = NEW.fiscal_period
            AND last_number = NEW.number) THEN
    RAISE EXCEPTION 'invoice number % was not allocated by its scope counter', NEW.number;
END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER tr_invoices_number_allocated
    BEFORE INSERT ON fsnix.invoices
    FOR EACH ROW
    EXECUTE FUNCTION fsnix.check_invoice_number_allocated ();

CREATE FUNCTION fsnix.reject_invoice_mutation ()
    RETURNS TRIGGER
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION '% is insert-only', TG_TABLE_NAME;
END;
$$;

CREATE TRIGGER tr_invoices_immutable
    BEFORE UPDATE OR DELETE ON fsnix.invoices
    FOR EACH ROW
    EXECUTE FUNCTION fsnix.reject_invoice_mutation ();

CREATE TRIGGER tr_invoices_no_truncate
    BEFORE TRUNCATE ON fsnix.invoices
    FOR EACH STATEMENT
    EXECUTE FUNCTION fsnix.reject_invoice_mutation ();

CREATE TRIGGER tr_invoice_lines_immutable
    BEFORE UPDATE OR DELETE ON fsnix.invoice_lines
    FOR EACH ROW
    EXECUTE FUNCTION fsnix.reject_invoice_mutation ();

CREATE TRIGGER tr_invoice_lines_no_truncate
    BEFORE TRUNCATE ON fsnix.invoice_lines
    FOR EACH STATEMENT
    EXECUTE FUNCTION fsnix.reject_invoice_mutation ();

CREATE TRIGGER tr_invoice_documents_immutable
    BEFORE UPDATE OR DELETE ON fsnix.invoice_documents
    FOR EACH ROW
    EXECUTE FUNCTION fsnix.reject_invoice_mutation ();

CREATE TRIGGER tr_invoice_documents_no_truncate
    BEFORE TRUNCATE ON fsnix.invoice_documents
    FOR EACH STATEMENT
    EXECUTE FUNCTION fsnix.reject_invoice_mutation ();

