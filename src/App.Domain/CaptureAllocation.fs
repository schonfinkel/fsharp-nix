namespace App.Domain

open NodaMoney

type CaptureAllocationOrderLine =
    { LineId: OrderLineId
      UnitPrice: Money
      Quantity: int }

type ShipmentLineAllocation = { LineId: OrderLineId; Quantity: int }

type ShipmentAllocationRequest =
    { AllocationId: ShipmentAllocationId
      Lines: ShipmentLineAllocation list }

type CaptureAllocationRequest =
    { OrderLines: CaptureAllocationOrderLine list
      Shipments: ShipmentAllocationRequest list
      Shipping: Money
      Tax: Money
      AuthorizedAmount: Money }

type ShipmentCaptureAllocation =
    { AllocationId: ShipmentAllocationId
      Lines: ShipmentLineAllocation list
      Merchandise: Money
      Shipping: Money
      Tax: Money
      Total: Money }

[<RequireQualifiedAccess>]
type CaptureAllocationError =
    | EmptyOrder
    | EmptyShipments
    | EmptyShipment of ShipmentAllocationId
    | DuplicateOrderLineId of OrderLineId
    | DuplicateShipmentAllocationId of ShipmentAllocationId
    | DuplicateAllocatedLineId of ShipmentAllocationId * OrderLineId
    | UnknownLineId of ShipmentAllocationId * OrderLineId
    | NonPositiveOrderQuantity of OrderLineId * int
    | NonPositiveAllocatedQuantity of ShipmentAllocationId * OrderLineId * int
    | OverAllocatedLine of OrderLineId * ordered: int * allocated: int
    | UnderAllocatedLine of OrderLineId * ordered: int * allocated: int
    | CurrencyMismatch of expected: string * actual: string
    | AuthorizedAmountMismatch of expected: Money * actual: Money

