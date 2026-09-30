namespace App

open System
open System.Threading.Tasks
open App.Database
open App.Domain
open App.Orders
open App.Shipments
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Oxpecker

/// <summary>
/// Operator fulfilment controls. The sandbox drives dispatch and tracking through the carrier
/// boundary by sending the corresponding events into the shipments machine, using the shipment
/// ids the order's fulfilment plan already owns. Every command result is mapped through
/// <see cref="HttpErrors"/>; nothing is silently dropped.
/// </summary>
[<RequireQualifiedAccess>]
module AdminFulfilment =
    let private parseOrderId (context: HttpContext) =
        match context.TryGetRouteValue("orderId") with
        | Some text ->
            match Guid.TryParseExact(text, "D") with
            | true, guid -> Some guid
            | _ -> None
        | None -> None

    let private orderNotFound = HttpErrors.notFound "Order not found."

    let private redirect guid (context: HttpContext) : Task =
        Web.redirect $"/orders/{guid:D}" context :> Task

    /// <summary>Loads the order for an operator action and hands its state to
    /// <paramref name="handle"/>; unknown or unreadable orders are 404.</summary>
    let private withOrder (handle: Guid -> OrderId -> OrderState -> HttpContext -> Task) : EndpointHandler =
        fun context ->
            task {
                match parseOrderId context with
                | None -> return! orderNotFound context
                | Some guid ->
                    let orders = context.GetService<OrderMachineClient>()
                    let orderId = Orders.orderId guid

                    match! Machine.state orders.Orders orderId context.RequestAborted with
                    | Ok(Some order) -> return! handle guid orderId order.State context
                    | Ok None -> return! orderNotFound context
                    | Error _ -> return! HttpErrors.unavailable context
            }

    /// <summary>Sends one command per shipment and responds with the most severe outcome.</summary>
    let private sendAll guid conflictMessage (sends: (unit -> Task<HttpOutcome>) list) (context: HttpContext) =
        task {
            let outcomes = ResizeArray<HttpOutcome>()

            for send in sends do
                let! outcome = send ()
                outcomes.Add outcome

            return! HttpErrors.respond (HttpOutcome.worst (List.ofSeq outcomes)) conflictMessage (redirect guid) context
        }

    /// <summary>Creates a single-shipment plan covering the whole order and begins fulfilment.</summary>
    let ship: EndpointHandler =
        withOrder (fun guid orderId state context ->
            task {
                match state with
                | Placed placed ->
                    let totals = placed.Reserved.Pending.Totals
                    let shipmentId = ShipmentId.generate ()

                    let plan =
                        { ShipmentId = shipmentId
                          CaptureId = CaptureId.generate ()
                          PaymentOperationId = PaymentOperationIds.forCapture shipmentId
                          Allocation =
                            { AllocationId = ShipmentAllocationId.generate ()
                              Lines =
                                placed.Reserved.Pending.Lines
                                |> List.map (fun line ->
                                    { LineId = line.LineId
                                      Quantity = Quantity.value line.Quantity })
                              Merchandise = totals.Subtotal
                              Shipping = totals.Shipping
                              Tax = totals.Tax
                              Total = totals.Total } }

                    let orders = context.GetService<OrderMachineClient>()

                    let! outcome =
                        Machine.send
                            orders.Orders
                            orderId
                            (EventEnvelope.create $"fulfil:v1:{guid:D}" (FulfilmentRequested [ plan ]))
                            context.RequestAborted

                    return!
                        HttpErrors.respond
                            (HttpOutcome.ofSend outcome)
                            "The order is not ready to ship."
                            (redirect guid)
                            context
                | _ -> return! HttpErrors.conflict "The order is not ready to ship." context
            })

    /// <summary>Marks every shipment prepared so label creation and dispatch can proceed.</summary>
    let prepare: EndpointHandler =
        withOrder (fun guid _ state context ->
            match state with
            | FulfilmentPending fulfilment
            | Processing fulfilment ->
                let shipments = context.GetService<ShipmentMachineClient>()

                let sends =
                    fulfilment.Shipments
                    |> List.map (fun shipment () ->
                        task {
                            let shipmentId = shipment.Plan.ShipmentId

                            let! outcome =
                                Machine.send
                                    shipments.Shipments
                                    (Shipments.shipmentEntityId shipmentId)
                                    (EventEnvelope.create
                                        $"prepare:v1:{ShipmentId.value shipmentId:D}"
                                        PreparationCompleted)
                                    context.RequestAborted

                            return HttpOutcome.ofSend outcome
                        })

                sendAll guid "A shipment could not be prepared." sends context
            | _ -> HttpErrors.conflict "The order is not being fulfilled." context)

    /// <summary>Dispatches every prepared shipment through the carrier boundary.</summary>
    let dispatch: EndpointHandler =
        withOrder (fun guid _ state context ->
            match state with
            | FulfilmentPending fulfilment
            | Processing fulfilment
            | PartiallyShipped fulfilment
            | Shipped fulfilment ->
                let shipments = context.GetService<ShipmentMachineClient>()
                let dispatchedAt = context.GetService<TimeProvider>().GetUtcNow()

                let sends =
                    fulfilment.Shipments
                    |> List.filter (fun shipment -> not shipment.Dispatched)
                    |> List.map (fun shipment () ->
                        task {
                            let shipmentId = shipment.Plan.ShipmentId

                            let! outcome =
                                Machine.send
                                    shipments.Shipments
                                    (Shipments.shipmentEntityId shipmentId)
                                    (EventEnvelope.create
                                        $"dispatch:v1:{ShipmentId.value shipmentId:D}"
                                        (DispatchConfirmed dispatchedAt))
                                    context.RequestAborted

                            return HttpOutcome.ofSend outcome
                        })

                sendAll guid "A shipment could not be dispatched." sends context
            | _ -> HttpErrors.conflict "The order has no shipments to dispatch." context)

    /// <summary>
    /// Records a delivered scan for every in-transit shipment. The scan carries the shipment's
    /// current tracking generation (a failed delivery bumps it), and both the carrier event id
    /// and the idempotency key are scoped to that generation, so a redelivery after a failed
    /// attempt is a new scan rather than a deduplicated repeat of the first one.
    /// </summary>
    let deliver: EndpointHandler =
        withOrder (fun guid _ state context ->
            match state with
            | PartiallyShipped fulfilment
            | Shipped fulfilment ->
                let shipments = context.GetService<ShipmentMachineClient>()
                let occurredAt = context.GetService<TimeProvider>().GetUtcNow()

                let sends =
                    fulfilment.Shipments
                    |> List.filter (fun shipment -> shipment.Dispatched && not shipment.Delivered)
                    |> List.map (fun shipment () ->
                        task {
                            let shipmentId = shipment.Plan.ShipmentId
                            let entity = Shipments.shipmentEntityId shipmentId

                            match! Machine.state shipments.Shipments entity context.RequestAborted with
                            | Ok(Some current) ->
                                match current.State with
                                | InTransit transit ->
                                    let generation = transit.TrackingGeneration
                                    let scan = $"delivered:{ShipmentId.value shipmentId:D}:{generation}"

                                    match TrackingEventId.create scan with
                                    | Error _ -> return HttpOutcome.Conflict
                                    | Ok eventId ->
                                        let! outcome =
                                            Machine.send
                                                shipments.Shipments
                                                entity
                                                (EventEnvelope.create
                                                    $"tracking:v1:{scan}"
                                                    (CarrierTrackingReceived
                                                        { EventId = eventId
                                                          Generation = generation
                                                          OccurredAt = occurredAt
                                                          Status = CarrierTrackingStatus.Delivered }))
                                                context.RequestAborted

                                        return HttpOutcome.ofSend outcome
                                | _ -> return HttpOutcome.Conflict
                            | Ok None -> return HttpOutcome.Conflict
                            | Error _ -> return HttpOutcome.Unavailable
                        })

                sendAll guid "A shipment could not be marked delivered." sends context
            | _ -> HttpErrors.conflict "The order has no shipped items to deliver." context)

    /// <summary>
    /// Hold and release are distinct operations with distinct idempotency keys scoped to the
    /// order epoch the operator acted on: a double-submit at the same epoch deduplicates, while
    /// a release after a hold (a later epoch) is a new command rather than a swallowed repeat.
    /// </summary>
    let private holdRelease (operation: string) (event: OrderEvent) : EndpointHandler =
        fun context ->
            task {
                match parseOrderId context with
                | None -> return! orderNotFound context
                | Some guid ->
                    let orders = context.GetService<OrderMachineClient>()
                    let orderId = Orders.orderId guid

                    match! Machine.state orders.Orders orderId context.RequestAborted with
                    | Ok(Some order) ->
                        let key = $"{operation}:v1:{guid:D}:{Epoch.value order.Epoch}"

                        let! outcome =
                            Machine.send orders.Orders orderId (EventEnvelope.create key event) context.RequestAborted

                        return!
                            HttpErrors.respond
                                (HttpOutcome.ofSend outcome)
                                $"The order cannot be {operation}ed in its current state."
                                (redirect guid)
                                context
                    | Ok None -> return! orderNotFound context
                    | Error _ -> return! HttpErrors.unavailable context
            }

    /// <summary>Places an operator hold on an order before it is fully delivered.</summary>
    let hold: EndpointHandler =
        holdRelease "hold" (HoldRequested(ReasonCode.ofLiteral "operator-review"))

    /// <summary>Releases an operator hold and resumes fulfilment.</summary>
    let release: EndpointHandler = holdRelease "release" ReleaseHoldRequested
