CREATE OR REPLACE VIEW fsnix.current_feature_flag_definitions AS
SELECT
    name,
    enabled,
    NULLIF (filter_name, '') AS filter_name,
    CASE WHEN filter_parameters = '{}'::jsonb THEN
        NULL
    ELSE
        filter_parameters::text
    END AS filter_parameters,
    LOWER(valid_during) AS valid_from,
    NULLIF (UPPER(valid_during), 'infinity'::timestamptz) AS valid_to,
    recorded_at
FROM
    fsnix.feature_flags
WHERE
    valid_during @> CURRENT_TIMESTAMP;

CREATE OR REPLACE VIEW fsnix.feature_flag_history AS
SELECT
    name,
    enabled,
    NULLIF (filter_name, '') AS filter_name,
    CASE WHEN filter_parameters = '{}'::jsonb THEN
        NULL
    ELSE
        filter_parameters::text
    END AS filter_parameters,
    LOWER(valid_during) AS valid_from,
    NULLIF (UPPER(valid_during), 'infinity'::timestamptz) AS valid_to,
    recorded_at
FROM
    fsnix.feature_flags;
