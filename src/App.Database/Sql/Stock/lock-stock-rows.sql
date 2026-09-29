SELECT
    product_id
FROM
    fsnix.product_stock
WHERE
    product_id = ANY (@ids)
ORDER BY
    product_id
FOR UPDATE
