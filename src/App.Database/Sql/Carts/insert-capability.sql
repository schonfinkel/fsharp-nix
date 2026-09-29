INSERT INTO fsnix.cart_guest_capabilities (locate_digest, key_id, purpose, entity_id, expires_at)
    VALUES (@locate_digest, @key_id, @purpose, @entity_id, @expires_at)
ON CONFLICT (locate_digest)
    DO NOTHING
