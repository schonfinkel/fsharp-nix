-- Development-only catalog seed (P2). Deterministic product ids so database and HTTP tests
-- can reference stable fixtures. Production seeds nothing: the operator catalog CRUD is the
-- source of truth there.
INSERT INTO fsnix.products (product_id, sku, name, description, price_amount, price_currency, price_version, active)
VALUES
    ('00000000-0000-0000-0000-000000000001', 'COFFEE-ESPRESSO', 'Espresso Beans', 'Single-origin espresso roast, 1 kg bag.', 24.90, 'USD', 1, TRUE),
    ('00000000-0000-0000-0000-000000000002', 'COFFEE-FILTER', 'Filter Roast', 'Medium filter roast, 1 kg bag.', 18.50, 'USD', 1, TRUE),
    ('00000000-0000-0000-0000-000000000003', 'BREWER-DRIP', 'Drip Coffee Brewer', 'Programmable 12-cup drip brewer.', 89.00, 'USD', 1, TRUE),
    ('00000000-0000-0000-0000-000000000004', 'GRINDER-BURR', 'Burr Grinder', 'Conical burr grinder with 40 settings.', 149.00, 'USD', 1, TRUE),
    ('00000000-0000-0000-0000-000000000005', 'CUP-CERAMIC', 'Ceramic Mug', 'Hand-glazed 350 ml ceramic mug.', 12.00, 'USD', 1, TRUE);

INSERT INTO fsnix.product_stock (product_id, on_hand, reserved)
VALUES
    ('00000000-0000-0000-0000-000000000001', 200, 0),
    ('00000000-0000-0000-0000-000000000002', 150, 0),
    ('00000000-0000-0000-0000-000000000003', 30, 0),
    ('00000000-0000-0000-0000-000000000004', 25, 0),
    ('00000000-0000-0000-0000-000000000005', 500, 0);

