UPDATE
    fsnix.cart_guest_capabilities
SET
    revoked = TRUE,
    revoked_at = STATEMENT_TIMESTAMP()
WHERE
    entity_id = @entity_id
    AND NOT revoked
