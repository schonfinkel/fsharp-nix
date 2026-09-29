UPDATE
    fsnix.invoice_render_checks AS target
SET
    lease_owner = @owner,
    lease_until = STATEMENT_TIMESTAMP() + (@lease_seconds * interval '1 second')
WHERE
    target.invoice_id IN (
        SELECT
            candidate.invoice_id
        FROM
            fsnix.invoice_render_checks AS candidate
        WHERE
            candidate.status = 'pending'
            AND candidate.due_at <= STATEMENT_TIMESTAMP()
            AND (candidate.lease_until IS NULL
                OR candidate.lease_until < STATEMENT_TIMESTAMP())
        ORDER BY
            candidate.due_at,
            candidate.invoice_id
        LIMIT @batch
        FOR UPDATE
            OF candidate SKIP LOCKED)
RETURNING
    target.invoice_id,
    target.checks,
    EXISTS (
        SELECT
            1
        FROM
            fsnix.invoice_documents d
        WHERE
            d.invoice_id = target.invoice_id)