[<RequireQualifiedAccess>]
module CaptureAllocation =
    let private duplicateBy selector values =
        values
        |> List.countBy selector
        |> List.tryPick (fun (key, count) -> if count > 1 then Some key else None)

    let private sumMoney currency values =
        values
        |> List.fold Money.add (Money.zero currency |> Result.defaultWith invalidOp)

    let private currencyError expected (money: Money) =
        let actual = Money.currencyCode money

        if actual = expected then
            None
        else
            Some(CaptureAllocationError.CurrencyMismatch(expected, actual))

    /// <summary>Allocates a complete order across shipments in caller-supplied order. Shipping
    /// and tax are proportional to merchandise value; for a zero-value order they are
    /// proportional to quantity. The final shipment receives each rounded remainder.</summary>
    let allocate (request: CaptureAllocationRequest) : Result<ShipmentCaptureAllocation list, CaptureAllocationError> =
        if request.OrderLines.IsEmpty then
            Error CaptureAllocationError.EmptyOrder
        elif request.Shipments.IsEmpty then
            Error CaptureAllocationError.EmptyShipments
        else
            match duplicateBy (fun (line: CaptureAllocationOrderLine) -> line.LineId) request.OrderLines with
            | Some lineId -> Error(CaptureAllocationError.DuplicateOrderLineId lineId)
            | None ->
                match
                    duplicateBy (fun (shipment: ShipmentAllocationRequest) -> shipment.AllocationId) request.Shipments
                with
                | Some allocationId -> Error(CaptureAllocationError.DuplicateShipmentAllocationId allocationId)
                | None ->
                    let expectedCurrency = Money.currencyCode request.AuthorizedAmount

                    let allMoney =
                        [ request.Shipping
                          request.Tax
                          yield! request.OrderLines |> List.map _.UnitPrice ]

                    match allMoney |> List.tryPick (currencyError expectedCurrency) with
                    | Some error -> Error error
                    | None ->
                        match request.OrderLines |> List.tryFind (fun line -> line.Quantity <= 0) with
                        | Some line ->
                            Error(CaptureAllocationError.NonPositiveOrderQuantity(line.LineId, line.Quantity))
                        | None ->
                            match request.Shipments |> List.tryFind _.Lines.IsEmpty with
                            | Some shipment -> Error(CaptureAllocationError.EmptyShipment shipment.AllocationId)
                            | None ->
                                let duplicateAllocationLine =
                                    request.Shipments
                                    |> List.tryPick (fun shipment ->
                                        duplicateBy _.LineId shipment.Lines
                                        |> Option.map (fun lineId -> shipment.AllocationId, lineId))

                                match duplicateAllocationLine with
                                | Some(allocationId, lineId) ->
                                    Error(CaptureAllocationError.DuplicateAllocatedLineId(allocationId, lineId))
                                | None ->
                                    let orderLines =
                                        request.OrderLines
                                        |> List.map (fun (line: CaptureAllocationOrderLine) -> line.LineId, line)
                                        |> Map.ofList

                                    let allocatedLines =
                                        request.Shipments
                                        |> List.collect (fun shipment ->
                                            shipment.Lines |> List.map (fun line -> shipment.AllocationId, line))

                                    match allocatedLines |> List.tryFind (fun (_, line) -> line.Quantity <= 0) with
                                    | Some(allocationId, line) ->
                                        Error(
                                            CaptureAllocationError.NonPositiveAllocatedQuantity(
                                                allocationId,
                                                line.LineId,
                                                line.Quantity
                                            )
                                        )
                                    | None ->
                                        match
                                            allocatedLines
                                            |> List.tryFind (fun (_, line) ->
                                                not (Map.containsKey line.LineId orderLines))
                                        with
                                        | Some(allocationId, line) ->
                                            Error(CaptureAllocationError.UnknownLineId(allocationId, line.LineId))
                                        | None ->
                                            let allocatedQuantities =
                                                allocatedLines
                                                |> List.groupBy (fun (_, line) -> line.LineId)
                                                |> List.map (fun (lineId, lines) ->
                                                    lineId, lines |> List.sumBy (fun (_, line) -> line.Quantity))
                                                |> Map.ofList

                                            let quantityError =
                                                request.OrderLines
                                                |> List.tryPick (fun line ->
                                                    let allocated =
                                                        allocatedQuantities
                                                        |> Map.tryFind line.LineId
                                                        |> Option.defaultValue 0

                                                    if allocated > line.Quantity then
                                                        Some(
                                                            CaptureAllocationError.OverAllocatedLine(
                                                                line.LineId,
                                                                line.Quantity,
                                                                allocated
                                                            )
                                                        )
                                                    elif allocated < line.Quantity then
                                                        Some(
                                                            CaptureAllocationError.UnderAllocatedLine(
                                                                line.LineId,
                                                                line.Quantity,
                                                                allocated
                                                            )
                                                        )
                                                    else
                                                        None)

                                            match quantityError with
                                            | Some error -> Error error
                                            | None ->
                                                let merchandiseFor (shipment: ShipmentAllocationRequest) =
                                                    shipment.Lines
                                                    |> List.map (fun allocation ->
                                                        let line = Map.find allocation.LineId orderLines
                                                        Money.multiply line.UnitPrice (decimal allocation.Quantity))
                                                    |> sumMoney expectedCurrency

                                                let shipmentMerchandise =
                                                    request.Shipments
                                                    |> List.map (fun shipment -> shipment, merchandiseFor shipment)

                                                let merchandiseTotal =
                                                    shipmentMerchandise |> List.map snd |> sumMoney expectedCurrency

                                                let expectedTotal = merchandiseTotal + request.Shipping + request.Tax

                                                if expectedTotal <> request.AuthorizedAmount then
                                                    Error(
                                                        CaptureAllocationError.AuthorizedAmountMismatch(
                                                            expectedTotal,
                                                            request.AuthorizedAmount
                                                        )
                                                    )
                                                else
                                                    let totalQuantity =
                                                        request.OrderLines |> List.sumBy _.Quantity |> decimal

                                                    let weight (shipment: ShipmentAllocationRequest) merchandise =
                                                        if Money.amount merchandise <> 0m then
                                                            Money.amount merchandise / Money.amount merchandiseTotal
                                                        elif Money.amount merchandiseTotal = 0m then
                                                            decimal (shipment.Lines |> List.sumBy _.Quantity)
                                                            / totalQuantity
                                                        else
                                                            0m

                                                    let rec build
                                                        (allocatedShipping: Money)
                                                        (allocatedTax: Money)
                                                        (remaining: (ShipmentAllocationRequest * Money) list)
                                                        : ShipmentCaptureAllocation list =
                                                        match remaining with
                                                        | [] -> []
                                                        | [ shipment, merchandise ] ->
                                                            let shipping = request.Shipping - allocatedShipping
                                                            let tax = request.Tax - allocatedTax

                                                            [ { AllocationId = shipment.AllocationId
                                                                Lines = shipment.Lines
                                                                Merchandise = merchandise
                                                                Shipping = shipping
                                                                Tax = tax
                                                                Total = merchandise + shipping + tax } ]
                                                        | (shipment, merchandise) :: tail ->
                                                            let share = weight shipment merchandise
                                                            let shipping = Money.multiply request.Shipping share
                                                            let tax = Money.multiply request.Tax share

                                                            { AllocationId = shipment.AllocationId
                                                              Lines = shipment.Lines
                                                              Merchandise = merchandise
                                                              Shipping = shipping
                                                              Tax = tax
                                                              Total = merchandise + shipping + tax }
                                                            :: build
                                                                (allocatedShipping + shipping)
                                                                (allocatedTax + tax)
                                                                tail

                                                    let zero =
                                                        Money.zero expectedCurrency |> Result.defaultWith invalidOp

                                                    Ok(build zero zero shipmentMerchandise)
