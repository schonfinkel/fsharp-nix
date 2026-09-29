UPDATE
    fsnix.shipment_tracking AS target
SET
    lease_owner = @owner,
    lease_until = STATEMENT_TIMESTAMP() + (@lease_seconds * interval '1 second')
WHERE
    target.shipment_id IN (
        SELECT
            candidate.shipment_id
        FROM
            fsnix.shipment_tracking AS candidate
        WHERE
            candidate.status = 'pending'
            AND candidate.lost_due_at <= STATEMENT_TIMESTAMP()
            AND (candidate.lease_until IS NULL
                OR candidate.lease_until < STATEMENT_TIMESTAMP())
        ORDER BY
            candidate.lost_due_at,
            candidate.shipment_id
        LIMIT @batch
        FOR UPDATE
            OF candidate SKIP LOCKED)
RETURNING
    target.shipment_id,
    target.tracking_generation,
    target.last_scan_at
