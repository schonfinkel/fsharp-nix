namespace App.Orders

open System
open App.Domain
open ByzantineSystems.Automata.Core
open NodaMoney

type OrderLine =
    { LineId: OrderLineId
      ProductId: ProductId
      Sku: Sku
      Name: NonEmptyString
      UnitPrice: Money
      Quantity: Quantity
      PriceVersion: PriceVersion }

type ReservationGeneration = int64

type ReservationPendingOrder =
    { SnapshotId: OrderSnapshotId
      CustomerId: string
      CartId: string
      Lines: OrderLine list
      Totals: OrderTotals
      Generation: ReservationGeneration }

type ReservedOrder =
    { Pending: ReservationPendingOrder
      ReservationIds: ReservationId list }

/// <summary>Authorization is in flight for one customer-minted attempt; the attempt id fences
/// stale callbacks after a declined attempt is retried.</summary>
type PaymentPendingOrder =
    { Reserved: ReservedOrder
      Attempt: PaymentOperationId }

type StockCommitPendingOrder =
    { Reserved: ReservedOrder
      ProviderReference: string }

type PlacedOrder =
    { Reserved: ReservedOrder
      ProviderReference: string }

type OrderShipmentPlan =
    { ShipmentId: ShipmentId
      CaptureId: CaptureId
      PaymentOperationId: PaymentOperationId
      Allocation: ShipmentCaptureAllocation }

type OrderShipmentStatus =
    { Plan: OrderShipmentPlan
      Created: bool
      CreationFailed: bool
      Dispatched: bool
      Delivered: bool
      CaptureSucceeded: bool }

type FulfilmentOrder =
    { PlacedOrder: PlacedOrder
      AddressSnapshotId: OrderSnapshotId
      Shipments: OrderShipmentStatus list }

type HeldForReviewOrder =
    { Fulfilment: FulfilmentOrder
      Reason: string }

/// <summary>Cancellation completes only after both durable effects settle: stock release and
/// payment unwinding. Either may arrive first; both are idempotent.</summary>
type CancellationPendingOrder =
    { Order: ReservationPendingOrder
      ReservationIds: ReservationId list
      Reason: string
      ReservationsSettled: bool
      PaymentSettled: bool }

type OrderState =
    | Initial
    | ReservationPending of ReservationPendingOrder
    | AwaitingAuthorization of ReservedOrder
    | PaymentPending of PaymentPendingOrder
    | StockCommitPending of StockCommitPendingOrder
    | Placed of PlacedOrder
    | HeldForReview of HeldForReviewOrder
    | FulfilmentPending of FulfilmentOrder
    | Processing of FulfilmentOrder
    | PartiallyShipped of FulfilmentOrder
    | Shipped of FulfilmentOrder
    | Delivered of FulfilmentOrder
    | CancellationPending of CancellationPendingOrder
    | Cancelled
    | ReservationFailed of code: string
    | ManualReview of reasonCode: string
    | Closed

[<RequireQualifiedAccess>]
type ReservationFailure =
    | InsufficientStock
    | ProductInactive
    | PriceVersionMismatch
    | InvalidReservation

type OrderEvent =
    | OrderSubmitted of ReservationPendingOrder
    | StockReserved of ReservationId list * ReservationGeneration
    | StockReservationFailed of ReservationFailure
    | ReservationExpired of ReservationGeneration * DateTimeOffset
    | CancelRequested
    | ReservationsReleased
    | AuthorizePaymentRequested of method: PaymentMethodReference * attempt: PaymentOperationId
    | PaymentAuthorized of attempt: PaymentOperationId * providerReference: string
    | PaymentDeclined of attempt: PaymentOperationId * reasonCode: string
    | PaymentSettled
    | StockCommitted
    | StockCommitFailed of reason: string
    | MarkManualReview of string
    | FulfilmentRequested of OrderShipmentPlan list
    | ShipmentCreated of shipmentId: ShipmentId * allocationId: ShipmentAllocationId
    | ShipmentCreationFailed of shipmentId: ShipmentId * allocationId: ShipmentAllocationId * reason: string
    | ShipmentDispatched of shipmentId: ShipmentId * allocationId: ShipmentAllocationId
    | ShipmentDelivered of shipmentId: ShipmentId * allocationId: ShipmentAllocationId
    | PaymentCaptured of captureId: CaptureId * operationId: PaymentOperationId
    | HoldRequested of reason: string
    | ReleaseHoldRequested
    | AddressSnapshotChanged of OrderSnapshotId

