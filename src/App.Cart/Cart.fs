namespace App.Cart

open System
open App.Domain
open ByzantineSystems.Automata.Core
open NodaMoney

/// <summary>The epoch carried by a cart belief. Every successful client mutation increments
/// it; a mutation whose expected epoch does not match is a stale conflict, never applied.</summary>
type CartEpoch = int64

/// <summary>One cart line, carrying a price snapshot (unit price + currency) captured when the
/// line was added, so a later catalog price change never retroactively alters an open cart.</summary>
type CartLine =
    { ProductId: ProductId
      Sku: Sku
      Name: NonEmptyString
      UnitPrice: Money
      Quantity: Quantity }

/// <summary>A cart carrying lines and its current epoch.</summary>
type ActiveCart =
    { Epoch: CartEpoch
      Lines: CartLine list }

/// <summary>A guest cart frozen for a customer merge. <c>Submitted</c> tracks whether the
/// snapshot has been handed to the target cart; until it is, the reconciler may retry.</summary>
type FrozenCart =
    { Epoch: CartEpoch
      Lines: CartLine list
      MergeId: Guid
      Target: string
      Submitted: bool }

type MergedCart = { MergeId: Guid; Target: string }

/// <summary>
/// One cart's belief. <c>Empty</c> is the initial state; once a cart becomes <c>Active</c> it
/// never returns to <c>Empty</c> — an emptied cart is <c>Active</c> with no lines and a bumped
/// epoch, so optimistic concurrency stays monotonic. <c>Converted</c> is defined now but
/// unreachable until the order machine (P3) converts a cart. <c>MergedInto</c> and
/// <c>Converted</c> are terminal for the guest cart.
/// </summary>
type CartState =
    | Empty
    | Active of ActiveCart
    | MergeFrozen of FrozenCart
    | MergedInto of MergedCart
    | Converted
    | Abandoned of ActiveCart

/// <summary>Events of the <c>carts</c> machine. Every client mutation carries the expected
/// epoch; stale epochs are absorbed as no-ops so a late or duplicate command never corrupts
/// the cart. <c>ApplyMerge</c> is the customer-side command submitted by the merge saga. No
/// bearer capability or secret ever appears here.</summary>
type CartEvent =
    | LineAdded of line: CartLine * expectedEpoch: CartEpoch
    | QuantityChanged of productId: ProductId * quantity: Quantity * expectedEpoch: CartEpoch
    | LineRemoved of productId: ProductId * expectedEpoch: CartEpoch
    | Cleared of expectedEpoch: CartEpoch
    | MergeRequested of mergeId: Guid * target: string
    | MergeSnapshotCaptured of mergeId: Guid
    | MergeApplied of mergeId: Guid
    | MergeFailed of mergeId: Guid
    | ApplyMerge of mergeId: Guid * sourceCartId: string * lines: CartLine list
    | AbandonmentTimerFired of generation: CartEpoch * deadline: DateTimeOffset

/// <summary>Consequential local effects of the cart machine. Each is idempotent, executed
/// through an action receipt and, where a machine must react, an integration-outbox callback.
/// Merge actions carry the frozen lines so the handler never re-reads state.</summary>
type CartAction =
    | RecordCartTouch of generation: CartEpoch * hasLines: bool
    | CaptureMergeSnapshot of mergeId: Guid * target: string * lines: CartLine list
    | SubmitMergeSnapshot of mergeId: Guid * target: string * lines: CartLine list
    | NotifyMergeApplied of mergeId: Guid * sourceCartId: string
    | RevokeGuestCapability of mergeId: Guid

[<RequireQualifiedAccess>]
type CartActionError =
    | InvalidCartEntityId
    | CapabilityLookupFailed
    | MergeSnapshotAlreadyCaptured
    | MergeTargetNotFound
    | CallbackEncodingFailed
    | ActionReceiptMismatch

/// <summary>The result of merging one line list into another, with per-product union and
/// quantity clamping. Used both by in-cart mutation and by the guest-to-customer merge.</summary>
type MergeReport =
    { Result: CartLine list
      Added: int
      Updated: int }

/// <summary>Marks entity ids of the <c>carts</c> machine.</summary>
type Cart = class end

type CartId = EntityId<Cart>

