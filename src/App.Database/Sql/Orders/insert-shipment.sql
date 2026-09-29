INSERT INTO fsnix.shipments (shipment_id, allocation_id, order_id)
    VALUES (@shipment, @allocation, @order)
ON CONFLICT (shipment_id)
    DO NOTHING
