CREATE OR REPLACE VIEW fsnix.current_feature_flag_definitions AS
SELECT
    feature_name AS name,
    enabled,
    LOWER(valid_during) AS valid_from,
    NULLIF (UPPER(valid_during), 'infinity'::timestamptz) AS valid_to,
    recorded_at
FROM
    fsnix.feature_flag_intervals
WHERE
    valid_during @> CURRENT_TIMESTAMP;

CREATE OR REPLACE VIEW fsnix.feature_flag_history AS
SELECT
    feature_name AS name,
    enabled,
    LOWER(valid_during) AS valid_from,
    NULLIF (UPPER(valid_during), 'infinity'::timestamptz) AS valid_to,
    recorded_at
FROM
    fsnix.feature_flag_intervals;

CREATE OR REPLACE VIEW fsnix.next_feature_flag_boundary AS
SELECT
    CURRENT_TIMESTAMP AS observed_at,
    MIN(LOWER(valid_during)) AS next_boundary
FROM
    fsnix.feature_flag_intervals
WHERE
    LOWER(valid_during) > CURRENT_TIMESTAMP;

