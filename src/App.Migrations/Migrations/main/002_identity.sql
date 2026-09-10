CREATE TABLE fsnix.users (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid (),
    username varchar(256) NOT NULL,
    normalized_username varchar(256) NOT NULL,
    email varchar(256) NOT NULL,
    normalized_email varchar(256) NOT NULL,
    email_confirmed boolean NOT NULL DEFAULT FALSE,
    password_hash text,
    two_factor_enabled boolean NOT NULL DEFAULT FALSE,
    security_stamp text NOT NULL,
    concurrency_stamp text NOT NULL,
    lockout_end timestamptz,
    lockout_enabled boolean NOT NULL DEFAULT TRUE,
    access_failed_count integer NOT NULL DEFAULT 0,
    CONSTRAINT uq_users_normalized_username UNIQUE (normalized_username),
    CONSTRAINT uq_users_normalized_email UNIQUE (normalized_email),
    CONSTRAINT ck_users_username_not_empty CHECK (LENGTH(TRIM(username)) > 0),
    CONSTRAINT ck_users_normalized_username_not_empty CHECK (LENGTH(TRIM(normalized_username)) > 0),
    CONSTRAINT ck_users_email_not_empty CHECK (LENGTH(TRIM(email)) > 0),
    CONSTRAINT ck_users_normalized_email_not_empty CHECK (LENGTH(TRIM(normalized_email)) > 0),
    CONSTRAINT ck_users_security_stamp_not_empty CHECK (LENGTH(TRIM(security_stamp)) > 0),
    CONSTRAINT ck_users_concurrency_stamp_not_empty CHECK (LENGTH(TRIM(concurrency_stamp)) > 0),
    CONSTRAINT ck_users_access_failed_count CHECK (access_failed_count >= 0)
);

CREATE TABLE fsnix.user_tokens (
    user_id uuid NOT NULL REFERENCES fsnix.users (id) ON DELETE CASCADE,
    login_provider varchar(128) NOT NULL,
    name varchar(128) NOT NULL,
    value text NOT NULL DEFAULT '',
    PRIMARY KEY (user_id, login_provider, name)
);

