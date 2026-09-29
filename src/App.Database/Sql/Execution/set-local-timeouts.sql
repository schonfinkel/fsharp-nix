-- set_config(..., true) is the parameterizable form of SET LOCAL: both last until the end of
-- the current transaction. Values are integral milliseconds.
SELECT
    SET_CONFIG('lock_timeout', @lock_timeout, TRUE),
    SET_CONFIG('statement_timeout', @statement_timeout, TRUE)
