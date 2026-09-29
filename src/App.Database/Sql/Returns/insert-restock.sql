INSERT INTO fsnix.return_restock (return_id, order_line_id, quantity, source_command_id)
SELECT
    @return,
    @line,
    @quantity,
    @command
WHERE
    EXISTS (
        SELECT
            1
        FROM
            fsnix.return_lines
        WHERE
            return_id = @return
            AND order_line_id = @line
            AND quantity >= @quantity)
ON CONFLICT
    DO NOTHING
RETURNING
    quantity
