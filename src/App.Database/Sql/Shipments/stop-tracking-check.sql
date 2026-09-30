-- Terminal outcome: stop checking. Inserts a stopped row when the arming checkpoint has not
-- been applied yet, so a late checkpoint cannot re-arm it.
INSERT INTO fsnix.shipment_tracking (shipment_id, tracking_generation, lost_due_at, status)
    VALUES (@shipment, 0, STATEMENT_TIMESTAMP(), 'cancelled')
ON CONFLICT (shipment_id)
    DO UPDATE SET
        status = 'cancelled',
        fired_at = NULL,
        lease_owner = NULL,
        lease_until = NULL,
        updated_at = STATEMENT_TIMESTAMP()
    WHERE
        fsnix.shipment_tracking.status = 'pending'
