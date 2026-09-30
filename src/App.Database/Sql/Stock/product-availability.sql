SELECT
    p.active,
    p.price_version,
    s.on_hand - s.reserved
FROM
    fsnix.products p
    JOIN fsnix.product_stock s USING (product_id)
WHERE
    p.product_id = @id
