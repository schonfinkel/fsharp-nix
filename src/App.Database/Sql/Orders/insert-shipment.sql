INSERT INTO fsnix.shipments (shipment_id, allocation_id, order_id, capture_id, merchandise, shipping, tax, total, currency, lines)
    VALUES (@shipment, @allocation, @order, @capture, @merchandise, @shipping, @tax, @total, @currency, @lines::jsonb)
ON CONFLICT (shipment_id)
    DO NOTHING
