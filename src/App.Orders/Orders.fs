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

type OrderAction =
    | ReserveStock of ReservationPendingOrder
    | ReleaseReservations of ReservationId list
    | NotifyCartConverted of cartId: string
    | RequestAuthorization of method: PaymentMethodReference * attempt: PaymentOperationId * amount: Money
    | RequestPaymentCancellation of reason: string
    | CommitStock of ReservationId list

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
        | StockCommitFailed _ -> true
        | OrderSubmitted _
        | CancelRequested
        | AuthorizePaymentRequested _
        | MarkManualReview _ -> false

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

            state "placed" { internalOn absorb (fun _ _ -> []) }

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
