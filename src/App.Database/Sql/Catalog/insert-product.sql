INSERT INTO fsnix.products (product_id, sku, name, description, price_amount, price_currency, price_version, active)
    VALUES (@product_id, @sku, @name, @description, @price_amount, @price_currency, 1, TRUE)
