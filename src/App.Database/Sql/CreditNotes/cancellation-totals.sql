-- A cancellation refund reverses one shipment's capture: its exact shipping and tax.
SELECT
    shipping,
    tax,
    currency
FROM
    fsnix.shipments
WHERE
    capture_id = @refund
    AND order_id = @order
