INSERT INTO fsnix.payment_operations (operation_id, payment_entity_id, order_id, kind)
    VALUES (@operation, @entity, @order, @kind)
ON CONFLICT (operation_id)
    DO NOTHING
