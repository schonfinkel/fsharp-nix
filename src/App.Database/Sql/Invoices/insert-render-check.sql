INSERT INTO fsnix.invoice_render_checks (invoice_id, due_at)
    VALUES (@invoice, @due)
ON CONFLICT (invoice_id)
    DO NOTHING
