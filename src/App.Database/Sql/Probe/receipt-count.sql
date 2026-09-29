SELECT
    COUNT(*)
FROM
    fsnix.action_receipts
WHERE
    machine_id = @machine_id
