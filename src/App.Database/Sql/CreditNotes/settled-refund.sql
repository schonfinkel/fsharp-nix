SELECT
    amount,
    currency
FROM
    fsnix.refund_allocations
WHERE
    refund_id = @refund
    AND order_id = @order
    AND status = 'settled'
