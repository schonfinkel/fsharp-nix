namespace App.Shipments

open System
open App.Domain
open ByzantineSystems.Automata.Core

type ShipmentLine = { LineId: OrderLineId; Quantity: int }

type ShipmentRequest =
    { ShipmentId: ShipmentId
      AllocationId: ShipmentAllocationId
      OrderId: string
      Lines: ShipmentLine list }

type TrackingCheckpoint =
    { EventId: TrackingEventId
      OccurredAt: DateTimeOffset }

type PreparingShipment =
    { Request: ShipmentRequest
      LabelGeneration: int64 }

type LabelPendingShipment =
    { Shipment: PreparingShipment
      Generation: int64 }

type ReadyShipment =
    { Shipment: PreparingShipment
      CarrierReference: CarrierReference }

type ShipmentInTransit =
    { Shipment: ReadyShipment
      TrackingGeneration: int64
      LastCheckpoint: TrackingCheckpoint option
      FailedDeliveryAttempts: int }

type FailedShipmentDelivery =
    { Transit: ShipmentInTransit
      ReasonCode: string }

type CompletedShipment =
    { Transit: ShipmentInTransit
      CompletedAt: DateTimeOffset }

type ShipmentState =
    | AllocationPending of ShipmentRequest option
    | Preparing of PreparingShipment
    | LabelPending of LabelPendingShipment
    | ReadyToDispatch of ReadyShipment
    | InTransit of ShipmentInTransit
    | DeliveryFailed of FailedShipmentDelivery
    | Delivered of CompletedShipment
    | ReturnedToSender of CompletedShipment
    | Lost of CompletedShipment
    | ManualReview of ShipmentRequest * reasonCode: string
    | Closed of ShipmentRequest

[<RequireQualifiedAccess>]
type CarrierTrackingStatus =
    | InTransit
    | DeliveryFailed of reasonCode: string
    | Delivered
    | ReturnedToSender
    | Lost

type CarrierTrackingUpdate =
    { EventId: TrackingEventId
      Generation: int64
      OccurredAt: DateTimeOffset
      Status: CarrierTrackingStatus }

type ShipmentEvent =
    | ShipmentRequested of ShipmentRequest
    | AllocationConfirmed of ShipmentAllocationId
    | AllocationRejected of ShipmentAllocationId * reasonCode: string
    | PreparationCompleted
    | LabelCreated of generation: int64 * carrierReference: CarrierReference
    | LabelCreationFailed of generation: int64 * reasonCode: string
    | DispatchConfirmed of dispatchedAt: DateTimeOffset
    | CarrierTrackingReceived of CarrierTrackingUpdate
    | DeliveryRetryRequested
    | MarkLost of expectedGeneration: int64 * expectedLastScan: DateTimeOffset option * detectedAt: DateTimeOffset
    | ManualReviewRequested of reasonCode: string
    | ShipmentCloseRequested

type ShipmentAction =
    | ConfirmAllocation of ShipmentAllocationId
    | CreateCarrierLabel of ShipmentId * generation: int64
    | NotifyOrderDispatched of orderId: string * ShipmentId * ShipmentAllocationId * dispatchedAt: DateTimeOffset
    | NotifyOrderDelivered of orderId: string * ShipmentId * ShipmentAllocationId * deliveredAt: DateTimeOffset
    | RequestDeliveryRetry of CarrierReference * attempt: int

[<RequireQualifiedAccess>]
type ShipmentActionError =
    | InvalidShipmentEntityId
    | AllocationLookupFailed
    | AllocationMismatch
    | CarrierRequestFailed
    | CallbackEncodingFailed
    | ActionReceiptMismatch
    | InvalidAction

type Shipment = class end
type ShipmentEntityId = EntityId<Shipment>

