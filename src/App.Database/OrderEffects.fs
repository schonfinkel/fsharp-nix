namespace App.Database

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Cart
open App.Domain
open App.Orders
open App.Payments
open App.Shipments
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Npgsql

[<RequireQualifiedAccess>]
module OrderEffects =
    let actionKind =
        function
        | ReserveStock _ -> "reserve-stock"
        | ReleaseReservations _ -> "release-reservations"
        | NotifyCartConverted _ -> "notify-cart-converted"
        | RequestAuthorization _ -> "request-authorization"
        | RequestPaymentCancellation _ -> "request-payment-cancellation"
        | CommitStock _ -> "commit-stock"
        | CreateShipment _ -> "create-shipment"
        | RequestCapture _ -> "request-capture"

    let private key (record: ActionRecord<OrderId, OrderAction>) purpose =
        $"xmsg:v1:{MachineId.value record.MachineId}:{CommandId.value record.CommandId}:{record.Ordinal}:{purpose}"

    let private paymentEntityOfOrder (orderEntity: string) =
        let prefix = "order:"

        if not (orderEntity.StartsWith(prefix, StringComparison.Ordinal)) then
            invalidOp "an order entity id must start with 'order:'"
        else
            "payment:" + orderEntity[prefix.Length ..]

    let private receipt
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (record: ActionRecord<OrderId, OrderAction>)
        (ct: CancellationToken)
        =
        task {
            let json =
                OrderCodec.action.Encode record.Action
                |> Result.defaultWith (fun _ -> "invalid")

            let hash = SHA256.HashData(Encoding.UTF8.GetBytes json)

            use insert =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.action_receipts(machine_id,command_id,ordinal,action_kind,payload_hash) VALUES(@machine,@command,@ordinal,@kind,@hash) ON CONFLICT DO NOTHING RETURNING command_id",
                    connection,
                    tx
                )

            insert.Parameters.AddWithValue("machine", MachineId.value record.MachineId)
            |> ignore

            insert.Parameters.AddWithValue("command", CommandId.value record.CommandId)
            |> ignore

            insert.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore
            insert.Parameters.AddWithValue("kind", actionKind record.Action) |> ignore
            insert.Parameters.AddWithValue("hash", hash) |> ignore
            let! inserted = insert.ExecuteScalarAsync ct

            if not (isNull inserted) then
                return Ok true
            else
                use verify =
                    new NpgsqlCommand(
                        "SELECT action_kind,payload_hash FROM fsnix.action_receipts WHERE machine_id=@machine AND command_id=@command AND ordinal=@ordinal",
                        connection,
                        tx
                    )

                verify.Parameters.AddWithValue("machine", MachineId.value record.MachineId)
                |> ignore

                verify.Parameters.AddWithValue("command", CommandId.value record.CommandId)
                |> ignore

                verify.Parameters.AddWithValue("ordinal", record.Ordinal) |> ignore
                let! reader = verify.ExecuteReaderAsync ct
                let! found = reader.ReadAsync ct

                let matches =
                    found
                    && reader.GetString 0 = actionKind record.Action
                    && CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte array>(1), hash)

                reader.Dispose()

                return
                    if matches then
                        Ok false
                    else
                        Error OrderActionError.ActionReceiptMismatch
        }

    let private callback
        (connection: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        callbackKey
        machine
        entity
        eventJson
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO fsnix.integration_outbox(callback_key,machine_id,entity_id,event) VALUES(@key,@machine,@entity,@event::jsonb) ON CONFLICT(callback_key) DO NOTHING",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("key", callbackKey) |> ignore
            command.Parameters.AddWithValue("machine", machine) |> ignore
            command.Parameters.AddWithValue("entity", entity) |> ignore
            command.Parameters.AddWithValue("event", eventJson) |> ignore
            let! _ = command.ExecuteNonQueryAsync ct
            return ()
        }

    let applyReserveStock
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<OrderId, OrderAction>)
        expiry
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok false ->
                do! tx.CommitAsync ct
                return Ok()
            | Ok true ->
                match record.Action with
                | ReserveStock order ->
                    let orderEntity = EntityId.value record.EntityId

                    let result =
                        task {
                            try
                                return!
                                    StockReservations.reserve
                                        connection
                                        tx
                                        orderEntity
                                        order.Generation
                                        expiry
                                        (MachineId.value record.MachineId)
                                        (CommandId.value record.CommandId)
                                        record.Ordinal
                                        order.Lines
                                        ct
                            with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UniqueViolation ->
                                return Error ReservationFailure.InvalidReservation
                        }

                    let! result = result

                    let event =
                        match result with
                        | Ok ids ->
                            StockReserved(
                                ids |> List.map (ReservationId.create >> Result.defaultWith invalidOp),
                                order.Generation
                            )
                        | Error failure -> StockReservationFailed failure

                    let! json =
                        OrderCodec.event.Encode event
                        |> function
                            | Ok json -> Task.FromResult(Ok json)
                            | Error _ -> Task.FromResult(Error OrderActionError.CallbackEncodingFailed)

                    match json with
                    | Error err ->
                        do! tx.RollbackAsync ct
                        return Error err
                    | Ok eventJson ->
                        let callbackKey = key record "stock-reservation-result"
                        do! callback connection tx callbackKey Orders.MachineKey orderEntity eventJson ct

                        match result with
                        | Ok _ ->
                            use deadline =
                                new NpgsqlCommand(
                                    "INSERT INTO fsnix.reservation_deadlines(order_id,generation,deadline,gate_callback_key) VALUES(@order,@generation,@deadline,@gate) ON CONFLICT DO NOTHING",
                                    connection,
                                    tx
                                )

                            deadline.Parameters.AddWithValue("order", orderEntity) |> ignore
                            deadline.Parameters.AddWithValue("generation", order.Generation) |> ignore
                            deadline.Parameters.AddWithValue("deadline", expiry) |> ignore
                            deadline.Parameters.AddWithValue("gate", callbackKey) |> ignore
                            let! _ = deadline.ExecuteNonQueryAsync ct
                            do! tx.CommitAsync ct
                            return Ok()
                        | Error _ ->
                            do! tx.CommitAsync ct
                            return Ok()
                | _ ->
                    do! tx.RollbackAsync ct
                    return Error OrderActionError.InvalidAction
        }

    let applyRelease
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<OrderId, OrderAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok false ->
                do! tx.CommitAsync ct
                return Ok()
            | Ok true ->
                match record.Action with
                | ReleaseReservations _ ->
                    let entity = EntityId.value record.EntityId
                    do! StockReservations.release connection tx entity ct

                    use cancel =
                        new NpgsqlCommand(
                            "UPDATE fsnix.reservation_deadlines SET status='cancelled',lease_owner=NULL,lease_until=NULL WHERE order_id=@order AND status='pending'",
                            connection,
                            tx
                        )

                    cancel.Parameters.AddWithValue("order", entity) |> ignore
                    let! _ = cancel.ExecuteNonQueryAsync ct

                    match OrderCodec.event.Encode ReservationsReleased with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error OrderActionError.CallbackEncodingFailed
                    | Ok eventJson ->
                        do!
                            callback
                                connection
                                tx
                                (key record "reservations-released")
                                Orders.MachineKey
                                entity
                                eventJson
                                ct

                        do! tx.CommitAsync ct
                        return Ok()
                | _ ->
                    do! tx.RollbackAsync ct
                    return Error OrderActionError.InvalidAction
        }

    let applyConvertCart
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<OrderId, OrderAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok _ ->
                match record.Action with
                | NotifyCartConverted cartId ->
                    let eventJson =
                        CartCodec.event.Encode(CartConverted(EntityId.value record.EntityId))

                    match eventJson with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error OrderActionError.CallbackEncodingFailed
                    | Ok eventJson ->
                        do! callback connection tx (key record "cart-converted") Cart.MachineKey cartId eventJson ct
                        do! tx.CommitAsync ct
                        return Ok()
                | _ ->
                    do! tx.RollbackAsync ct
                    return Error OrderActionError.InvalidAction
        }

    /// <summary>Hands one authorization attempt to the payments machine through the integration
    /// outbox. The authorization intent is claimed only while the reservation controls are open;
    /// a fenced (already cancelled) handoff instead settles the payment leg immediately so the
    /// order's cancellation can never strand waiting for a payment that will never exist.</summary>
    let applyRequestAuthorization
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<OrderId, OrderAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok _ ->
                match record.Action with
                | RequestAuthorization(method, attempt, amount) ->
                    let orderEntity = EntityId.value record.EntityId

                    use claim =
                        new NpgsqlCommand(
                            """INSERT INTO fsnix.authorization_intents(order_id,amount,currency,status)
                               SELECT @order,@amount,@currency,'claimed'
                               WHERE EXISTS (SELECT 1 FROM fsnix.order_reservation_controls WHERE order_id=@order AND status='open')
                               ON CONFLICT(order_id) DO UPDATE SET status='claimed', updated_at=statement_timestamp()
                                   WHERE fsnix.authorization_intents.status='pending'""",
                            connection,
                            tx
                        )

                    claim.Parameters.AddWithValue("order", orderEntity) |> ignore
                    claim.Parameters.AddWithValue("amount", Money.amount amount) |> ignore
                    claim.Parameters.AddWithValue("currency", Money.currencyCode amount) |> ignore
                    let! _ = claim.ExecuteNonQueryAsync ct

                    use read =
                        new NpgsqlCommand(
                            "SELECT status FROM fsnix.authorization_intents WHERE order_id=@order",
                            connection,
                            tx
                        )

                    read.Parameters.AddWithValue("order", orderEntity) |> ignore
                    let! status = read.ExecuteScalarAsync ct

                    if status :?> string = "claimed" then
                        let paymentAttempt =
                            { OperationId = attempt
                              OrderId = orderEntity
                              Amount = amount
                              Method = method }

                        match PaymentCodec.event.Encode(AuthorizeRequested paymentAttempt) with
                        | Error _ ->
                            do! tx.RollbackAsync ct
                            return Error OrderActionError.CallbackEncodingFailed
                        | Ok eventJson ->
                            do!
                                callback
                                    connection
                                    tx
                                    (key record "authorize-request")
                                    Payments.MachineKey
                                    (paymentEntityOfOrder orderEntity)
                                    eventJson
                                    ct

                            do! tx.CommitAsync ct
                            return Ok()
                    else
                        match OrderCodec.event.Encode PaymentSettled with
                        | Error _ ->
                            do! tx.RollbackAsync ct
                            return Error OrderActionError.CallbackEncodingFailed
                        | Ok eventJson ->
                            do!
                                callback
                                    connection
                                    tx
                                    (key record "payment-fenced")
                                    Orders.MachineKey
                                    orderEntity
                                    eventJson
                                    ct

                            do! tx.CommitAsync ct
                            return Ok()
                | _ ->
                    do! tx.RollbackAsync ct
                    return Error OrderActionError.InvalidAction
        }

    /// <summary>Forwards an order cancellation to its payment entity and cancels any open
    /// authorization intent, so a later authorize action cannot pass the fence.</summary>
    let applyRequestPaymentCancellation
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<OrderId, OrderAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok _ ->
                match record.Action with
                | RequestPaymentCancellation reason ->
                    let orderEntity = EntityId.value record.EntityId

                    use fence =
                        new NpgsqlCommand(
                            "INSERT INTO fsnix.order_reservation_controls(order_id,generation,status) VALUES(@order,1,'cancelled') ON CONFLICT(order_id) DO UPDATE SET status='cancelled',updated_at=statement_timestamp()",
                            connection,
                            tx
                        )

                    fence.Parameters.AddWithValue("order", orderEntity) |> ignore
                    let! _ = fence.ExecuteNonQueryAsync ct

                    use intent =
                        new NpgsqlCommand(
                            "UPDATE fsnix.authorization_intents SET status='cancelled' WHERE order_id=@order AND status IN ('pending','claimed')",
                            connection,
                            tx
                        )

                    intent.Parameters.AddWithValue("order", orderEntity) |> ignore
                    let! _ = intent.ExecuteNonQueryAsync ct

                    match PaymentCodec.event.Encode(PaymentCancellationRequested(orderEntity, reason)) with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error OrderActionError.CallbackEncodingFailed
                    | Ok eventJson ->
                        do!
                            callback
                                connection
                                tx
                                (key record "payment-cancel-request")
                                Payments.MachineKey
                                (paymentEntityOfOrder orderEntity)
                                eventJson
                                ct

                        do! tx.CommitAsync ct
                        return Ok()
                | _ ->
                    do! tx.RollbackAsync ct
                    return Error OrderActionError.InvalidAction
        }

    /// <summary>Converts reserved ledger rows into committed stock inside one transaction with
    /// the receipt, deadline cancellation, intent completion, and self-callback. Quantities
    /// derive from the ledger, never from the action payload.</summary>
    let applyCommitStock
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<OrderId, OrderAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok _ ->
                match record.Action with
                | CommitStock _ ->
                    let entity = EntityId.value record.EntityId
                    let! committed = StockReservations.commit connection tx entity ct

                    use cancelDeadline =
                        new NpgsqlCommand(
                            "UPDATE fsnix.reservation_deadlines SET status='cancelled',lease_owner=NULL,lease_until=NULL WHERE order_id=@order AND status='pending'",
                            connection,
                            tx
                        )

                    cancelDeadline.Parameters.AddWithValue("order", entity) |> ignore
                    let! _ = cancelDeadline.ExecuteNonQueryAsync ct

                    let event =
                        if committed > 0 then
                            StockCommitted
                        else
                            StockCommitFailed "no-open-reservations"

                    match OrderCodec.event.Encode event with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error OrderActionError.CallbackEncodingFailed
                    | Ok eventJson ->
                        if committed > 0 then
                            use complete =
                                new NpgsqlCommand(
                                    "UPDATE fsnix.authorization_intents SET status='completed' WHERE order_id=@order AND status='claimed'",
                                    connection,
                                    tx
                                )

                            complete.Parameters.AddWithValue("order", entity) |> ignore
                            let! _ = complete.ExecuteNonQueryAsync ct
                            ()

                        do!
                            callback
                                connection
                                tx
                                (key record "stock-commit-result")
                                Orders.MachineKey
                                entity
                                eventJson
                                ct

                        do! tx.CommitAsync ct
                        return Ok()
                | _ ->
                    do! tx.RollbackAsync ct
                    return Error OrderActionError.InvalidAction
        }

    /// <summary>Persists the shipment-allocation ledger row, hands the shipment request to the
    /// shipments machine, and marks the order's shipment created in one transaction.</summary>
    let applyCreateShipment
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<OrderId, OrderAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok _ ->
                match record.Action with
                | CreateShipment(shipment, addressSnapshotId) ->
                    let orderEntity = EntityId.value record.EntityId

                    use insert =
                        new NpgsqlCommand(
                            "INSERT INTO fsnix.shipments(shipment_id,allocation_id,order_id) VALUES(@shipment,@allocation,@order) ON CONFLICT(shipment_id) DO NOTHING",
                            connection,
                            tx
                        )

                    insert.Parameters.AddWithValue("shipment", ShipmentId.value shipment.ShipmentId)
                    |> ignore

                    insert.Parameters.AddWithValue(
                        "allocation",
                        ShipmentAllocationId.value shipment.Allocation.AllocationId
                    )
                    |> ignore

                    insert.Parameters.AddWithValue("order", orderEntity) |> ignore
                    let! _ = insert.ExecuteNonQueryAsync ct

                    let shipmentRequest =
                        { ShipmentId = shipment.ShipmentId
                          AllocationId = shipment.Allocation.AllocationId
                          OrderId = orderEntity
                          Lines =
                            shipment.Allocation.Lines
                            |> List.map (fun line ->
                                { LineId = line.LineId
                                  Quantity = line.Quantity }) }

                    match ShipmentCodec.event.Encode(ShipmentRequested shipmentRequest) with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error OrderActionError.CallbackEncodingFailed
                    | Ok shipmentJson ->
                        let shipmentEntity = EntityId.value (Shipments.shipmentEntityId shipment.ShipmentId)

                        do!
                            callback
                                connection
                                tx
                                (key record "shipment-requested")
                                Shipments.MachineKey
                                shipmentEntity
                                shipmentJson
                                ct

                        match
                            OrderCodec.event.Encode(
                                ShipmentCreated(shipment.ShipmentId, shipment.Allocation.AllocationId)
                            )
                        with
                        | Error _ ->
                            do! tx.RollbackAsync ct
                            return Error OrderActionError.CallbackEncodingFailed
                        | Ok createdJson ->
                            do!
                                callback
                                    connection
                                    tx
                                    (key record "shipment-created")
                                    Orders.MachineKey
                                    orderEntity
                                    createdJson
                                    ct

                            do! tx.CommitAsync ct
                            return Ok()
                | _ ->
                    do! tx.RollbackAsync ct
                    return Error OrderActionError.InvalidAction
        }

    /// <summary>Forwards a dispatch-triggered capture request to the payments machine.</summary>
    let applyRequestCapture
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<OrderId, OrderAction>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct

            match! receipt connection tx record ct with
            | Error error ->
                do! tx.RollbackAsync ct
                return Error error
            | Ok _ ->
                match record.Action with
                | RequestCapture(shipmentId, allocation, captureId, operationId, providerReference) ->
                    let orderEntity = EntityId.value record.EntityId

                    let captureRequest =
                        { OrderId = orderEntity
                          CaptureId = captureId
                          OperationId = operationId
                          Amount = allocation.Total }

                    match PaymentCodec.event.Encode(CaptureRequested captureRequest) with
                    | Error _ ->
                        do! tx.RollbackAsync ct
                        return Error OrderActionError.CallbackEncodingFailed
                    | Ok eventJson ->
                        do!
                            callback
                                connection
                                tx
                                (key record "capture-requested")
                                Payments.MachineKey
                                (paymentEntityOfOrder orderEntity)
                                eventJson
                                ct

                        do! tx.CommitAsync ct
                        return Ok()
                | _ ->
                    do! tx.RollbackAsync ct
                    return Error OrderActionError.InvalidAction
        }
