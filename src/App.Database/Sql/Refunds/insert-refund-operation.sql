INSERT INTO fsnix.payment_operations (operation_id, payment_entity_id, order_id, kind, amount, currency, reconcile_machine, reconcile_entity)
    VALUES (@id, @entity, @order, 'refund', @amount, @currency, 'refunds', @refund_entity)
ON CONFLICT (operation_id)
    DO NOTHING
