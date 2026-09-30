namespace App.Returns

open System
open App.Domain
open ByzantineSystems.Automata.Core

type ReturnProgress =
    { Request: ReturnRequest
      Received: (OrderLineId * int) list
      Restocked: (OrderLineId * int) list
      RestockSettled: bool
      LastScan: ReturnTrackingEventId option }

type ReturnState =
    | Initial
    | AuthorizationPending of ReturnProgress
    | Approved of ReturnProgress
    | Rejected of ReturnProgress * reasonCode: ReasonCode
    | LabelPending of ReturnProgress
    | LabelIssued of ReturnProgress * CarrierReference
    | InTransit of ReturnProgress * CarrierReference
    | Received of ReturnProgress * CarrierReference
    | Inspected of ReturnProgress * CarrierReference
    | RefundPending of ReturnProgress * RefundRequest
    | Refunded of ReturnProgress * RefundRequest
    | RejectedAfterInspection of ReturnProgress * reasonCode: ReasonCode
    | ManualReview of ReturnProgress * reasonCode: ReasonCode
    | Closed of ReturnProgress

type ReturnEvent =
    | ReturnRequested of ReturnRequest
    | AuthorizationApproved of ReturnAuthorizationId
    | AuthorizationRejected of ReturnAuthorizationId * reasonCode: ReasonCode
    | LabelCreated of CarrierReference
    | LabelFailed of reasonCode: ReasonCode
    | CarrierScanReceived of ReturnTrackingEventId
    | ItemsReceived of (OrderLineId * int) list
    | InspectionApproved of (OrderLineId * int) list
    | RestockCompleted of ReturnId
    | InspectionRejected of reasonCode: ReasonCode
    | RefundSucceeded of RefundId
    | RefundFailed of RefundId * reasonCode: ReasonCode
    | ReturnWindowExpired of ReturnAuthorizationId * DateTimeOffset
    /// <summary>Operator decision after inspection and restock: refund the inspected items.</summary>
    | RefundStartRequested
    /// <summary>Operator/scanner closure of a resolved RMA; valid only once nothing is pending.</summary>
    | CloseRequested

type ReturnAction =
    | VerifyAuthorization of ReturnRequest
    | IssueLabel of ReturnRequest
    | RestockItems of ReturnRequest * (OrderLineId * int) list
    | RequestRefund of RefundRequest
    | NotifyOrderRefunded of ReturnRequest
    | NotifyOrderRejected of ReturnRequest

[<RequireQualifiedAccess>]
type ReturnActionError =
    | CallbackEncodingFailed
    | ActionReceiptMismatch
    | InvalidAction

type Return = class end
type ReturnEntityId = EntityId<Return>