[<RequireQualifiedAccess>]
module Cart =

    [<Literal>]
    let MachineKey = "carts"

    [<Literal>]
    let ActionQueue = "cart_actions"

    [<Literal>]
    let InitialEpoch: CartEpoch = 0L

    let initialState = Empty

    /// <summary>The canonical entity id of a customer's cart, keyed to the Identity user id.</summary>
    let customerId (userId: Guid) : CartId = entityId $"customer:{userId:D}"

    /// <summary>The entity id of a guest cart, from the random guid minted at cookie creation.</summary>
    let guestId (guest: Guid) : CartId = entityId $"guest:{guest:D}"

    let private clampQuantity (quantity: int) : Quantity =
        match Quantity.create (min Quantity.maxValue quantity) with
        | Ok quantity -> quantity
        | Error _ -> Quantity.create 1 |> Result.defaultWith (fun _ -> invalidOp "impossible")

    let private lineQuantity (line: CartLine) = Quantity.value line.Quantity

    /// <summary>Adds one line to a list, unioning by product id and clamping to the maximum
    /// quantity. The incoming line's price/name snapshot replaces the stored one only when the
    /// product was not already present.</summary>
    let addLine (incoming: CartLine) (lines: CartLine list) : CartLine list =
        let isProduct = fun (line: CartLine) -> line.ProductId = incoming.ProductId

        match lines |> List.tryFind isProduct with
        | None -> lines @ [ incoming ]
        | Some existing ->
            let combined = lineQuantity existing + lineQuantity incoming

            let merged =
                { existing with
                    Quantity = clampQuantity combined }

            lines |> List.map (fun line -> if isProduct line then merged else line)

    /// <summary>Sets a line's quantity to an exact value, removing nothing.</summary>
    let setQuantity (productId: ProductId) (quantity: Quantity) (lines: CartLine list) : CartLine list =
        lines
        |> List.map (fun line ->
            if line.ProductId = productId then
                { line with Quantity = quantity }
            else
                line)

    /// <summary>Removes a line by product id.</summary>
    let removeLine (productId: ProductId) (lines: CartLine list) : CartLine list =
        lines |> List.filter (fun line -> line.ProductId <> productId)

    /// <summary>Pure union of two line lists. Result clamps to the maximum quantity; the
    /// report records how many products were added (absent from the destination) versus
    /// updated (already present).</summary>
    let merge (existing: CartLine list) (incoming: CartLine list) : MergeReport =
        let existingIds = existing |> List.map _.ProductId |> Set.ofList

        let added =
            incoming
            |> List.filter (fun line -> not (Set.contains line.ProductId existingIds))

        let merged = incoming |> List.fold (fun acc line -> addLine line acc) existing

        { Result = merged
          Added = added.Length
          Updated = incoming.Length - added.Length }

    let classifyState (state: CartState) =
        match state with
        | Empty -> stateId "empty"
        | Active _ -> stateId "active"
        | MergeFrozen _ -> stateId "merge-frozen"
        | MergedInto _ -> stateId "merged-into"
        | Converted -> stateId "converted"
        | Abandoned _ -> stateId "abandoned"

    let private withLines (lines: CartLine list) (epoch: CartEpoch) : CartState =
        Active { Epoch = epoch; Lines = lines }

    let private bump = (+) 1L

    let private activeCart (state: CartState) : ActiveCart option =
        match state with
        | Active cart
        | Abandoned cart -> Some cart
        | Empty
        | MergeFrozen _
        | MergedInto _
        | Converted -> None

    /// <summary>The epoch a client must submit for a state. Empty returns the initial epoch;
    /// terminal states have no client-mutable epoch.</summary>
    let epochOf (state: CartState) : CartEpoch option =
        match state with
        | Empty -> Some InitialEpoch
        | Active cart -> Some cart.Epoch
        | MergeFrozen cart -> Some cart.Epoch
        | MergedInto _
        | Converted -> None
        | Abandoned cart -> Some cart.Epoch

    /// <summary>Applies one client line mutation to a cart, bumping the epoch and reporting the
    /// resulting state and whether a touch must be recorded. Returns the unchanged state when
    /// the mutation is a no-op (stale epoch, absent line, or clamped-to-identical quantity).</summary>
    let private applyMutation
        (cart: ActiveCart)
        (update: CartLine list -> CartLine list)
        : CartState * CartAction list =
        let next = update cart.Lines

        if next = cart.Lines then
            Active cart, []
        else
            let nextEpoch = bump cart.Epoch
            let hasLines = not next.IsEmpty
            Active { Epoch = nextEpoch; Lines = next }, [ RecordCartTouch(nextEpoch, hasLines) ]

    let private lineAddedEvent state event =
        match state, event with
        | Empty, LineAdded(_, expected) -> expected = InitialEpoch
        | Active cart, LineAdded(_, expected)
        | Abandoned cart, LineAdded(_, expected) -> expected = cart.Epoch
        | _ -> false

    let private quantityChangedEvent state event =
        match state, event with
        | Active cart, QuantityChanged(_, _, expected)
        | Abandoned cart, QuantityChanged(_, _, expected) -> expected = cart.Epoch
        | _ -> false

    let private lineRemovedEvent state event =
        match state, event with
        | Active cart, LineRemoved(_, expected)
        | Abandoned cart, LineRemoved(_, expected) -> expected = cart.Epoch
        | _ -> false

    let private clearedEvent state event =
        match state, event with
        | Active cart, Cleared expected
        | Abandoned cart, Cleared expected -> expected = cart.Epoch
        | _ -> false

    let private mergeRequestedEvent state event =
        match state, event with
        | Active cart, MergeRequested _ when not cart.Lines.IsEmpty -> true
        | Abandoned cart, MergeRequested _ when not cart.Lines.IsEmpty -> true
        | _ -> false

    let private mergeSnapshotCapturedEvent state event =
        match state, event with
        | MergeFrozen cart, MergeSnapshotCaptured mergeId -> cart.MergeId = mergeId && not cart.Submitted
        | _ -> false

    let private mergeAppliedEvent state event =
        match state, event with
        | MergeFrozen cart, MergeApplied mergeId -> cart.MergeId = mergeId
        | _ -> false

    let private mergeFailedEvent state event =
        match state, event with
        | MergeFrozen cart, MergeFailed mergeId -> cart.MergeId = mergeId
        | _ -> false

    let private abandonmentEvent state event =
        match state, event with
        | Active cart, AbandonmentTimerFired(generation, _) -> generation = cart.Epoch
        | _ -> false

    let private applyMergeEvent _state event =
        match event with
        | ApplyMerge _ -> true
        | _ -> false

    /// <summary>Events that are absorbed as no-ops when their guarded transition cannot fire:
    /// stale timers and late or duplicate merge callbacks. Client mutations are never absorbed,
    /// so a stale epoch or illegal transition is rejected rather than silently dropped.</summary>
    let private absorbEvent (_state: CartState) (event: CartEvent) : bool =
        match event with
        | LineAdded _
        | QuantityChanged _
        | LineRemoved _
        | Cleared _
        | MergeRequested _
        | ApplyMerge _ -> false
        | AbandonmentTimerFired _
        | MergeSnapshotCaptured _
        | MergeApplied _
        | MergeFailed _ -> true

    let chartResult =
        statechart<CartState, CartEvent, CartAction, CartActionError> {
            root MachineKey

            classify classifyState

            state "empty" {
                on (fun _ event -> lineAddedEvent Empty event) (fun _ event ->
                    match event with
                    | LineAdded(line, _) -> [ RecordCartTouch(1L, true) ], withLines [ line ] 1L
                    | _ -> [], Empty)

                on applyMergeEvent (fun _ event ->
                    match event with
                    | ApplyMerge(mergeId, source, lines) ->
                        let report = merge [] lines
                        let nextEpoch = 1L
                        let hasLines = not report.Result.IsEmpty

                        [ NotifyMergeApplied(mergeId, source); RecordCartTouch(nextEpoch, hasLines) ],
                        withLines report.Result nextEpoch
                    | _ -> [], Empty)

                // A merge request, timer, or mutation for a cart that never had a line is a no-op.
                internalOn absorbEvent (fun _ _ -> [])
            }

            state "active" {
                on lineAddedEvent (fun state event ->
                    match state, event with
                    | Active cart, LineAdded(line, _) ->
                        let next, actions = applyMutation cart (addLine line)
                        actions, next
                    | _ -> [], state)

                on quantityChangedEvent (fun state event ->
                    match state, event with
                    | Active cart, QuantityChanged(productId, quantity, _) ->
                        let next, actions = applyMutation cart (setQuantity productId quantity)
                        actions, next
                    | _ -> [], state)

                on lineRemovedEvent (fun state event ->
                    match state, event with
                    | Active cart, LineRemoved(productId, _) ->
                        let next, actions = applyMutation cart (removeLine productId)
                        actions, next
                    | _ -> [], state)

                on clearedEvent (fun state event ->
                    match state, event with
                    | Active cart, Cleared _ ->
                        let next, actions = applyMutation cart (fun _ -> [])
                        actions, next
                    | _ -> [], state)

                on mergeRequestedEvent (fun state event ->
                    match state, event with
                    | Active cart, MergeRequested(mergeId, target) ->
                        [ CaptureMergeSnapshot(mergeId, target, cart.Lines) ],
                        MergeFrozen
                            { Epoch = cart.Epoch
                              Lines = cart.Lines
                              MergeId = mergeId
                              Target = target
                              Submitted = false }
                    | _ -> [], state)

                on abandonmentEvent (fun state event ->
                    match state with
                    | Active cart -> [], Abandoned cart
                    | _ -> [], state)

                on applyMergeEvent (fun state event ->
                    match state, event with
                    | Active cart, ApplyMerge(mergeId, source, lines) ->
                        let report = merge cart.Lines lines
                        let nextEpoch = bump cart.Epoch
                        let hasLines = not report.Result.IsEmpty

                        [ NotifyMergeApplied(mergeId, source); RecordCartTouch(nextEpoch, hasLines) ],
                        withLines report.Result nextEpoch
                    | _ -> [], state)

                // Duplicate starts, stale epochs, and stale timers are absorbed.
                internalOn absorbEvent (fun _ _ -> [])
            }

            state "merge-frozen" {
                on mergeSnapshotCapturedEvent (fun state event ->
                    match state, event with
                    | MergeFrozen cart, MergeSnapshotCaptured _ ->
                        [ SubmitMergeSnapshot(cart.MergeId, cart.Target, cart.Lines) ],
                        MergeFrozen { cart with Submitted = true }
                    | _ -> [], state)

                on mergeAppliedEvent (fun state event ->
                    match state, event with
                    | MergeFrozen cart, MergeApplied _ ->
                        [ RevokeGuestCapability cart.MergeId ],
                        MergedInto
                            { MergeId = cart.MergeId
                              Target = cart.Target }
                    | _ -> [], state)

                on mergeFailedEvent (fun state event ->
                    match state, event with
                    | MergeFrozen cart, MergeFailed _ ->
                        let nextEpoch = bump cart.Epoch
                        let hasLines = not cart.Lines.IsEmpty

                        [ RecordCartTouch(nextEpoch, hasLines) ],
                        Active
                            { Epoch = nextEpoch
                              Lines = cart.Lines }
                    | _ -> [], state)

                internalOn absorbEvent (fun _ _ -> [])
            }

            state "merged-into" {
                // Terminal for the guest cart: any late merge callback or mutation is absorbed.
                internalOn absorbEvent (fun _ _ -> [])
            }

            state "converted" {
                // Reachable only from P3; no command mutates a converted cart.
                internalOn absorbEvent (fun _ _ -> [])
            }

            state "abandoned" {
                on lineAddedEvent (fun state event ->
                    match state, event with
                    | Abandoned cart, LineAdded(line, _) ->
                        let next, actions = applyMutation cart (addLine line)
                        actions, next
                    | _ -> [], state)

                on quantityChangedEvent (fun state event ->
                    match state, event with
                    | Abandoned cart, QuantityChanged(productId, quantity, _) ->
                        let next, actions = applyMutation cart (setQuantity productId quantity)
                        actions, next
                    | _ -> [], state)

                on lineRemovedEvent (fun state event ->
                    match state, event with
                    | Abandoned cart, LineRemoved(productId, _) ->
                        let next, actions = applyMutation cart (removeLine productId)
                        actions, next
                    | _ -> [], state)

                on clearedEvent (fun state event ->
                    match state, event with
                    | Abandoned cart, Cleared _ ->
                        let next, actions = applyMutation cart (fun _ -> [])
                        actions, next
                    | _ -> [], state)

                on mergeRequestedEvent (fun state event ->
                    match state, event with
                    | Abandoned cart, MergeRequested(mergeId, target) ->
                        [ CaptureMergeSnapshot(mergeId, target, cart.Lines) ],
                        MergeFrozen
                            { Epoch = cart.Epoch
                              Lines = cart.Lines
                              MergeId = mergeId
                              Target = target
                              Submitted = false }
                    | _ -> [], state)

                on applyMergeEvent (fun state event ->
                    match state, event with
                    | Abandoned cart, ApplyMerge(mergeId, source, lines) ->
                        let report = merge cart.Lines lines
                        let nextEpoch = bump cart.Epoch
                        let hasLines = not report.Result.IsEmpty

                        [ NotifyMergeApplied(mergeId, source); RecordCartTouch(nextEpoch, hasLines) ],
                        withLines report.Result nextEpoch
                    | _ -> [], state)

                internalOn absorbEvent (fun _ _ -> [])
            }
        }

    let chartValue =
        match chartResult with
        | Ok built -> built
        | Error errors -> invalidOp $"carts chart is invalid: %A{errors}"
