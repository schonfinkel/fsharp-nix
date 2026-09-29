SELECT
    refund_id,
    order_id,
    amount,
    currency,
    status
FROM
    fsnix.refund_allocations
WHERE
    allocation_id = @allocation
