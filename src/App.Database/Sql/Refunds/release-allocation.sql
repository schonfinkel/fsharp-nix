UPDATE
    fsnix.refund_allocations
SET
    status = 'released',
    updated_at = STATEMENT_TIMESTAMP()
WHERE
    allocation_id = @id
    AND status = 'pending'
