SELECT
    payment_entity_id,
    order_id,
    kind,
    amount,
    currency,
    status,
    provider_reference,
    result_code
FROM
    fsnix.payment_operations
WHERE
    operation_id = @id
