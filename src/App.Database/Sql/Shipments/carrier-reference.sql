SELECT
    carrier_reference
FROM
    fsnix.shipments
WHERE
    shipment_id = @shipment