[<RequireQualifiedAccess>]
module Shipments =
    [<Literal>]
    let MachineKey = "shipments"

    [<Literal>]
    let ActionQueue = "shipment_actions"

    [<Literal>]
    let MaxDeliveryAttempts = 3

    let initialState = AllocationPending None

    let shipmentEntityId (id: ShipmentId) : ShipmentEntityId =
        entityId $"shipment:{ShipmentId.wireString id}"

    let classifyState =
        function
        | AllocationPending _ -> stateId "allocation-pending"
        | Preparing _ -> stateId "preparing"
        | LabelPending _ -> stateId "label-pending"
        | ReadyToDispatch _ -> stateId "ready-to-dispatch"
        | InTransit _ -> stateId "in-transit"
        | DeliveryFailed _ -> stateId "delivery-failed"
        | Delivered _ -> stateId "delivered"
        | ReturnedToSender _ -> stateId "returned-to-sender"
        | Lost _ -> stateId "lost"
        | ManualReview _ -> stateId "manual-review"
        | Closed _ -> stateId "closed"

    let private requestEvent state event =
        match state, event with
        | AllocationPending None, ShipmentRequested _ -> true
        | _ -> false

    let private allocationResult state event =
        match state, event with
        | AllocationPending(Some shipment), AllocationConfirmed allocationId
        | AllocationPending(Some shipment), AllocationRejected(allocationId, _) -> allocationId = shipment.AllocationId
        | _ -> false

    let private preparationCompleted state event =
        match state, event with
        | Preparing _, PreparationCompleted -> true
        | _ -> false

    let private labelResult state event =
        match state, event with
        | LabelPending pending, LabelCreated(generation, _)
        | LabelPending pending, LabelCreationFailed(generation, _) -> generation = pending.Generation
        | _ -> false

    let private dispatchEvent state event =
        match state, event with
        | ReadyToDispatch _, DispatchConfirmed _ -> true
        | _ -> false

    let private transitOf =
        function
        | InTransit transit -> Some transit
        | DeliveryFailed failed -> Some failed.Transit
        | Delivered completed
        | ReturnedToSender completed
        | Lost completed -> Some completed.Transit
        | AllocationPending _
        | Preparing _
        | LabelPending _
        | ReadyToDispatch _
        | ManualReview _
        | Closed _ -> None

    let private currentTrackingUpdate state event =
        match transitOf state, event with
        | Some transit, CarrierTrackingReceived update ->
            let isLater =
                transit.LastCheckpoint
                |> Option.forall (fun checkpoint ->
                    update.EventId <> checkpoint.EventId
                    && update.OccurredAt > checkpoint.OccurredAt)

            update.Generation = transit.TrackingGeneration && isLater
        | _ -> false

    let private retryEvent state event =
        match state, event with
        | DeliveryFailed failed, DeliveryRetryRequested -> failed.Transit.FailedDeliveryAttempts < MaxDeliveryAttempts
        | _ -> false

    let private markLostEvent state event =
        match transitOf state, event with
        | Some transit, MarkLost(generation, expectedLastScan, _) ->
            generation = transit.TrackingGeneration
            && expectedLastScan = (transit.LastCheckpoint |> Option.map _.OccurredAt)
        | _ -> false

    let private manualReviewEvent state event =
        match state, event with
        | AllocationPending None, ManualReviewRequested _ -> false
        | _, ManualReviewRequested _ -> true
        | _ -> false

    let private closeEvent state event =
        match state, event with
        | (Delivered _ | ReturnedToSender _ | Lost _ | ManualReview _), ShipmentCloseRequested -> true
        | _ -> false

    let private requestOf =
        function
        | AllocationPending(Some shipment) -> Some shipment
        | Preparing shipment -> Some shipment.Request
        | LabelPending pending -> Some pending.Shipment.Request
        | ReadyToDispatch shipment -> Some shipment.Shipment.Request
        | InTransit transit -> Some transit.Shipment.Shipment.Request
        | DeliveryFailed failed -> Some failed.Transit.Shipment.Shipment.Request
        | Delivered completed
        | ReturnedToSender completed
        | Lost completed -> Some completed.Transit.Shipment.Shipment.Request
        | ManualReview(shipment, _)
        | Closed shipment -> Some shipment
        | AllocationPending None -> None

    /// Callback-shaped events are deliberately absorbed when their generation, allocation,
    /// event id, or timestamp is stale. Commands remain rejectable illegal transitions.
    let private absorbCallback _ event =
        match event with
        | AllocationConfirmed _
        | AllocationRejected _
        | LabelCreated _
        | LabelCreationFailed _
        | CarrierTrackingReceived _
        | MarkLost _ -> true
        | ShipmentRequested _
        | PreparationCompleted
        | DispatchConfirmed _
        | DeliveryRetryRequested
        | ManualReviewRequested _
        | ShipmentCloseRequested -> false

    let private applyTracking transit update =
        let checkpoint =
            { EventId = update.EventId
              OccurredAt = update.OccurredAt }

        let nextTransit =
            { transit with
                LastCheckpoint = Some checkpoint }

        match update.Status with
        | CarrierTrackingStatus.InTransit -> InTransit nextTransit
        | CarrierTrackingStatus.DeliveryFailed reason ->
            DeliveryFailed
                { Transit =
                    { nextTransit with
                        FailedDeliveryAttempts = transit.FailedDeliveryAttempts + 1 }
                  ReasonCode = reason }
        | CarrierTrackingStatus.Delivered ->
            Delivered
                { Transit = nextTransit
                  CompletedAt = update.OccurredAt }
        | CarrierTrackingStatus.ReturnedToSender ->
            ReturnedToSender
                { Transit = nextTransit
                  CompletedAt = update.OccurredAt }
        | CarrierTrackingStatus.Lost ->
            Lost
                { Transit = nextTransit
                  CompletedAt = update.OccurredAt }

    let private trackingTransition state event =
        match transitOf state, event with
        | Some transit, CarrierTrackingReceived update ->
            let next = applyTracking transit update

            let actions =
                match update.Status with
                | CarrierTrackingStatus.Delivered ->
                    let request = transit.Shipment.Shipment.Request

                    [ NotifyOrderDelivered(request.OrderId, request.ShipmentId, request.AllocationId, update.OccurredAt) ]
                | _ -> []

            actions, next
        | _ -> [], state

    let private manualReviewTransition state event =
        match requestOf state, event with
        | Some shipment, ManualReviewRequested reason -> [], ManualReview(shipment, reason)
        | _ -> [], state

    let private closeTransition state =
        match requestOf state with
        | Some shipment -> [], Closed shipment
        | None -> [], state

    let chartResult =
        statechart<ShipmentState, ShipmentEvent, ShipmentAction, ShipmentActionError> {
            root MachineKey
            classify classifyState

            state "allocation-pending" {
                on requestEvent (fun _ event ->
                    match event with
                    | ShipmentRequested shipment ->
                        [ ConfirmAllocation shipment.AllocationId ], AllocationPending(Some shipment)
                    | _ -> [], initialState)

                on allocationResult (fun state event ->
                    match state, event with
                    | AllocationPending(Some shipment), AllocationConfirmed _ ->
                        [],
                        Preparing
                            { Request = shipment
                              LabelGeneration = 0L }
                    | AllocationPending(Some shipment), AllocationRejected(_, reason) ->
                        [], ManualReview(shipment, reason)
                    | _ -> [], state)

                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "preparing" {
                on preparationCompleted (fun state _ ->
                    match state with
                    | Preparing shipment ->
                        let generation = shipment.LabelGeneration + 1L

                        [ CreateCarrierLabel(shipment.Request.ShipmentId, generation) ],
                        LabelPending
                            { Shipment =
                                { shipment with
                                    LabelGeneration = generation }
                              Generation = generation }
                    | _ -> [], state)

                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "label-pending" {
                on labelResult (fun state event ->
                    match state, event with
                    | LabelPending pending, LabelCreated(_, reference) ->
                        [],
                        ReadyToDispatch
                            { Shipment = pending.Shipment
                              CarrierReference = reference }
                    | LabelPending pending, LabelCreationFailed(_, reason) ->
                        [], ManualReview(pending.Shipment.Request, reason)
                    | _ -> [], state)

                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "ready-to-dispatch" {
                on dispatchEvent (fun state event ->
                    match state, event with
                    | ReadyToDispatch shipment, DispatchConfirmed dispatchedAt ->
                        let request = shipment.Shipment.Request

                        [ NotifyOrderDispatched(
                              request.OrderId,
                              request.ShipmentId,
                              request.AllocationId,
                              dispatchedAt
                          ) ],
                        InTransit
                            { Shipment = shipment
                              TrackingGeneration = 1L
                              LastCheckpoint = None
                              FailedDeliveryAttempts = 0 }
                    | _ -> [], state)

                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "in-transit" {
                on currentTrackingUpdate trackingTransition

                on markLostEvent (fun state event ->
                    match transitOf state, event with
                    | Some transit, MarkLost(_, _, detectedAt) ->
                        [],
                        Lost
                            { Transit = transit
                              CompletedAt = detectedAt }
                    | _ -> [], state)

                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "delivery-failed" {
                on currentTrackingUpdate trackingTransition

                on retryEvent (fun state _ ->
                    match state with
                    | DeliveryFailed failed ->
                        let transit =
                            { failed.Transit with
                                TrackingGeneration = failed.Transit.TrackingGeneration + 1L }

                        [ RequestDeliveryRetry(transit.Shipment.CarrierReference, transit.FailedDeliveryAttempts) ],
                        InTransit transit
                    | _ -> [], state)

                on markLostEvent (fun state event ->
                    match transitOf state, event with
                    | Some transit, MarkLost(_, _, detectedAt) ->
                        [],
                        Lost
                            { Transit = transit
                              CompletedAt = detectedAt }
                    | _ -> [], state)

                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "delivered" {
                on currentTrackingUpdate trackingTransition
                on closeEvent (fun state _ -> closeTransition state)
                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "returned-to-sender" {
                on closeEvent (fun state _ -> closeTransition state)
                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "lost" {
                on closeEvent (fun state _ -> closeTransition state)
                on manualReviewEvent manualReviewTransition
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "manual-review" {
                on closeEvent (fun state _ -> closeTransition state)
                internalOn absorbCallback (fun _ _ -> [])
            }

            state "closed" { internalOn absorbCallback (fun _ _ -> []) }
        }

    let chartValue =
        match chartResult with
        | Ok chart -> chart
        | Error errors -> invalidOp $"shipments chart is invalid: %A{errors}"
