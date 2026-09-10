CREATE TABLE fsnix.development_migration_marker (
    id integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Development-only credentials for manual testing:
-- email: test.operator@example.test
-- password: Test-Operator-42!
INSERT INTO fsnix.users (
    id,
    username,
    normalized_username,
    email,
    normalized_email,
    email_confirmed,
    password_hash,
    security_stamp,
    concurrency_stamp
)
VALUES (
    '10000000-0000-0000-0000-000000000001',
    'test-operator',
    'TEST-OPERATOR',
    'test.operator@example.test',
    'TEST.OPERATOR@EXAMPLE.TEST',
    TRUE,
    'AQAAAAEAAYagAAAAEMoYqa1WkeGpOvW4HJDH1tdTStebCy7kT1nExUm64FaEi8kJttez9rCwS/lgnNHW8w==',
    'development-test-user-security-stamp',
    'development-test-user-concurrency-stamp'
);
