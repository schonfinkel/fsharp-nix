SELECT
    payment_entity_id,
    order_id,
    kind,
    status,
    provider_reference,
    result_code,
    expires_at
FROM
    fsnix.payment_operations
WHERE
    operation_id = @operation
