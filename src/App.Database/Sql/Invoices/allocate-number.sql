INSERT INTO fsnix.invoice_counters (legal_entity, series, fiscal_period)
    VALUES (@entity, @series, @period)
ON CONFLICT
    DO NOTHING;

UPDATE
    fsnix.invoice_counters
SET
    last_number = last_number + 1,
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    legal_entity = @entity
    AND series = @series
    AND fiscal_period = @period
RETURNING
    last_number
