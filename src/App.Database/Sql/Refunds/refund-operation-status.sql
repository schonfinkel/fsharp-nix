SELECT
    status,
    provider_reference,
    result_code
FROM
    fsnix.payment_operations
WHERE
    operation_id = @id
