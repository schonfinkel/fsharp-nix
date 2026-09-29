SELECT
    status,
    generation
FROM
    fsnix.order_reservation_controls
WHERE
    order_id = @order
FOR UPDATE
