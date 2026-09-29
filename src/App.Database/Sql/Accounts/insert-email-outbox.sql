INSERT INTO fsnix.account_email_outbox (flow_id, generation, protected_payload, encryption_version)
    VALUES (@flow_id, @generation, @protected_payload, @encryption_version)
ON CONFLICT (flow_id, generation)
    DO NOTHING
