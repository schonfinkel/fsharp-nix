SELECT
    action_kind,
    payload_hash
FROM
    fsnix.action_receipts
WHERE
    machine_id = @machine
    AND command_id = @command
    AND ordinal = @ordinal
