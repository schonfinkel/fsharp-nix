SELECT
    entity_id
FROM
    fsnix.cart_guest_capabilities
WHERE
    locate_digest = @locate_digest
    AND key_id = @key_id
    AND purpose = @purpose
    AND NOT revoked
    AND expires_at > STATEMENT_TIMESTAMP()
