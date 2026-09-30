UPDATE
    fsnix.shipments
SET
    delivered_at = COALESCE(delivered_at, @delivered)
WHERE
    shipment_id = @shipment
