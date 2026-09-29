INSERT INTO fsnix.refund_allocations (allocation_id, refund_id, order_id, amount, currency, status)
    VALUES (@allocation, @refund, @order, @amount, @currency, 'pending')
ON CONFLICT (allocation_id)
    DO NOTHING
