CREATE TABLE fsnix.feature_flags (
    name text PRIMARY KEY,
    CONSTRAINT feature_flags_name_not_empty CHECK (LENGTH(TRIM(name)) > 0)
);

CREATE TABLE fsnix.feature_flag_intervals (
    feature_name text NOT NULL REFERENCES fsnix.feature_flags (name) ON DELETE CASCADE,
    enabled boolean NOT NULL,
    valid_during tstzrange NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT feature_flag_intervals_valid_range CHECK (NOT ISEMPTY(valid_during)),
    CONSTRAINT feature_flag_intervals_pkey PRIMARY KEY (feature_name, valid_during WITHOUT OVERLAPS)
);

INSERT INTO fsnix.feature_flags (name)
VALUES
    ('NewDashboard'),
    ('BetaCheckout');

WITH migration_time AS (
    SELECT
        CURRENT_TIMESTAMP AS value)
INSERT INTO fsnix.feature_flag_intervals (feature_name, enabled, valid_during)
SELECT
    name,
    enabled,
    TSTZRANGE(migration_time.value, NULL, '[)')
FROM
    migration_time
    CROSS JOIN (
        VALUES ('NewDashboard', FALSE),
            ('BetaCheckout', FALSE)) AS seeds (name, enabled);

