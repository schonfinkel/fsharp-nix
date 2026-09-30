INSERT INTO fsnix.action_receipts (machine_id, command_id, ordinal, action_kind, payload_hash)
    VALUES (@machine, @command, @ordinal, @kind, @hash)
ON CONFLICT
    DO NOTHING
RETURNING
    command_id
