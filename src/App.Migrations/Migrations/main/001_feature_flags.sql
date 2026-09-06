CREATE TABLE feature_flags (
    name text NOT NULL,
    enabled boolean NOT NULL DEFAULT FALSE,
    filter_name text NOT NULL DEFAULT '',
    filter_parameters jsonb NOT NULL DEFAULT '{}'::jsonb,
    valid_during tstzrange NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT feature_flags_valid_range CHECK (NOT ISEMPTY(valid_during)),
    CONSTRAINT feature_flags_pkey PRIMARY KEY (name, valid_during WITHOUT OVERLAPS)
);

CREATE INDEX feature_flags_name_idx ON feature_flags (name);

WITH migration_time AS (
    SELECT
        CURRENT_TIMESTAMP AS value)
INSERT INTO feature_flags (name, enabled, filter_name, filter_parameters, valid_during)
SELECT
    name,
    enabled,
    '',
    '{}'::jsonb,
    TSTZRANGE(migration_time.value, 'infinity'::timestamptz, '[)')
FROM
    migration_time
    CROSS JOIN (
        VALUES ('NewDashboard', FALSE),
            ('BetaCheckout', FALSE)) AS seeds (name, enabled);

