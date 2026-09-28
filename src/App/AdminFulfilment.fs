namespace App

open System
open System.Threading.Tasks
open App.Database
open App.Domain
open App.Orders
open App.Shipments
open App.Views
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker
open Oxpecker.ViewEngine

/// <summary>
/// Operator fulfilment controls. The sandbox drives dispatch and tracking through the carrier
/// boundary by enqueueing the corresponding events into the shipments machine, using the
/// shipment ids the order's fulfilment plan already owns.
/// </summary>
[<RequireQualifiedAccess>]
module AdminFulfilment =
    let private parseOrderId (context: HttpContext) =
        context.TryGetRouteValue("orderId")
        |> Option.defaultValue ""
        |> fun text -> Guid.TryParseExact(text, "D")

    let private notFound (context: HttpContext) =
        context.Response.StatusCode <- StatusCodes.Status404NotFound
        context.WriteHtmlView(p (class' = "error") { "Order not found." })

    let private redirect context guid =
        Web.redirect $"/orders/{guid:D}" context

    let private shipmentIds (fulfilment: FulfilmentOrder) =
        fulfilment.Shipments |> List.map (fun shipment -> shipment.Plan.ShipmentId)

    /// <summary>Creates a single-shipment plan covering the whole order and begins fulfilment.</summary>
    let ship: EndpointHandler =
        fun context ->
            task {
                let orderGuid = parseOrderId context

                match orderGuid with
                | false, _ -> return! notFound context
                | true, guid ->
                    let orders = context.GetService<OrderMachineClient>()
                    let orderId = Orders.orderId guid

                    let! snapshot = Machine.state orders.Orders orderId context.RequestAborted

                    match snapshot with
                    | Ok(Some order) ->
                        match order.State with
                        | Placed placed ->
                            let totals = placed.Reserved.Pending.Totals

                            let shipmentId = ShipmentId.create (Guid.NewGuid()) |> Result.defaultWith invalidOp

                            let allocationId =
                                ShipmentAllocationId.create (Guid.NewGuid()) |> Result.defaultWith invalidOp

                            let captureId = CaptureId.create (Guid.NewGuid()) |> Result.defaultWith invalidOp

                            let operationId =
                                PaymentOperationId.create $"capture:v1:{ShipmentId.value shipmentId:N}"
                                |> Result.defaultWith invalidOp

                            let allocation =
                                { AllocationId = allocationId
                                  Lines =
                                    placed.Reserved.Pending.Lines
                                    |> List.map (fun line ->
                                        { LineId = line.LineId
                                          Quantity = Quantity.value line.Quantity })
                                  Merchandise = totals.Subtotal
                                  Shipping = totals.Shipping
                                  Tax = totals.Tax
                                  Total = totals.Total }

                            let plan =
                                { ShipmentId = shipmentId
                                  CaptureId = captureId
                                  PaymentOperationId = operationId
                                  Allocation = allocation }

                            let! _ =
                                Machine.send
                                    orders.Orders
                                    orderId
                                    (EventEnvelope.create $"fulfil:v1:{guid:D}" (FulfilmentRequested [ plan ]))
                                    context.RequestAborted

                            return! redirect context guid
                        | _ ->
                            context.Response.StatusCode <- StatusCodes.Status409Conflict
                            return! context.WriteHtmlView(p (class' = "error") { "The order is not ready to ship." })
                    | _ -> return! notFound context
            }

    /// <summary>Marks every shipment prepared so label creation and dispatch can proceed.</summary>
    let prepare: EndpointHandler =
        fun context ->
            task {
                let orderGuid = parseOrderId context

                match orderGuid with
                | false, _ -> return! notFound context
                | true, guid ->
                    let orders = context.GetService<OrderMachineClient>()
                    let shipments = context.GetService<ShipmentMachineClient>()
                    let orderId = Orders.orderId guid

                    let! snapshot = Machine.state orders.Orders orderId context.RequestAborted

                    match snapshot with
                    | Ok(Some order) ->
                        match order.State with
                        | FulfilmentPending fulfilment
                        | Processing fulfilment ->
                            for shipmentId in shipmentIds fulfilment do
                                let entity = Shipments.shipmentEntityId shipmentId

                                let! _ =
                                    Machine.send
                                        shipments.Shipments
                                        entity
                                        (EventEnvelope.create
                                            $"prepare:v1:{ShipmentId.value shipmentId:D}"
                                            PreparationCompleted)
                                        context.RequestAborted

                                ()

                            return! redirect context guid
                        | _ ->
                            context.Response.StatusCode <- StatusCodes.Status409Conflict
                            return! context.WriteHtmlView(p (class' = "error") { "The order is not being fulfilled." })
                    | _ -> return! notFound context
            }

    /// <summary>Dispatches every prepared shipment through the carrier boundary.</summary>
    let dispatch: EndpointHandler =
        fun context ->
            task {
                let orderGuid = parseOrderId context

                match orderGuid with
                | false, _ -> return! notFound context
                | true, guid ->
                    let orders = context.GetService<OrderMachineClient>()
                    let shipments = context.GetService<ShipmentMachineClient>()
                    let orderId = Orders.orderId guid

                    let! snapshot = Machine.state orders.Orders orderId context.RequestAborted

                    match snapshot with
                    | Ok(Some order) ->
                        match order.State with
                        | FulfilmentPending fulfilment
                        | Processing fulfilment
                        | PartiallyShipped fulfilment
                        | Shipped fulfilment ->
                            let dispatchedAt = context.GetService<TimeProvider>().GetUtcNow()

                            for shipment in fulfilment.Shipments do
                                if not shipment.Dispatched then
                                    let entity = Shipments.shipmentEntityId shipment.Plan.ShipmentId

                                    let! _ =
                                        Machine.send
                                            shipments.Shipments
                                            entity
                                            (EventEnvelope.create
                                                $"dispatch:v1:{ShipmentId.value shipment.Plan.ShipmentId:D}"
                                                (DispatchConfirmed dispatchedAt))
                                            context.RequestAborted

                                    ()

                            return! redirect context guid
                        | _ ->
                            context.Response.StatusCode <- StatusCodes.Status409Conflict

                            return!
                                context.WriteHtmlView(
                                    p (class' = "error") { "The order has no shipments to dispatch." }
                                )
                    | _ -> return! notFound context
            }

    /// <summary>Records a delivered tracking scan for every in-transit shipment.</summary>
    let deliver: EndpointHandler =
        fun context ->
            task {
                let orderGuid = parseOrderId context

                match orderGuid with
                | false, _ -> return! notFound context
                | true, guid ->
                    let orders = context.GetService<OrderMachineClient>()
                    let shipments = context.GetService<ShipmentMachineClient>()
                    let orderId = Orders.orderId guid

                    let! snapshot = Machine.state orders.Orders orderId context.RequestAborted

                    match snapshot with
                    | Ok(Some order) ->
                        match order.State with
                        | PartiallyShipped fulfilment
                        | Shipped fulfilment ->
                            let occurredAt = context.GetService<TimeProvider>().GetUtcNow()

                            for shipment in fulfilment.Shipments do
                                if shipment.Dispatched && not shipment.Delivered then
                                    let entity = Shipments.shipmentEntityId shipment.Plan.ShipmentId

                                    let trackingId =
                                        TrackingEventId.create
                                            $"delivered:{ShipmentId.value shipment.Plan.ShipmentId:D}"
                                        |> Result.defaultWith invalidOp

                                    let! _ =
                                        Machine.send
                                            shipments.Shipments
                                            entity
                                            (EventEnvelope.create
                                                $"tracking:v1:{ShipmentId.value shipment.Plan.ShipmentId:D}"
                                                (CarrierTrackingReceived
                                                    { EventId = trackingId
                                                      Generation = 1L
                                                      OccurredAt = occurredAt
                                                      Status = CarrierTrackingStatus.Delivered }))
                                            context.RequestAborted

                                    ()

                            return! redirect context guid
                        | _ ->
                            context.Response.StatusCode <- StatusCodes.Status409Conflict

                            return!
                                context.WriteHtmlView(
                                    p (class' = "error") { "The order has no shipped items to deliver." }
                                )
                    | _ -> return! notFound context
            }

    let private holdRelease eventFactory (context: HttpContext) =
        task {
            let orderGuid = parseOrderId context

            match orderGuid with
            | false, _ -> return! notFound context
            | true, guid ->
                let orders = context.GetService<OrderMachineClient>()
                let orderId = Orders.orderId guid

                let! _ =
                    Machine.send
                        orders.Orders
                        orderId
                        (EventEnvelope.create $"hold:v1:{guid:D}" (eventFactory ()))
                        context.RequestAborted

                return! redirect context guid
        }

    /// <summary>Places an operator hold on an order before it is fully delivered.</summary>
    let hold: EndpointHandler =
        fun context -> holdRelease (fun () -> HoldRequested "operator-review") context

    /// <summary>Releases an operator hold and resumes fulfilment.</summary>
    let release: EndpointHandler =
        fun context -> holdRelease (fun () -> ReleaseHoldRequested) context
