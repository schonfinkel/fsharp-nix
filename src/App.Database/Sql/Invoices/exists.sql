SELECT
    EXISTS (
        SELECT
            1
        FROM
            fsnix.invoices
        WHERE
            invoice_id = @invoice
            AND (@customer IS NULL
                OR customer_id = @customer))