[<RequireQualifiedAccess>]
module Returns =
    [<Literal>]
    let MachineKey = "returns"

    [<Literal>]
    let ActionQueue = "return_actions"

    /// Bump on every semantic chart change (guards, transitions, codecs), even when the
    /// structure is unchanged; Automata's fingerprint cannot see inside functions.
    [<Literal>]
    let ChartVersion = 1

    let initialState = Initial

    let returnEntityId (id: ReturnId) : ReturnEntityId =
        entityId $"return:{ReturnId.wireString id}"

    let classifyState =
        function
        | Initial -> stateId "initial"
        | AuthorizationPending _ -> stateId "authorization-pending"
        | Approved _ -> stateId "approved"
        | Rejected _ -> stateId "rejected"
        | LabelPending _ -> stateId "label-pending"
        | LabelIssued _ -> stateId "label-issued"
        | InTransit _ -> stateId "in-transit"
        | Received _ -> stateId "received"
        | Inspected _ -> stateId "inspected"
        | RefundPending _ -> stateId "refund-pending"
        | Refunded _ -> stateId "refunded"
        | RejectedAfterInspection _ -> stateId "rejected-after-inspection"
        | ManualReview _ -> stateId "manual-review"
        | Closed _ -> stateId "closed"

    let private progress =
        function
        | AuthorizationPending p
        | Approved p
        | LabelPending p
        | Rejected(p, _)
        | LabelIssued(p, _)
        | InTransit(p, _)
        | Received(p, _)
        | Inspected(p, _)
        | RefundPending(p, _)
        | Refunded(p, _)
        | RejectedAfterInspection(p, _)
        | ManualReview(p, _)
        | Closed p -> Some p
        | Initial -> None

    let private validQuantities
        (request: ReturnRequest)
        (current: (OrderLineId * int) list)
        (quantities: (OrderLineId * int) list)
        =
        not quantities.IsEmpty
        && quantities |> List.countBy fst |> List.forall (fun (_, count) -> count = 1)
        && quantities
           |> List.forall (fun (lineId, quantity) ->
               let allowed =
                   request.Lines
                   |> List.tryFind (fun line -> line.OrderLineId = lineId)
                   |> Option.map _.Quantity

               let already =
                   current
                   |> List.tryFind (fun (id, _) -> id = lineId)
                   |> Option.map snd
                   |> Option.defaultValue 0

               allowed
               |> Option.exists (fun maximum -> quantity > 0 && already + quantity <= maximum))

    let private addQuantities current quantities =
        let map = current |> Map.ofList

        quantities
        |> List.fold
            (fun acc (line, quantity) ->
                acc
                |> Map.add line (quantity + (acc |> Map.tryFind line |> Option.defaultValue 0)))
            map
        |> Map.toList

    let private beginReturn state event =
        match state, event with
        | Initial, ReturnRequested request -> Result.isOk (ReturnRequest.validate request)
        | _ -> false

    let private authorization state event =
        match state, event with
        | AuthorizationPending p, AuthorizationApproved id
        | AuthorizationPending p, AuthorizationRejected(id, _) -> p.Request.AuthorizationId = id
        | _ -> false

    let private label state event =
        match state, event with
        | LabelPending _, LabelCreated _
        | LabelPending _, LabelFailed _ -> true
        | _ -> false

    let private scan state event =
        match state, event with
        | (LabelIssued(p, _) | InTransit(p, _)), CarrierScanReceived id -> p.LastScan <> Some id
        | _ -> false

    let private receipt state event =
        match state, event with
        | InTransit(p, _), ItemsReceived quantities
        | Received(p, _), ItemsReceived quantities -> validQuantities p.Request p.Received quantities
        | _ -> false

    let private inspect state event =
        match state, event with
        | Received(p, _), InspectionApproved quantities ->
            validQuantities p.Request p.Restocked quantities
            && p.Request.Lines
               |> List.forall (fun line ->
                   let received =
                       p.Received
                       |> List.tryFind (fun (id, _) -> id = line.OrderLineId)
                       |> Option.map snd
                       |> Option.defaultValue 0

                   let approved =
                       quantities
                       |> List.tryFind (fun (id, _) -> id = line.OrderLineId)
                       |> Option.map snd
                       |> Option.defaultValue 0

                   received = line.Quantity && approved = line.Quantity)
            && quantities
               |> List.forall (fun (line, count) ->
                   let received =
                       p.Received
                       |> List.tryFind (fun (id, _) -> id = line)
                       |> Option.map snd
                       |> Option.defaultValue 0

                   let already =
                       p.Restocked
                       |> List.tryFind (fun (id, _) -> id = line)
                       |> Option.map snd
                       |> Option.defaultValue 0

                   count + already <= received)
        | Received _, InspectionRejected reason ->
            (ignore reason
             true)
        | _ -> false

    let private refundResult state event =
        match state, event with
        | RefundPending(_, request), RefundSucceeded id
        | RefundPending(_, request), RefundFailed(id, _) -> request.RefundId = id
        | _ -> false

    let private expiry state event =
        match progress state, event with
        | Some p, ReturnWindowExpired(id, deadline) ->
            p.Request.AuthorizationId = id && p.Request.WindowEndsAt = deadline
        | _ -> false

    let private closeRequested _ event = event = CloseRequested

    /// <summary>Closes a resolved RMA. Only resolved states route here, so no pending effect
    /// can be orphaned by closing.</summary>
    let private close state _ =
        match state with
        | ReturnState.Rejected(p, _)
        | Refunded(p, _)
        | RejectedAfterInspection(p, _) -> [], Closed p
        | _ -> [], state

    let private absorb _ =
        function
        | AuthorizationApproved _
        | AuthorizationRejected _
        | LabelCreated _
        | LabelFailed _
        | CarrierScanReceived _
        | RefundSucceeded _
        | RefundFailed _
        | RestockCompleted _
        | ReturnWindowExpired _ -> true
        | _ -> false

    let chartResult =
        statechart<ReturnState, ReturnEvent, ReturnAction, ReturnActionError> {
            root MachineKey
            classify classifyState

            state "initial" {
                on beginReturn (fun _ event ->
                    match event with
                    | ReturnRequested request ->
                        [ VerifyAuthorization request ],
                        AuthorizationPending
                            { Request = request
                              Received = []
                              Restocked = []
                              RestockSettled = false
                              LastScan = None }
                    | _ -> [], Initial)

                internalOn absorb (fun _ _ -> [])
            }

            state "authorization-pending" {
                on authorization (fun state event ->
                    match state, event with
                    | AuthorizationPending p, AuthorizationApproved _ -> [ IssueLabel p.Request ], LabelPending p
                    | AuthorizationPending p, AuthorizationRejected(_, reason) ->
                        [ NotifyOrderRejected p.Request ], Rejected(p, reason)
                    | _ -> [], state)

                on expiry (fun state _ ->
                    match state with
                    | AuthorizationPending p ->
                        [ NotifyOrderRejected p.Request ], Rejected(p, (ReasonCode.ofLiteral "window-expired"))
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "approved" { internalOn absorb (fun _ _ -> []) }

            state "rejected" {
                on closeRequested close
                internalOn absorb (fun _ _ -> [])
            }

            state "label-pending" {
                on label (fun state event ->
                    match state, event with
                    | LabelPending p, LabelCreated reference -> [], LabelIssued(p, reference)
                    | LabelPending p, LabelFailed reason -> [], ManualReview(p, reason)
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "label-issued" {
                on scan (fun state event ->
                    match state, event with
                    | LabelIssued(p, reference), CarrierScanReceived id ->
                        [], InTransit({ p with LastScan = Some id }, reference)
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "in-transit" {
                on scan (fun state event ->
                    match state, event with
                    | InTransit(p, reference), CarrierScanReceived id ->
                        [], InTransit({ p with LastScan = Some id }, reference)
                    | _ -> [], state)

                on receipt (fun state event ->
                    match state, event with
                    | InTransit(p, reference), ItemsReceived quantities ->
                        [],
                        Received(
                            { p with
                                Received = addQuantities p.Received quantities },
                            reference
                        )
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "received" {
                on receipt (fun state event ->
                    match state, event with
                    | Received(p, reference), ItemsReceived quantities ->
                        [],
                        Received(
                            { p with
                                Received = addQuantities p.Received quantities },
                            reference
                        )
                    | _ -> [], state)

                on inspect (fun state event ->
                    match state, event with
                    | Received(p, reference), InspectionApproved quantities ->
                        let updated =
                            { p with
                                Restocked = addQuantities p.Restocked quantities }

                        [ RestockItems(p.Request, quantities) ], Inspected(updated, reference)
                    | Received(p, _), InspectionRejected reason ->
                        [ NotifyOrderRejected p.Request ], RejectedAfterInspection(p, reason)
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "inspected" {
                on
                    (fun state event ->
                        match state, event with
                        | Inspected(p, _), RestockCompleted id -> p.Request.ReturnId = id && not p.RestockSettled
                        | _ -> false)
                    (fun state _ ->
                        match state with
                        | Inspected(p, reference) -> [], Inspected({ p with RestockSettled = true }, reference)
                        | _ -> [], state)

                on
                    (fun state event ->
                        match state, event with
                        | Inspected(p, _), RefundStartRequested -> p.RestockSettled
                        | _ -> false)
                    (fun state _ ->
                        match state with
                        | Inspected(p, _) ->
                            let id = ReturnId.value p.Request.ReturnId

                            let request =
                                { RefundId = RefundId.create id |> Result.defaultWith invalidOp
                                  AllocationId = RefundAllocationId.create id |> Result.defaultWith invalidOp
                                  OperationId =
                                    PaymentOperationId.create $"refund:return:{id:D}"
                                    |> Result.defaultWith invalidOp
                                  Origin = InspectedReturn(p.Request.OrderId, id)
                                  Amount = ReturnRequest.total p.Request }

                            [ RequestRefund request ], RefundPending(p, request)
                        | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "refund-pending" {
                on refundResult (fun state event ->
                    match state, event with
                    | RefundPending(p, request), RefundSucceeded _ ->
                        [ NotifyOrderRefunded p.Request ], Refunded(p, request)
                    | RefundPending(p, _), RefundFailed(_, reason) -> [], ManualReview(p, reason)
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "refunded" {
                on closeRequested close
                internalOn absorb (fun _ _ -> [])
            }

            state "rejected-after-inspection" {
                on closeRequested close
                internalOn absorb (fun _ _ -> [])
            }

            state "manual-review" { internalOn absorb (fun _ _ -> []) }
            state "closed" { internalOn absorb (fun _ _ -> []) }
        }

    let chartValue =
        match chartResult with
        | Ok chart -> chart
        | Error errors -> invalidOp $"returns chart is invalid: %A{errors}"
