SELECT
    p.product_id,
    p.sku,
    p.name,
    p.description,
    p.price_amount,
    p.price_currency,
    p.price_version,
    p.active,
    COALESCE(s.on_hand, 0)
FROM
    fsnix.products p
    LEFT JOIN fsnix.product_stock s ON s.product_id = p.product_id
WHERE
    p.active = @active
ORDER BY
    p.name
