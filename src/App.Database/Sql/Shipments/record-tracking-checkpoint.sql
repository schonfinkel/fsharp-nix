-- Moves forward only: an older checkpoint (actions are unordered) or one arriving after the
-- check was stopped leaves the row alone.
INSERT INTO fsnix.shipment_tracking (shipment_id, tracking_generation, last_scan_at, lost_due_at)
    VALUES (@shipment, @generation, @last_scan, @observed + (@lost_after_seconds * interval '1 second'))
ON CONFLICT (shipment_id)
    DO UPDATE SET
        tracking_generation = excluded.tracking_generation,
        last_scan_at = excluded.last_scan_at,
        lost_due_at = excluded.lost_due_at,
        status = 'pending',
        fired_at = NULL,
        lease_owner = NULL,
        lease_until = NULL,
        updated_at = STATEMENT_TIMESTAMP()
    WHERE
        fsnix.shipment_tracking.status <> 'cancelled'
        AND (fsnix.shipment_tracking.tracking_generation, COALESCE(fsnix.shipment_tracking.last_scan_at, '-infinity')) < (excluded.tracking_generation, COALESCE(excluded.last_scan_at, '-infinity'))
