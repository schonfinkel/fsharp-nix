CREATE SCHEMA IF NOT EXISTS fsnix;

CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

CREATE TABLE IF NOT EXISTS fsnix.repeatable_migration_state (
    script_name text PRIMARY KEY,
    content_hash text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
