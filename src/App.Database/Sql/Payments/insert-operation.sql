INSERT INTO fsnix.payment_operations (operation_id, payment_entity_id, order_id, kind, reconcile_machine, reconcile_entity)
    VALUES (@operation, @entity, @order, @kind, 'payments', @entity)
ON CONFLICT (operation_id)
    DO NOTHING
