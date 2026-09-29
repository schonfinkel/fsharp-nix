INSERT INTO fsnix.invoice_documents (invoice_id, sha256, renderer, content, size)
    VALUES (@invoice, @sha256, @renderer, @content, @size)
ON CONFLICT (invoice_id, sha256)
    DO NOTHING
