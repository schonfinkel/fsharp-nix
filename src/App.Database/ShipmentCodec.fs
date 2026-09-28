namespace App.Database

open System
open System.Text.Json
open System.Text.Json.Serialization
open App.Domain
open App.Shipments
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging

module ShipmentWire =
    [<CLIMutable>]
    type LineDto = { LineId: string; Quantity: int }

    [<CLIMutable>]
    type WireDto =
        { Tag: string
          ShipmentId: string
          AllocationId: string
          OrderId: string
          Lines: LineDto array
          CarrierReference: string
          ReasonCode: string
          Generation: int64
          LabelGeneration: int64
          TrackingGeneration: int64
          OccurredAt: int64
          DispatchedAt: int64
          CompletedAt: int64
          DetectedAt: int64
          ExpectedGeneration: int64
          ExpectedLastScanAt: int64
          EventId: string
          Status: string
          LastCheckpointEventId: string
          LastCheckpointAt: int64
          FailedDeliveryAttempts: int
          Attempt: int }

[<RequireQualifiedAccess>]
module ShipmentCodec =
    let private options =
        JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

    do options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let private codecError name message =
        CodecError.DecodeError(name, FormatException message)

    let private empty tag : ShipmentWire.WireDto =
        { Tag = tag
          ShipmentId = ""
          AllocationId = ""
          OrderId = ""
          Lines = [||]
          CarrierReference = ""
          ReasonCode = ""
          Generation = 0L
          LabelGeneration = 0L
          TrackingGeneration = 0L
          OccurredAt = 0L
          DispatchedAt = 0L
          CompletedAt = 0L
          DetectedAt = 0L
          ExpectedGeneration = 0L
          ExpectedLastScanAt = 0L
          EventId = ""
          Status = ""
          LastCheckpointEventId = ""
          LastCheckpointAt = 0L
          FailedDeliveryAttempts = 0
          Attempt = 0 }

    let private encode name (dto: ShipmentWire.WireDto) =
        try
            Ok(JsonSerializer.Serialize(dto, options))
        with ex ->
            Error(CodecError.EncodeError(name, ex))

    let private decode name (json: string) : Result<ShipmentWire.WireDto, CodecError> =
        try
            let dto = JsonSerializer.Deserialize<ShipmentWire.WireDto>(json, options)

            if isNull (box dto) then
                Error(codecError name "JSON decoded to null.")
            elif String.IsNullOrWhiteSpace dto.Tag then
                Error(codecError name "Missing tag.")
            else
                Ok dto
        with ex ->
            Error(CodecError.DecodeError(name, ex))

    let private guid name value =
        match Guid.TryParseExact(value, "D") with
        | true, id when id <> Guid.Empty -> Ok id
        | _ -> Error(codecError name "Expected a non-empty GUID in D format.")

    let private idValue parse name value =
        parse value |> Result.mapError (fun message -> codecError name message)

    let private sequenceResults values =
        values
        |> List.fold
            (fun state item -> state |> Result.bind (fun acc -> item |> Result.map (fun value -> value :: acc)))
            (Ok [])
        |> Result.map List.rev

    let private linesDto (lines: ShipmentLine list) =
        lines
        |> List.map (fun line ->
            ({ LineId = OrderLineId.wireString line.LineId
               Quantity = line.Quantity }
            : ShipmentWire.LineDto))
        |> List.toArray

    let private linesOfDto name (dtos: ShipmentWire.LineDto array) =
        (if isNull dtos then [] else Array.toList dtos)
        |> List.map (fun line ->
            guid name line.LineId
            |> Result.bind (OrderLineId.create >> Result.mapError (fun message -> codecError name message))
            |> Result.bind (fun lineId ->
                if line.Quantity > 0 then
                    Ok
                        { LineId = lineId
                          Quantity = line.Quantity }
                else
                    Error(codecError name "Quantity must be positive.")))
        |> sequenceResults

    let private requestDto (dto: ShipmentWire.WireDto) (request: ShipmentRequest) : ShipmentWire.WireDto =
        { dto with
            ShipmentId = ShipmentId.wireString request.ShipmentId
            AllocationId = ShipmentAllocationId.wireString request.AllocationId
            OrderId = request.OrderId
            Lines = linesDto request.Lines }

    let private requestOfDto name (dto: ShipmentWire.WireDto) : Result<ShipmentRequest, CodecError> =
        idValue ShipmentId.tryParse name dto.ShipmentId
        |> Result.bind (fun shipmentId ->
            idValue ShipmentAllocationId.tryParse name dto.AllocationId
            |> Result.bind (fun allocationId ->
                if String.IsNullOrWhiteSpace dto.OrderId then
                    Error(codecError name "Order id is required.")
                else
                    linesOfDto name dto.Lines
                    |> Result.bind (fun lines ->
                        if lines.IsEmpty then
                            Error(codecError name "A shipment must contain at least one line.")
                        else
                            Ok
                                { ShipmentId = shipmentId
                                  AllocationId = allocationId
                                  OrderId = dto.OrderId
                                  Lines = lines })))

    let private checkpointOfDto (dto: ShipmentWire.WireDto) =
        if String.IsNullOrWhiteSpace dto.LastCheckpointEventId then
            Ok None
        else
            idValue TrackingEventId.tryParse "Shipment" dto.LastCheckpointEventId
            |> Result.map (fun eventId ->
                Some
                    { EventId = eventId
                      OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds dto.LastCheckpointAt })

    let private preparingOfDto name (dto: ShipmentWire.WireDto) =
        requestOfDto name dto
        |> Result.map (fun request ->
            { Request = request
              LabelGeneration = dto.LabelGeneration })

    let private labelPendingOfDto name (dto: ShipmentWire.WireDto) =
        preparingOfDto name dto
        |> Result.map (fun preparing ->
            { Shipment = preparing
              Generation = dto.Generation })

    let private readyOfDto name (dto: ShipmentWire.WireDto) =
        preparingOfDto name dto
        |> Result.bind (fun preparing ->
            idValue CarrierReference.tryParse name dto.CarrierReference
            |> Result.map (fun reference ->
                { Shipment = preparing
                  CarrierReference = reference }))

    let private inTransitOfDto name (dto: ShipmentWire.WireDto) =
        readyOfDto name dto
        |> Result.bind (fun ready ->
            checkpointOfDto dto
            |> Result.map (fun checkpoint ->
                { Shipment = ready
                  TrackingGeneration = dto.TrackingGeneration
                  LastCheckpoint = checkpoint
                  FailedDeliveryAttempts = dto.FailedDeliveryAttempts }))

    let private completedOfDto name (dto: ShipmentWire.WireDto) =
        inTransitOfDto name dto
        |> Result.map (fun transit ->
            { Transit = transit
              CompletedAt = DateTimeOffset.FromUnixTimeMilliseconds dto.CompletedAt })

    let private failedOfDto name (dto: ShipmentWire.WireDto) =
        inTransitOfDto name dto
        |> Result.map (fun transit ->
            { Transit = transit
              ReasonCode = dto.ReasonCode })

    let private inTransitDto (dto: ShipmentWire.WireDto) (transit: ShipmentInTransit) : ShipmentWire.WireDto =
        let request = transit.Shipment.Shipment.Request

        let withCheckpoint =
            match transit.LastCheckpoint with
            | Some checkpoint ->
                { dto with
                    LastCheckpointEventId = TrackingEventId.value checkpoint.EventId
                    LastCheckpointAt = checkpoint.OccurredAt.ToUnixTimeMilliseconds() }
            | None -> dto

        { withCheckpoint with
            ShipmentId = ShipmentId.wireString request.ShipmentId
            AllocationId = ShipmentAllocationId.wireString request.AllocationId
            OrderId = request.OrderId
            Lines = linesDto request.Lines
            CarrierReference = CarrierReference.value transit.Shipment.CarrierReference
            LabelGeneration = transit.Shipment.Shipment.LabelGeneration
            TrackingGeneration = transit.TrackingGeneration
            FailedDeliveryAttempts = transit.FailedDeliveryAttempts }

    let private trackingStatusToString =
        function
        | CarrierTrackingStatus.InTransit -> "in-transit"
        | CarrierTrackingStatus.DeliveryFailed reason -> $"delivery-failed:{reason}"
        | CarrierTrackingStatus.Delivered -> "delivered"
        | CarrierTrackingStatus.ReturnedToSender -> "returned-to-sender"
        | CarrierTrackingStatus.Lost -> "lost"

    let private trackingStatusOfString =
        function
        | "in-transit" -> Ok CarrierTrackingStatus.InTransit
        | "delivered" -> Ok CarrierTrackingStatus.Delivered
        | "returned-to-sender" -> Ok CarrierTrackingStatus.ReturnedToSender
        | "lost" -> Ok CarrierTrackingStatus.Lost
        | value when value.StartsWith("delivery-failed:", StringComparison.Ordinal) ->
            Ok(CarrierTrackingStatus.DeliveryFailed value["delivery-failed:".Length ..])
        | _ -> Error "Unknown tracking status."

    let state: Codec<ShipmentState> =
        Codec.create
            (fun state ->
                let dto =
                    match state with
                    | AllocationPending None -> empty "allocation-pending-v1"
                    | AllocationPending(Some request) -> requestDto (empty "allocation-pending-v1") request
                    | Preparing preparing ->
                        requestDto (empty "preparing-v1") preparing.Request
                        |> fun dto ->
                            { dto with
                                LabelGeneration = preparing.LabelGeneration }
                    | LabelPending pending ->
                        requestDto (empty "label-pending-v1") pending.Shipment.Request
                        |> fun dto ->
                            { dto with
                                LabelGeneration = pending.Shipment.LabelGeneration
                                Generation = pending.Generation }
                    | ReadyToDispatch ready ->
                        requestDto (empty "ready-to-dispatch-v1") ready.Shipment.Request
                        |> fun dto ->
                            { dto with
                                LabelGeneration = ready.Shipment.LabelGeneration
                                CarrierReference = CarrierReference.value ready.CarrierReference }
                    | InTransit transit -> inTransitDto (empty "in-transit-v1") transit
                    | DeliveryFailed failed ->
                        inTransitDto (empty "delivery-failed-v1") failed.Transit
                        |> fun dto ->
                            { dto with
                                ReasonCode = failed.ReasonCode }
                    | ShipmentState.Delivered completed ->
                        inTransitDto (empty "delivered-v1") completed.Transit
                        |> fun dto ->
                            { dto with
                                CompletedAt = completed.CompletedAt.ToUnixTimeMilliseconds() }
                    | ShipmentState.ReturnedToSender completed ->
                        inTransitDto (empty "returned-to-sender-v1") completed.Transit
                        |> fun dto ->
                            { dto with
                                CompletedAt = completed.CompletedAt.ToUnixTimeMilliseconds() }
                    | ShipmentState.Lost completed ->
                        inTransitDto (empty "lost-v1") completed.Transit
                        |> fun dto ->
                            { dto with
                                CompletedAt = completed.CompletedAt.ToUnixTimeMilliseconds() }
                    | ManualReview(request, reason) ->
                        requestDto (empty "manual-review-v1") request
                        |> fun dto -> { dto with ReasonCode = reason }
                    | Closed request -> requestDto (empty "closed-v1") request

                encode "ShipmentState" dto)
            (fun json ->
                decode "ShipmentState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "allocation-pending-v1" ->
                        if String.IsNullOrWhiteSpace dto.ShipmentId then
                            Ok(AllocationPending None)
                        else
                            requestOfDto "ShipmentState" dto |> Result.map (Some >> AllocationPending)
                    | "preparing-v1" -> preparingOfDto "ShipmentState" dto |> Result.map Preparing
                    | "label-pending-v1" -> labelPendingOfDto "ShipmentState" dto |> Result.map LabelPending
                    | "ready-to-dispatch-v1" -> readyOfDto "ShipmentState" dto |> Result.map ReadyToDispatch
                    | "in-transit-v1" -> inTransitOfDto "ShipmentState" dto |> Result.map InTransit
                    | "delivery-failed-v1" -> failedOfDto "ShipmentState" dto |> Result.map DeliveryFailed
                    | "delivered-v1" -> completedOfDto "ShipmentState" dto |> Result.map ShipmentState.Delivered
                    | "returned-to-sender-v1" ->
                        completedOfDto "ShipmentState" dto |> Result.map ShipmentState.ReturnedToSender
                    | "lost-v1" -> completedOfDto "ShipmentState" dto |> Result.map ShipmentState.Lost
                    | "manual-review-v1" when not (String.IsNullOrWhiteSpace dto.ReasonCode) ->
                        requestOfDto "ShipmentState" dto
                        |> Result.map (fun request -> ManualReview(request, dto.ReasonCode))
                    | "closed-v1" -> requestOfDto "ShipmentState" dto |> Result.map Closed
                    | tag -> Error(codecError "ShipmentState" $"Unknown or invalid tag '{tag}'.")))

    let event: Codec<ShipmentEvent> =
        Codec.create
            (fun event ->
                let dto =
                    match event with
                    | ShipmentRequested request -> requestDto (empty "shipment-requested-v1") request
                    | AllocationConfirmed allocationId ->
                        { empty "allocation-confirmed-v1" with
                            AllocationId = ShipmentAllocationId.wireString allocationId }
                    | AllocationRejected(allocationId, reason) ->
                        { empty "allocation-rejected-v1" with
                            AllocationId = ShipmentAllocationId.wireString allocationId
                            ReasonCode = reason }
                    | PreparationCompleted -> empty "preparation-completed-v1"
                    | LabelCreated(generation, reference) ->
                        { empty "label-created-v1" with
                            Generation = generation
                            CarrierReference = CarrierReference.value reference }
                    | LabelCreationFailed(generation, reason) ->
                        { empty "label-creation-failed-v1" with
                            Generation = generation
                            ReasonCode = reason }
                    | DispatchConfirmed dispatchedAt ->
                        { empty "dispatch-confirmed-v1" with
                            DispatchedAt = dispatchedAt.ToUnixTimeMilliseconds() }
                    | CarrierTrackingReceived update ->
                        { empty "carrier-tracking-received-v1" with
                            EventId = TrackingEventId.value update.EventId
                            Generation = update.Generation
                            OccurredAt = update.OccurredAt.ToUnixTimeMilliseconds()
                            Status = trackingStatusToString update.Status }
                    | DeliveryRetryRequested -> empty "delivery-retry-requested-v1"
                    | MarkLost(generation, expectedLastScan, detectedAt) ->
                        { empty "mark-lost-v1" with
                            ExpectedGeneration = generation
                            ExpectedLastScanAt =
                                (match expectedLastScan with
                                 | Some scan -> scan.ToUnixTimeMilliseconds()
                                 | None -> 0L)
                            DetectedAt = detectedAt.ToUnixTimeMilliseconds() }
                    | ManualReviewRequested reason ->
                        { empty "manual-review-requested-v1" with
                            ReasonCode = reason }
                    | ShipmentCloseRequested -> empty "shipment-close-requested-v1"

                encode "ShipmentEvent" dto)
            (fun json ->
                decode "ShipmentEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "shipment-requested-v1" -> requestOfDto "ShipmentEvent" dto |> Result.map ShipmentRequested
                    | "allocation-confirmed-v1" ->
                        idValue ShipmentAllocationId.tryParse "ShipmentEvent" dto.AllocationId
                        |> Result.map AllocationConfirmed
                    | "allocation-rejected-v1" when not (String.IsNullOrWhiteSpace dto.ReasonCode) ->
                        idValue ShipmentAllocationId.tryParse "ShipmentEvent" dto.AllocationId
                        |> Result.map (fun allocationId -> AllocationRejected(allocationId, dto.ReasonCode))
                    | "preparation-completed-v1" -> Ok PreparationCompleted
                    | "label-created-v1" ->
                        idValue CarrierReference.tryParse "ShipmentEvent" dto.CarrierReference
                        |> Result.map (fun reference -> LabelCreated(dto.Generation, reference))
                    | "label-creation-failed-v1" when not (String.IsNullOrWhiteSpace dto.ReasonCode) ->
                        Ok(LabelCreationFailed(dto.Generation, dto.ReasonCode))
                    | "dispatch-confirmed-v1" ->
                        Ok(DispatchConfirmed(DateTimeOffset.FromUnixTimeMilliseconds dto.DispatchedAt))
                    | "carrier-tracking-received-v1" ->
                        idValue TrackingEventId.tryParse "ShipmentEvent" dto.EventId
                        |> Result.bind (fun eventId ->
                            trackingStatusOfString dto.Status
                            |> Result.mapError (fun message -> codecError "ShipmentEvent" message)
                            |> Result.map (fun status ->
                                CarrierTrackingReceived
                                    { EventId = eventId
                                      Generation = dto.Generation
                                      OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds dto.OccurredAt
                                      Status = status }))
                    | "delivery-retry-requested-v1" -> Ok DeliveryRetryRequested
                    | "mark-lost-v1" ->
                        let expectedLastScan =
                            if dto.ExpectedLastScanAt = 0L then
                                None
                            else
                                Some(DateTimeOffset.FromUnixTimeMilliseconds dto.ExpectedLastScanAt)

                        Ok(
                            MarkLost(
                                dto.ExpectedGeneration,
                                expectedLastScan,
                                DateTimeOffset.FromUnixTimeMilliseconds dto.DetectedAt
                            )
                        )
                    | "manual-review-requested-v1" when not (String.IsNullOrWhiteSpace dto.ReasonCode) ->
                        Ok(ManualReviewRequested dto.ReasonCode)
                    | "shipment-close-requested-v1" -> Ok ShipmentCloseRequested
                    | tag -> Error(codecError "ShipmentEvent" $"Unknown tag '{tag}'.")))

    let action: Codec<ShipmentAction> =
        Codec.create
            (fun action ->
                match action with
                | ConfirmAllocation allocationId ->
                    encode
                        "ShipmentAction"
                        { empty "confirm-allocation-v1" with
                            AllocationId = ShipmentAllocationId.wireString allocationId }
                | CreateCarrierLabel(shipmentId, generation) ->
                    encode
                        "ShipmentAction"
                        { empty "create-carrier-label-v1" with
                            ShipmentId = ShipmentId.wireString shipmentId
                            Generation = generation }
                | NotifyOrderDispatched(orderId, shipmentId, allocationId, dispatchedAt) ->
                    encode
                        "ShipmentAction"
                        { empty "notify-order-dispatched-v1" with
                            OrderId = orderId
                            ShipmentId = ShipmentId.wireString shipmentId
                            AllocationId = ShipmentAllocationId.wireString allocationId
                            DispatchedAt = dispatchedAt.ToUnixTimeMilliseconds() }
                | NotifyOrderDelivered(orderId, shipmentId, allocationId, deliveredAt) ->
                    encode
                        "ShipmentAction"
                        { empty "notify-order-delivered-v1" with
                            OrderId = orderId
                            ShipmentId = ShipmentId.wireString shipmentId
                            AllocationId = ShipmentAllocationId.wireString allocationId
                            DispatchedAt = deliveredAt.ToUnixTimeMilliseconds() }
                | RequestDeliveryRetry(reference, attempt) ->
                    encode
                        "ShipmentAction"
                        { empty "request-delivery-retry-v1" with
                            CarrierReference = CarrierReference.value reference
                            Attempt = attempt })
            (fun json ->
                decode "ShipmentAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "confirm-allocation-v1" ->
                        idValue ShipmentAllocationId.tryParse "ShipmentAction" dto.AllocationId
                        |> Result.map ConfirmAllocation
                    | "create-carrier-label-v1" ->
                        idValue ShipmentId.tryParse "ShipmentAction" dto.ShipmentId
                        |> Result.map (fun shipmentId -> CreateCarrierLabel(shipmentId, dto.Generation))
                    | "notify-order-dispatched-v1" when not (String.IsNullOrWhiteSpace dto.OrderId) ->
                        idValue ShipmentId.tryParse "ShipmentAction" dto.ShipmentId
                        |> Result.bind (fun shipmentId ->
                            idValue ShipmentAllocationId.tryParse "ShipmentAction" dto.AllocationId
                            |> Result.map (fun allocationId ->
                                NotifyOrderDispatched(
                                    dto.OrderId,
                                    shipmentId,
                                    allocationId,
                                    DateTimeOffset.FromUnixTimeMilliseconds dto.DispatchedAt
                                )))
                    | "notify-order-delivered-v1" when not (String.IsNullOrWhiteSpace dto.OrderId) ->
                        idValue ShipmentId.tryParse "ShipmentAction" dto.ShipmentId
                        |> Result.bind (fun shipmentId ->
                            idValue ShipmentAllocationId.tryParse "ShipmentAction" dto.AllocationId
                            |> Result.map (fun allocationId ->
                                NotifyOrderDelivered(
                                    dto.OrderId,
                                    shipmentId,
                                    allocationId,
                                    DateTimeOffset.FromUnixTimeMilliseconds dto.DispatchedAt
                                )))
                    | "request-delivery-retry-v1" ->
                        idValue CarrierReference.tryParse "ShipmentAction" dto.CarrierReference
                        |> Result.map (fun reference -> RequestDeliveryRetry(reference, dto.Attempt))
                    | tag -> Error(codecError "ShipmentAction" $"Unknown tag '{tag}'.")))

    let error: Codec<ShipmentActionError> =
        Codec.create
            (fun errorValue ->
                let tag =
                    match errorValue with
                    | ShipmentActionError.InvalidShipmentEntityId -> "invalid-shipment-entity-id-v1"
                    | ShipmentActionError.AllocationLookupFailed -> "allocation-lookup-failed-v1"
                    | ShipmentActionError.AllocationMismatch -> "allocation-mismatch-v1"
                    | ShipmentActionError.CarrierRequestFailed -> "carrier-request-failed-v1"
                    | ShipmentActionError.CallbackEncodingFailed -> "callback-encoding-failed-v1"
                    | ShipmentActionError.ActionReceiptMismatch -> "action-receipt-mismatch-v1"
                    | ShipmentActionError.InvalidAction -> "invalid-action-v1"

                encode "ShipmentActionError" (empty tag))
            (fun json ->
                decode "ShipmentActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "invalid-shipment-entity-id-v1" -> Ok ShipmentActionError.InvalidShipmentEntityId
                    | "allocation-lookup-failed-v1" -> Ok ShipmentActionError.AllocationLookupFailed
                    | "allocation-mismatch-v1" -> Ok ShipmentActionError.AllocationMismatch
                    | "carrier-request-failed-v1" -> Ok ShipmentActionError.CarrierRequestFailed
                    | "callback-encoding-failed-v1" -> Ok ShipmentActionError.CallbackEncodingFailed
                    | "action-receipt-mismatch-v1" -> Ok ShipmentActionError.ActionReceiptMismatch
                    | "invalid-action-v1" -> Ok ShipmentActionError.InvalidAction
                    | tag -> Error(codecError "ShipmentActionError" $"Unknown tag '{tag}'.")))

    let storeOptions
        (context: PostgresContext)
        : MachineStoreOptions<ShipmentEntityId, ShipmentState, ShipmentEvent, ShipmentAction, ShipmentActionError> =
        { MachineStoreOptions.forEntityId<Shipment, ShipmentState, ShipmentEvent, ShipmentAction, ShipmentActionError>
              context
              Shipments.ActionQueue with
            StateCodec = state
            EventCodec = event
            ActionCodec = action
            ErrorCodec = error }

    let workerStore context =
        storeOptions context |> PostgresMachineStore

    let clientStore context =
        { storeOptions context with
            Listener = ListenerConnection.Off }
        |> PostgresMachineStore

    let private build (log: ILogger) storeArg =
        machine<ShipmentEntityId, ShipmentState, ShipmentEvent, ShipmentAction, ShipmentActionError> (
            machineId Shipments.MachineKey
        ) {
            chart Shipments.chartValue
            chartVersion 1
            initialState Shipments.initialState
            store storeArg
            logger log
        }

    let buildWorker log context =
        build
            log
            (workerStore context
            :> IMachineStore<ShipmentEntityId, ShipmentState, ShipmentEvent, ShipmentAction, ShipmentActionError>)

    let buildClient log context =
        build
            log
            (clientStore context
            :> IMachineStore<ShipmentEntityId, ShipmentState, ShipmentEvent, ShipmentAction, ShipmentActionError>)
