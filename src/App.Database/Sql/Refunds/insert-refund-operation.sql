INSERT INTO fsnix.payment_operations (operation_id, payment_entity_id, order_id, kind, amount, currency)
    VALUES (@id, @entity, @order, 'refund', @amount, @currency)
ON CONFLICT (operation_id)
    DO NOTHING
