-- TB1
--   Employee (id, name, adress+)
-- TB2
--   Company ()
-- TB3
--   Departments ()
-- An EMPLOYEE is part of a DEPARTMENT, which is part of a
-- COMPANY. Each employee has 0 or more children.
CREATE TABLE employer.address (
    id int PRIMARY KEY,
    line1 text NOT NULL,
    line2 text NOT NULL DEFAULT '',
    line3 text NOT NULL DEFAULT '',
    country text NOT NULL,
    state text NOT NULL,
)
CREATE TABLE employer.children (
    id int PRIMARY KEY,
    name text NOT NULL,
)
CREATE TABLE employer.employee (
    id int PRIMARY KEY,
    name text NOT NULL,
    address_id FOREIGN KEY e.adress,
)
