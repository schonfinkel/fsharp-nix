INSERT INTO fsnix.cart_deadlines (cart_id, timer_kind, generation, deadline, gate_callback_key)
    VALUES (@cart_id, 'cart-abandonment', @generation, @deadline, @gate)
ON CONFLICT (cart_id, timer_kind, generation)
    DO NOTHING
