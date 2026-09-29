SELECT
    EXISTS (
        SELECT
        FROM
            information_schema.schemata
        WHERE
            schema_name = 'fsm')
