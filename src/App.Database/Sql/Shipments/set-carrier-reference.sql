UPDATE
    fsnix.shipments
SET
    carrier_reference = @reference
WHERE
    shipment_id = @shipment
    AND carrier_reference IS NULL