type OrderAction =
    | ReserveStock of ReservationPendingOrder
    | ReleaseReservations of ReservationId list
    | NotifyCartConverted of cartId: string
    | RequestAuthorization of method: PaymentMethodReference * attempt: PaymentOperationId * amount: Money
    | RequestPaymentCancellation of reason: string
    | CommitStock of ReservationId list
    | CreateShipment of shipment: OrderShipmentPlan * addressSnapshotId: OrderSnapshotId
    | RequestCapture of
        shipmentId: ShipmentId *
        allocation: ShipmentCaptureAllocation *
        captureId: CaptureId *
        operationId: PaymentOperationId *
        providerReference: string

[<RequireQualifiedAccess>]
type OrderActionError =
    | InvalidOrderEntityId
    | SnapshotMissing
    | CallbackEncodingFailed
    | ActionReceiptMismatch
    | InvalidAction

type Order = class end
type OrderId = EntityId<Order>

[<RequireQualifiedAccess>]
module Orders =
    [<Literal>]
    let MachineKey = "orders"

    [<Literal>]
    let ActionQueue = "order_actions"

    [<Literal>]
    let ChartVersion = 3

    let initialState = Initial

    let orderId (id: Guid) : OrderId = entityId $"order:{id:D}"

    let classifyState =
        function
        | Initial -> stateId "initial"
        | ReservationPending _ -> stateId "reservation-pending"
        | AwaitingAuthorization _ -> stateId "awaiting-authorization"
        | PaymentPending _ -> stateId "payment-pending"
        | StockCommitPending _ -> stateId "stock-commit-pending"
        | Placed _ -> stateId "placed"
        | HeldForReview _ -> stateId "held-for-review"
        | FulfilmentPending _ -> stateId "fulfilment-pending"
        | Processing _ -> stateId "processing"
        | PartiallyShipped _ -> stateId "partially-shipped"
        | Shipped _ -> stateId "shipped"
        | Delivered _ -> stateId "delivered"
        | CancellationPending _ -> stateId "cancellation-pending"
        | Cancelled -> stateId "cancelled"
        | ReservationFailed _ -> stateId "reservation-failed"
        | ManualReview _ -> stateId "manual-review"
        | Closed -> stateId "closed"

    let private initialSubmission _ event =
        match event with
        | OrderSubmitted _ -> true
        | _ -> false

    let private reserved state event =
        match state, event with
        | ReservationPending pending, StockReserved(_, generation) -> generation = pending.Generation
        | _ -> false

    let private failed state event =
        match state, event with
        | ReservationPending _, StockReservationFailed _ -> true
        | _ -> false

    let private payRequested state event =
        match state, event with
        | AwaitingAuthorization _, AuthorizePaymentRequested _ -> true
        | _ -> false

    let private paymentResult state event =
        match state, event with
        | PaymentPending pending, PaymentAuthorized(attempt, _)
        | PaymentPending pending, PaymentDeclined(attempt, _) -> attempt = pending.Attempt
        | _ -> false

    let private cancelable state event =
        match state, event with
        | (ReservationPending _ | AwaitingAuthorization _ | PaymentPending _), CancelRequested -> true
        | _ -> false

    let private expired state event =
        match state, event with
        | (AwaitingAuthorization order | PaymentPending { Reserved = order }), ReservationExpired(generation, _) ->
            generation = order.Pending.Generation
        | _ -> false

    let private stockCommitSettled state event =
        match state, event with
        | StockCommitPending _, (StockCommitted | StockCommitFailed _) -> true
        | _ -> false

    let private released state event =
        match state, event with
        | CancellationPending order, ReservationsReleased -> not order.ReservationsSettled
        | _ -> false

    let private paymentSettled state event =
        match state, event with
        | CancellationPending order, PaymentSettled -> not order.PaymentSettled
        | _ -> false

    let private absorb _ =
        function
        | StockReserved _
        | StockReservationFailed _
        | ReservationExpired _
        | ReservationsReleased
        | PaymentAuthorized _
        | PaymentDeclined _
        | PaymentSettled
        | StockCommitted
        | StockCommitFailed _
        | ShipmentCreated _
        | ShipmentCreationFailed _
        | ShipmentDispatched _
        | ShipmentDelivered _
        | PaymentCaptured _ -> true
        | OrderSubmitted _
        | CancelRequested
        | AuthorizePaymentRequested _
        | MarkManualReview _
        | FulfilmentRequested _
        | HoldRequested _
        | ReleaseHoldRequested
        | AddressSnapshotChanged _ -> false

    let private duplicateBy selector values =
        values |> List.countBy selector |> List.exists (fun (_, count) -> count > 1)

    let private validFulfilmentPlan (placed: PlacedOrder) (plan: OrderShipmentPlan list) =
        let expectedCurrency = Money.currencyCode placed.Reserved.Pending.Totals.Total
        let allAllocations = plan |> List.map _.Allocation

        let allocationMoney =
            allAllocations
            |> List.collect (fun allocation ->
                [ allocation.Merchandise
                  allocation.Shipping
                  allocation.Tax
                  allocation.Total ])

        let currenciesMatch =
            allocationMoney
            |> List.forall (fun money -> Money.currencyCode money = expectedCurrency)

        let allocationTotalsAreConsistent =
            allAllocations
            |> List.forall (fun allocation ->
                allocation.Total = allocation.Merchandise + allocation.Shipping + allocation.Tax)

        let totalMatches =
            currenciesMatch
            && (allAllocations
                |> List.map _.Total
                |> List.fold Money.add (Money.zero expectedCurrency |> Result.defaultWith invalidOp)) = placed.Reserved.Pending.Totals.Total

        let expectedQuantities =
            placed.Reserved.Pending.Lines
            |> List.map (fun line -> line.LineId, Quantity.value line.Quantity)
            |> Map.ofList

        let allocatedLines = allAllocations |> List.collect _.Lines

        let allocatedQuantities =
            allocatedLines
            |> List.groupBy _.LineId
            |> List.map (fun (lineId, lines) -> lineId, lines |> List.sumBy _.Quantity)
            |> Map.ofList

        not plan.IsEmpty
        && not (duplicateBy _.ShipmentId plan)
        && not (duplicateBy (fun shipment -> shipment.Allocation.AllocationId) plan)
        && not (duplicateBy _.CaptureId plan)
        && not (duplicateBy _.PaymentOperationId plan)
        && (allAllocations |> List.forall (fun allocation -> not allocation.Lines.IsEmpty))
        && (allocatedLines
            |> List.forall (fun line -> line.Quantity > 0 && Map.containsKey line.LineId expectedQuantities))
        && expectedQuantities = allocatedQuantities
        && currenciesMatch
        && allocationTotalsAreConsistent
        && totalMatches

    let private fulfilmentOfState =
        function
        | FulfilmentPending fulfilment
        | Processing fulfilment
        | PartiallyShipped fulfilment
        | Shipped fulfilment
        | Delivered fulfilment -> Some fulfilment
        | HeldForReview held -> Some held.Fulfilment
        | _ -> None

    let private classifyFulfilment fulfilment =
        if
            fulfilment.Shipments
            |> List.forall (fun shipment -> shipment.Delivered && shipment.CaptureSucceeded)
        then
            Delivered fulfilment
        elif fulfilment.Shipments |> List.forall _.Dispatched then
            Shipped fulfilment
        elif fulfilment.Shipments |> List.exists _.Dispatched then
            PartiallyShipped fulfilment
        elif fulfilment.Shipments |> List.forall _.Created then
            Processing fulfilment
        else
            FulfilmentPending fulfilment

    let private tryShipment shipmentId allocationId (fulfilment: FulfilmentOrder) =
        fulfilment.Shipments
        |> List.tryFind (fun shipment ->
            shipment.Plan.ShipmentId = shipmentId
            && shipment.Plan.Allocation.AllocationId = allocationId)

    let private updateShipment shipmentId allocationId update (fulfilment: FulfilmentOrder) =
        { fulfilment with
            Shipments =
                fulfilment.Shipments
                |> List.map (fun shipment ->
                    if
                        shipment.Plan.ShipmentId = shipmentId
                        && shipment.Plan.Allocation.AllocationId = allocationId
                    then
                        update shipment
                    else
                        shipment) }

    let private classifyUpdated state fulfilment =
        match state with
        | HeldForReview held -> HeldForReview { held with Fulfilment = fulfilment }
        | _ -> classifyFulfilment fulfilment

    let private fulfilmentRequested state event =
        match state, event with
        | Placed placed, FulfilmentRequested plan -> validFulfilmentPlan placed plan
        | _ -> false

    let private shipmentCreated state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, ShipmentCreated(shipmentId, allocationId) ->
            tryShipment shipmentId allocationId fulfilment
            |> Option.exists (fun shipment -> not shipment.Created && not shipment.CreationFailed)
        | _ -> false

    let private shipmentCreationFailed state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, ShipmentCreationFailed(shipmentId, allocationId, reason) ->
            not (String.IsNullOrWhiteSpace reason)
            && (tryShipment shipmentId allocationId fulfilment
                |> Option.exists (fun shipment -> not shipment.Created && not shipment.CreationFailed))
        | _ -> false

    let private shipmentDispatched state event =
        match state, event with
        | (Processing fulfilment | PartiallyShipped fulfilment), ShipmentDispatched(shipmentId, allocationId) ->
            tryShipment shipmentId allocationId fulfilment
            |> Option.exists (fun shipment -> shipment.Created && not shipment.Dispatched)
        | _ -> false

    let private shipmentDelivered state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, ShipmentDelivered(shipmentId, allocationId) ->
            tryShipment shipmentId allocationId fulfilment
            |> Option.exists (fun shipment -> shipment.Dispatched && not shipment.Delivered)
        | _ -> false

    let private paymentCaptured state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, PaymentCaptured(captureId, operationId) ->
            fulfilment.Shipments
            |> List.exists (fun shipment ->
                shipment.Dispatched
                && not shipment.CaptureSucceeded
                && shipment.Plan.CaptureId = captureId
                && shipment.Plan.PaymentOperationId = operationId)
        | _ -> false

    let private holdRequested state event =
        match state, event with
        | (FulfilmentPending _ | Processing _ | PartiallyShipped _ | Shipped _), HoldRequested reason ->
            not (String.IsNullOrWhiteSpace reason)
        | _ -> false

    let private releaseHoldRequested state event =
        match state, event with
        | HeldForReview _, ReleaseHoldRequested -> true
        | _ -> false

    let private addressSnapshotChanged state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, AddressSnapshotChanged _ -> fulfilment.Shipments |> List.forall (not << _.Dispatched)
        | _ -> false

    let private applyShipmentCreated state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, ShipmentCreated(shipmentId, allocationId) ->
            let updated =
                updateShipment shipmentId allocationId (fun shipment -> { shipment with Created = true }) fulfilment

            [], classifyUpdated state updated
        | _ -> [], state

    let private applyShipmentCreationFailed state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, ShipmentCreationFailed(shipmentId, allocationId, reason) ->
            let updated =
                updateShipment
                    shipmentId
                    allocationId
                    (fun shipment -> { shipment with CreationFailed = true })
                    fulfilment

            [],
            HeldForReview
                { Fulfilment = updated
                  Reason = reason }
        | _ -> [], state

    let private applyShipmentDispatched state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, ShipmentDispatched(shipmentId, allocationId) ->
            match tryShipment shipmentId allocationId fulfilment with
            | Some shipment ->
                let updated =
                    updateShipment
                        shipmentId
                        allocationId
                        (fun current -> { current with Dispatched = true })
                        fulfilment

                [ RequestCapture(
                      shipmentId,
                      shipment.Plan.Allocation,
                      shipment.Plan.CaptureId,
                      shipment.Plan.PaymentOperationId,
                      fulfilment.PlacedOrder.ProviderReference
                  ) ],
                classifyFulfilment updated
            | None -> [], state
        | _ -> [], state

    let private applyShipmentDelivered state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, ShipmentDelivered(shipmentId, allocationId) ->
            let updated =
                updateShipment shipmentId allocationId (fun shipment -> { shipment with Delivered = true }) fulfilment

            [], classifyUpdated state updated
        | _ -> [], state

    let private applyPaymentCaptured state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, PaymentCaptured(captureId, _) ->
            let updated =
                { fulfilment with
                    Shipments =
                        fulfilment.Shipments
                        |> List.map (fun shipment ->
                            if shipment.Plan.CaptureId = captureId then
                                { shipment with
                                    CaptureSucceeded = true }
                            else
                                shipment) }

            [], classifyUpdated state updated
        | _ -> [], state

    let private applyHoldRequested state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, HoldRequested reason ->
            [],
            HeldForReview
                { Fulfilment = fulfilment
                  Reason = reason }
        | _ -> [], state

    let private applyAddressSnapshotChanged state event =
        match fulfilmentOfState state, event with
        | Some fulfilment, AddressSnapshotChanged snapshotId ->
            let updated =
                { fulfilment with
                    AddressSnapshotId = snapshotId }

            match state with
            | HeldForReview held -> [], HeldForReview { held with Fulfilment = updated }
            | _ -> [], classifyFulfilment updated
        | _ -> [], state

    let private beginCancellation (reserved: ReservedOrder) reason paymentInvolved =
        let actions =
            [ ReleaseReservations reserved.ReservationIds
              if paymentInvolved then
                  RequestPaymentCancellation reason ]

        actions,
        CancellationPending
            { Order = reserved.Pending
              ReservationIds = reserved.ReservationIds
              Reason = reason
              ReservationsSettled = false
              PaymentSettled = not paymentInvolved }

    let chartResult =
        statechart<OrderState, OrderEvent, OrderAction, OrderActionError> {
            root MachineKey
            classify classifyState

            state "initial" {
                on initialSubmission (fun _ event ->
                    match event with
                    | OrderSubmitted order ->
                        [ ReserveStock order; NotifyCartConverted order.CartId ], ReservationPending order
                    | _ -> [], Initial)
            }

            state "reservation-pending" {
                on reserved (fun state event ->
                    match state, event with
                    | ReservationPending pending, StockReserved(ids, _) ->
                        [],
                        AwaitingAuthorization
                            { Pending = pending
                              ReservationIds = ids }
                    | _ -> [], state)

                on failed (fun _ event ->
                    match event with
                    | StockReservationFailed reason -> [], ReservationFailed(string reason)
                    | _ -> [], Initial)

                on cancelable (fun state _ ->
                    match state with
                    | ReservationPending pending ->
                        beginCancellation
                            { Pending = pending
                              ReservationIds = [] }
                            "customer-cancelled"
                            false
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "awaiting-authorization" {
                on payRequested (fun state event ->
                    match state, event with
                    | AwaitingAuthorization reserved, AuthorizePaymentRequested(method, attempt) ->
                        [ RequestAuthorization(method, attempt, reserved.Pending.Totals.Total) ],
                        PaymentPending
                            { Reserved = reserved
                              Attempt = attempt }
                    | _ -> [], state)

                on cancelable (fun state _ ->
                    match state with
                    | AwaitingAuthorization reserved -> beginCancellation reserved "customer-cancelled" false
                    | _ -> [], state)

                on expired (fun state event ->
                    match state, event with
                    | AwaitingAuthorization reserved, ReservationExpired _ ->
                        beginCancellation reserved "reservation-expired" false
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "payment-pending" {
                on paymentResult (fun state event ->
                    match state, event with
                    | PaymentPending pending, PaymentAuthorized(_, reference) ->
                        [ CommitStock pending.Reserved.ReservationIds ],
                        StockCommitPending
                            { Reserved = pending.Reserved
                              ProviderReference = reference }
                    | PaymentPending pending, PaymentDeclined _ -> [], AwaitingAuthorization pending.Reserved
                    | _ -> [], state)

                on cancelable (fun state _ ->
                    match state with
                    | PaymentPending pending -> beginCancellation pending.Reserved "customer-cancelled" true
                    | _ -> [], state)

                on expired (fun state event ->
                    match state, event with
                    | PaymentPending pending, ReservationExpired _ ->
                        beginCancellation pending.Reserved "reservation-expired" true
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "stock-commit-pending" {
                on stockCommitSettled (fun state event ->
                    match state, event with
                    | StockCommitPending pending, StockCommitted ->
                        [],
                        Placed
                            { Reserved = pending.Reserved
                              ProviderReference = pending.ProviderReference }
                    | StockCommitPending _, StockCommitFailed reason -> [], ManualReview reason
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "placed" {
                on fulfilmentRequested (fun state event ->
                    match state, event with
                    | Placed placed, FulfilmentRequested plan ->
                        let fulfilment =
                            { PlacedOrder = placed
                              AddressSnapshotId = placed.Reserved.Pending.SnapshotId
                              Shipments =
                                plan
                                |> List.map (fun shipment ->
                                    { Plan = shipment
                                      Created = false
                                      CreationFailed = false
                                      Dispatched = false
                                      Delivered = false
                                      CaptureSucceeded = false }) }

                        plan
                        |> List.map (fun shipment -> CreateShipment(shipment, fulfilment.AddressSnapshotId)),
                        FulfilmentPending fulfilment
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "held-for-review" {
                on shipmentCreated applyShipmentCreated
                on shipmentCreationFailed applyShipmentCreationFailed
                on shipmentDelivered applyShipmentDelivered
                on paymentCaptured applyPaymentCaptured

                on releaseHoldRequested (fun state _ ->
                    match state with
                    | HeldForReview held ->
                        let failed = held.Fulfilment.Shipments |> List.filter _.CreationFailed

                        if failed.IsEmpty then
                            [], classifyFulfilment held.Fulfilment
                        else
                            let retried =
                                { held.Fulfilment with
                                    Shipments =
                                        held.Fulfilment.Shipments
                                        |> List.map (fun shipment ->
                                            if shipment.CreationFailed then
                                                { shipment with CreationFailed = false }
                                            else
                                                shipment) }

                            failed
                            |> List.map (fun shipment ->
                                CreateShipment(shipment.Plan, held.Fulfilment.AddressSnapshotId)),
                            FulfilmentPending retried
                    | _ -> [], state)

                on addressSnapshotChanged applyAddressSnapshotChanged
                internalOn absorb (fun _ _ -> [])
            }

            state "fulfilment-pending" {
                on shipmentCreated applyShipmentCreated
                on shipmentCreationFailed applyShipmentCreationFailed
                on holdRequested applyHoldRequested
                on addressSnapshotChanged applyAddressSnapshotChanged
                internalOn absorb (fun _ _ -> [])
            }

            state "processing" {
                on shipmentDispatched applyShipmentDispatched
                on holdRequested applyHoldRequested
                on addressSnapshotChanged applyAddressSnapshotChanged
                internalOn absorb (fun _ _ -> [])
            }

            state "partially-shipped" {
                on shipmentDispatched applyShipmentDispatched
                on shipmentDelivered applyShipmentDelivered
                on paymentCaptured applyPaymentCaptured
                on holdRequested applyHoldRequested
                internalOn absorb (fun _ _ -> [])
            }

            state "shipped" {
                on shipmentDelivered applyShipmentDelivered
                on paymentCaptured applyPaymentCaptured
                on holdRequested applyHoldRequested
                internalOn absorb (fun _ _ -> [])
            }

            state "delivered" { internalOn absorb (fun _ _ -> []) }

            state "cancellation-pending" {
                on released (fun state _ ->
                    match state with
                    | CancellationPending order ->
                        [],
                        (if order.PaymentSettled then
                             Cancelled
                         else
                             CancellationPending
                                 { order with
                                     ReservationsSettled = true })
                    | _ -> [], state)

                on paymentSettled (fun state _ ->
                    match state with
                    | CancellationPending order ->
                        [],
                        (if order.ReservationsSettled then
                             Cancelled
                         else
                             CancellationPending { order with PaymentSettled = true })
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "cancelled" { internalOn absorb (fun _ _ -> []) }
            state "reservation-failed" { internalOn absorb (fun _ _ -> []) }
            state "manual-review" { internalOn absorb (fun _ _ -> []) }
            state "closed" { internalOn absorb (fun _ _ -> []) }
        }

    let chartValue =
        match chartResult with
        | Ok chart -> chart
        | Error errors -> invalidOp $"orders chart is invalid: %A{errors}"
