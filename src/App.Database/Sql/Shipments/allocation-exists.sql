SELECT
    EXISTS (
        SELECT
            1
        FROM
            fsnix.shipments
        WHERE
            allocation_id = @allocation)
