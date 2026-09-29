UPDATE
    fsnix.refund_allocations
SET
    status = 'settled',
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    allocation_id = @id
    AND status = 'pending'
