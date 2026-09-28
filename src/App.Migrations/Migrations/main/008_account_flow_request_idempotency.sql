-- Idempotency for existing-user account-flow requests. Hashes are scoped to a user and flow
-- kind; the raw idempotency key is never persisted. The request fingerprint binds a key to
-- its canonical inputs, so accidental key reuse with a different destination is a conflict.
ALTER TABLE fsnix.account_flow_requests
    ADD COLUMN idempotency_key_hash bytea,
    ADD COLUMN request_hash bytea,
    ADD CONSTRAINT ck_account_flow_requests_idempotency_pair CHECK ((idempotency_key_hash IS NULL) = (request_hash IS NULL)),
    ADD CONSTRAINT ck_account_flow_requests_idempotency_hashes_sha256 CHECK (idempotency_key_hash IS NULL OR (OCTET_LENGTH(idempotency_key_hash) = 32 AND OCTET_LENGTH(request_hash) = 32));

CREATE UNIQUE INDEX ux_account_flow_requests_idempotency ON fsnix.account_flow_requests (user_id, flow_kind, idempotency_key_hash)
WHERE
    idempotency_key_hash IS NOT NULL;

