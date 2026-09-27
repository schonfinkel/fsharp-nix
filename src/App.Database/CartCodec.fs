namespace App.Database

open System
open System.Text.Json
open System.Text.Json.Serialization
open App.Cart
open App.Domain
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging

/// <summary>
/// Wire records for the <c>carts</c> machine. Money is stored as an invariant decimal amount
/// string plus an ISO 4217 code — never NodaMoney's default JSON converter, whose rounding is
/// coupled to the ambient context. Like every durable chart, decoding peeks the <c>tag</c>
/// first, deserializes that tag's record through validating domain constructors, and rejects
/// unknown tags.
/// </summary>
module CartWire =

    type TaggedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string }

    type LineDto =
        { [<JsonPropertyName("productId")>]
          ProductId: string
          [<JsonPropertyName("sku")>]
          Sku: string
          [<JsonPropertyName("name")>]
          Name: string
          [<JsonPropertyName("unitPriceAmount")>]
          UnitPriceAmount: string
          [<JsonPropertyName("unitPriceCurrency")>]
          UnitPriceCurrency: string
          [<JsonPropertyName("quantity")>]
          Quantity: int }

    type ActiveStateDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("epoch")>]
          Epoch: int64
          [<JsonPropertyName("lines")>]
          Lines: LineDto array }

    type FrozenStateDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("epoch")>]
          Epoch: int64
          [<JsonPropertyName("lines")>]
          Lines: LineDto array
          [<JsonPropertyName("mergeId")>]
          MergeId: string
          [<JsonPropertyName("target")>]
          Target: string
          [<JsonPropertyName("submitted")>]
          Submitted: bool }

    type MergedStateDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("mergeId")>]
          MergeId: string
          [<JsonPropertyName("target")>]
          Target: string }

    type LineAddedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("epoch")>]
          Epoch: int64
          [<JsonPropertyName("line")>]
          Line: LineDto }

    type QuantityChangedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("productId")>]
          ProductId: string
          [<JsonPropertyName("quantity")>]
          Quantity: int
          [<JsonPropertyName("epoch")>]
          Epoch: int64 }

    type LineRemovedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("productId")>]
          ProductId: string
          [<JsonPropertyName("epoch")>]
          Epoch: int64 }

    type ClearedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("epoch")>]
          Epoch: int64 }

    type MergeRequestedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("mergeId")>]
          MergeId: string
          [<JsonPropertyName("target")>]
          Target: string }

    type MergeCallbackDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("mergeId")>]
          MergeId: string }

    type ApplyMergeDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("mergeId")>]
          MergeId: string
          [<JsonPropertyName("sourceCartId")>]
          SourceCartId: string
          [<JsonPropertyName("lines")>]
          Lines: LineDto array }

    type CaptureMergeSnapshotDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("mergeId")>]
          MergeId: string
          [<JsonPropertyName("target")>]
          Target: string
          [<JsonPropertyName("lines")>]
          Lines: LineDto array }

    type SubmitMergeSnapshotDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("mergeId")>]
          MergeId: string
          [<JsonPropertyName("target")>]
          Target: string
          [<JsonPropertyName("lines")>]
          Lines: LineDto array }

    type NotifyMergeAppliedDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("mergeId")>]
          MergeId: string
          [<JsonPropertyName("sourceCartId")>]
          SourceCartId: string }

    type AbandonmentDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("generation")>]
          Generation: int64
          [<JsonPropertyName("deadline")>]
          Deadline: int64 }

    type RecordCartTouchDto =
        { [<JsonPropertyName("tag")>]
          Tag: string
          [<JsonPropertyName("generation")>]
          Generation: int64
          [<JsonPropertyName("hasLines")>]
          HasLines: bool }

[<RequireQualifiedAccess>]
module CartCodec =

    module Tags =

        [<Literal>]
        let EmptyState = "empty-v1"

        [<Literal>]
        let ActiveState = "active-v1"

        [<Literal>]
        let MergeFrozenState = "merge-frozen-v1"

        [<Literal>]
        let MergedIntoState = "merged-into-v1"

        [<Literal>]
        let ConvertedState = "converted-v1"

        [<Literal>]
        let AbandonedState = "abandoned-v1"

        [<Literal>]
        let LineAddedEvent = "line-added-v1"

        [<Literal>]
        let QuantityChangedEvent = "quantity-changed-v1"

        [<Literal>]
        let LineRemovedEvent = "line-removed-v1"

        [<Literal>]
        let ClearedEvent = "cleared-v1"

        [<Literal>]
        let MergeRequestedEvent = "merge-requested-v1"

        [<Literal>]
        let MergeSnapshotCapturedEvent = "merge-snapshot-captured-v1"

        [<Literal>]
        let MergeAppliedEvent = "merge-applied-v1"

        [<Literal>]
        let MergeFailedEvent = "merge-failed-v1"

        [<Literal>]
        let ApplyMergeEvent = "apply-merge-v1"

        [<Literal>]
        let AbandonmentTimerFiredEvent = "abandonment-timer-fired-v1"

        [<Literal>]
        let RecordCartTouchAction = "record-cart-touch-v1"

        [<Literal>]
        let CaptureMergeSnapshotAction = "capture-merge-snapshot-v1"

        [<Literal>]
        let SubmitMergeSnapshotAction = "submit-merge-snapshot-v1"

        [<Literal>]
        let NotifyMergeAppliedAction = "notify-merge-applied-v1"

        [<Literal>]
        let RevokeGuestCapabilityAction = "revoke-guest-capability-v1"

        [<Literal>]
        let InvalidCartEntityIdError = "invalid-cart-entity-id-v1"

        [<Literal>]
        let CapabilityLookupFailedError = "capability-lookup-failed-v1"

        [<Literal>]
        let MergeSnapshotAlreadyCapturedError = "merge-snapshot-already-captured-v1"

        [<Literal>]
        let MergeTargetNotFoundError = "merge-target-not-found-v1"

        [<Literal>]
        let CallbackEncodingFailedError = "callback-encoding-failed-v1"

        [<Literal>]
        let ActionReceiptMismatchError = "action-receipt-mismatch-v1"

    [<Literal>]
    let private NameMaxLength = 200

    let private options =
        let configured =
            JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

        configured.RespectRequiredConstructorParameters <- true
        configured

    let private encodeValue (typeName: string) (value: obj) : Result<string, CodecError> =
        try
            Ok(JsonSerializer.Serialize(value, options))
        with error ->
            Error(CodecError.EncodeError(typeName, error))

    let private decodeExact<'T> (typeName: string) (json: string) : Result<'T, CodecError> =
        try
            let value = JsonSerializer.Deserialize<'T>(json, options)

            if isNull (box value) then
                Error(CodecError.DecodeError(typeName, FormatException "JSON deserialized to null."))
            else
                Ok value
        with error ->
            Error(CodecError.DecodeError(typeName, error))

    let private readTag (typeName: string) (json: string) : Result<string, CodecError> =
        try
            use document = JsonDocument.Parse json

            match document.RootElement.TryGetProperty "tag" with
            | false, _ -> Error(CodecError.DecodeError(typeName, FormatException "Missing 'tag'."))
            | true, element ->
                match element.GetString() with
                | null -> Error(CodecError.DecodeError(typeName, FormatException "Missing 'tag'."))
                | tag -> Ok tag
        with error ->
            Error(CodecError.DecodeError(typeName, error))

    let private decodeFailure (typeName: string) (message: string) : CodecError =
        CodecError.DecodeError(typeName, FormatException message)

    let private unknownTag (typeName: string) (tag: string) : CodecError =
        decodeFailure typeName $"Unknown tag '{tag}'."

    let private tryGuid (typeName: string) (field: string) (wire: string) : Result<Guid, CodecError> =
        match Guid.TryParseExact(wire, "D") with
        | true, value when value <> Guid.Empty -> Ok value
        | _ -> Error(decodeFailure typeName $"'{field}' must be a non-empty GUID in \"D\" format.")

    let private tryEpoch (typeName: string) (field: string) (value: int64) : Result<CartEpoch, CodecError> =
        if value < 0L then
            Error(decodeFailure typeName $"'{field}' must not be negative.")
        else
            Ok value

    let private productIdOf (typeName: string) (wire: string) : Result<ProductId, CodecError> =
        ProductId.tryParse wire
        |> Result.mapError (fun message -> decodeFailure typeName message)

    let private skuOf (typeName: string) (wire: string) : Result<Sku, CodecError> =
        Sku.tryParse wire
        |> Result.mapError (fun message -> decodeFailure typeName message)

    let private nameOf (typeName: string) (wire: string) : Result<NonEmptyString, CodecError> =
        NonEmptyString.create NameMaxLength wire
        |> Result.mapError (fun message -> decodeFailure typeName message)

    let private quantityOf (typeName: string) (value: int) : Result<Quantity, CodecError> =
        Quantity.tryParse value
        |> Result.mapError (fun message -> decodeFailure typeName message)

    let private moneyOf (typeName: string) (amountValue: string) (currency: string) =
        Money.tryOfWire amountValue currency
        |> Result.mapError (fun message -> decodeFailure typeName message)

    let private lineOfDto (typeName: string) (dto: CartWire.LineDto) : Result<CartLine, CodecError> =
        productIdOf typeName dto.ProductId
        |> Result.bind (fun productId ->
            skuOf typeName dto.Sku
            |> Result.bind (fun sku ->
                nameOf typeName dto.Name
                |> Result.bind (fun name ->
                    moneyOf typeName dto.UnitPriceAmount dto.UnitPriceCurrency
                    |> Result.bind (fun unitPrice ->
                        quantityOf typeName dto.Quantity
                        |> Result.map (fun quantity ->
                            { ProductId = productId
                              Sku = sku
                              Name = name
                              UnitPrice = unitPrice
                              Quantity = quantity })))))

    let private lineDto (line: CartLine) : CartWire.LineDto =
        { ProductId = ProductId.wireString line.ProductId
          Sku = Sku.value line.Sku
          Name = NonEmptyString.value line.Name
          UnitPriceAmount = Money.wireAmount line.UnitPrice
          UnitPriceCurrency = Money.currencyCode line.UnitPrice
          Quantity = Quantity.value line.Quantity }

    let private linesOfDto (typeName: string) (dtos: CartWire.LineDto array) : Result<CartLine list, CodecError> =
        let rec go remaining acc =
            match remaining with
            | [] -> Ok(List.rev acc)
            | dto :: rest ->
                match lineOfDto typeName dto with
                | Ok line -> go rest (line :: acc)
                | Error error -> Error error

        go (Array.toList dtos) []

    let private linesDto (lines: CartLine list) : CartWire.LineDto array =
        lines |> List.map lineDto |> List.toArray

    /// <summary>Canonical JSON encoding of a line list, used by the merge snapshot and the
    /// customer-side apply-merge command so both sides share one stable bytes shape.</summary>
    let encodeLines (lines: CartLine list) : Result<string, CodecError> =
        encodeValue "CartLines" (linesDto lines)

    /// <summary>Decodes a canonical line-list JSON through the validating line constructor.</summary>
    let decodeLines (json: string) : Result<CartLine list, CodecError> =
        decodeExact<CartWire.LineDto array> "CartLines" json
        |> Result.bind (linesOfDto "CartLines")

    let private stateTypeName = "CartState"

    let private encodeState (state: CartState) : Result<string, CodecError> =
        let dto =
            match state with
            | Empty -> box { CartWire.TaggedDto.Tag = Tags.EmptyState }
            | Active cart ->
                let active: CartWire.ActiveStateDto =
                    { Tag = Tags.ActiveState
                      Epoch = cart.Epoch
                      Lines = linesDto cart.Lines }

                box active
            | CartState.Abandoned cart ->
                let abandoned: CartWire.ActiveStateDto =
                    { Tag = Tags.AbandonedState
                      Epoch = cart.Epoch
                      Lines = linesDto cart.Lines }

                box abandoned
            | MergeFrozen cart ->
                let frozen: CartWire.FrozenStateDto =
                    { Tag = Tags.MergeFrozenState
                      Epoch = cart.Epoch
                      Lines = linesDto cart.Lines
                      MergeId = cart.MergeId.ToString("D")
                      Target = cart.Target
                      Submitted = cart.Submitted }

                box frozen
            | MergedInto cart ->
                let merged: CartWire.MergedStateDto =
                    { Tag = Tags.MergedIntoState
                      MergeId = cart.MergeId.ToString("D")
                      Target = cart.Target }

                box merged
            | Converted -> box { CartWire.TaggedDto.Tag = Tags.ConvertedState }

        encodeValue stateTypeName dto

    let private decodeActive (typeName: string) (dto: CartWire.ActiveStateDto) : Result<ActiveCart, CodecError> =
        tryEpoch typeName "epoch" dto.Epoch
        |> Result.bind (fun epoch ->
            linesOfDto typeName dto.Lines
            |> Result.map (fun lines ->
                let cart: ActiveCart = { Epoch = epoch; Lines = lines }

                cart))

    let private decodeState (json: string) : Result<CartState, CodecError> =
        readTag stateTypeName json
        |> Result.bind (fun tag ->
            match tag with
            | t when t = Tags.EmptyState ->
                decodeExact<CartWire.TaggedDto> stateTypeName json
                |> Result.map (fun _ -> Empty)
            | t when t = Tags.ConvertedState ->
                decodeExact<CartWire.TaggedDto> stateTypeName json
                |> Result.map (fun _ -> Converted)
            | t when t = Tags.ActiveState ->
                decodeExact<CartWire.ActiveStateDto> stateTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag stateTypeName dto.Tag)
                    else
                        decodeActive stateTypeName dto |> Result.map Active)
            | t when t = Tags.AbandonedState ->
                decodeExact<CartWire.ActiveStateDto> stateTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag stateTypeName dto.Tag)
                    else
                        decodeActive stateTypeName dto |> Result.map CartState.Abandoned)
            | t when t = Tags.MergeFrozenState ->
                decodeExact<CartWire.FrozenStateDto> stateTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag stateTypeName dto.Tag)
                    else
                        tryEpoch stateTypeName "epoch" dto.Epoch
                        |> Result.bind (fun epoch ->
                            tryGuid stateTypeName "mergeId" dto.MergeId
                            |> Result.bind (fun mergeId ->
                                linesOfDto stateTypeName dto.Lines
                                |> Result.map (fun lines ->
                                    MergeFrozen
                                        { Epoch = epoch
                                          Lines = lines
                                          MergeId = mergeId
                                          Target = dto.Target
                                          Submitted = dto.Submitted }))))
            | t when t = Tags.MergedIntoState ->
                decodeExact<CartWire.MergedStateDto> stateTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag stateTypeName dto.Tag)
                    else
                        tryGuid stateTypeName "mergeId" dto.MergeId
                        |> Result.map (fun mergeId ->
                            MergedInto
                                { MergeId = mergeId
                                  Target = dto.Target }))
            | _ -> Error(unknownTag stateTypeName tag))

    let private eventTypeName = "CartEvent"

    let private encodeEvent (event: CartEvent) : Result<string, CodecError> =
        let dto =
            match event with
            | LineAdded(line, epoch) ->
                let added: CartWire.LineAddedDto =
                    { Tag = Tags.LineAddedEvent
                      Epoch = epoch
                      Line = lineDto line }

                box added
            | QuantityChanged(productId, quantity, epoch) ->
                let changed: CartWire.QuantityChangedDto =
                    { Tag = Tags.QuantityChangedEvent
                      ProductId = ProductId.wireString productId
                      Quantity = Quantity.value quantity
                      Epoch = epoch }

                box changed
            | LineRemoved(productId, epoch) ->
                let removed: CartWire.LineRemovedDto =
                    { Tag = Tags.LineRemovedEvent
                      ProductId = ProductId.wireString productId
                      Epoch = epoch }

                box removed
            | Cleared epoch ->
                let cleared: CartWire.ClearedDto =
                    { Tag = Tags.ClearedEvent
                      Epoch = epoch }

                box cleared
            | MergeRequested(mergeId, target) ->
                let requested: CartWire.MergeRequestedDto =
                    { Tag = Tags.MergeRequestedEvent
                      MergeId = mergeId.ToString("D")
                      Target = target }

                box requested
            | MergeSnapshotCaptured mergeId ->
                let callback: CartWire.MergeCallbackDto =
                    { Tag = Tags.MergeSnapshotCapturedEvent
                      MergeId = mergeId.ToString("D") }

                box callback
            | MergeApplied mergeId ->
                let callback: CartWire.MergeCallbackDto =
                    { Tag = Tags.MergeAppliedEvent
                      MergeId = mergeId.ToString("D") }

                box callback
            | MergeFailed mergeId ->
                let callback: CartWire.MergeCallbackDto =
                    { Tag = Tags.MergeFailedEvent
                      MergeId = mergeId.ToString("D") }

                box callback
            | ApplyMerge(mergeId, sourceCartId, lines) ->
                let apply: CartWire.ApplyMergeDto =
                    { Tag = Tags.ApplyMergeEvent
                      MergeId = mergeId.ToString("D")
                      SourceCartId = sourceCartId
                      Lines = linesDto lines }

                box apply
            | AbandonmentTimerFired(generation, deadline) ->
                let timer: CartWire.AbandonmentDto =
                    { Tag = Tags.AbandonmentTimerFiredEvent
                      Generation = generation
                      Deadline = deadline.ToUnixTimeMilliseconds() }

                box timer

        encodeValue eventTypeName dto

    let private decodeEvent (json: string) : Result<CartEvent, CodecError> =
        readTag eventTypeName json
        |> Result.bind (fun tag ->
            let decodeMergeCallback (wrap: Guid -> CartEvent) : Result<CartEvent, CodecError> =
                decodeExact<CartWire.MergeCallbackDto> eventTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag eventTypeName dto.Tag)
                    else
                        tryGuid eventTypeName "mergeId" dto.MergeId |> Result.map wrap)

            match tag with
            | t when t = Tags.LineAddedEvent ->
                decodeExact<CartWire.LineAddedDto> eventTypeName json
                |> Result.bind (fun dto ->
                    tryEpoch eventTypeName "epoch" dto.Epoch
                    |> Result.bind (fun epoch ->
                        lineOfDto eventTypeName dto.Line
                        |> Result.map (fun line -> LineAdded(line, epoch))))
            | t when t = Tags.QuantityChangedEvent ->
                decodeExact<CartWire.QuantityChangedDto> eventTypeName json
                |> Result.bind (fun dto ->
                    productIdOf eventTypeName dto.ProductId
                    |> Result.bind (fun productId ->
                        quantityOf eventTypeName dto.Quantity
                        |> Result.bind (fun quantity ->
                            tryEpoch eventTypeName "epoch" dto.Epoch
                            |> Result.map (fun epoch -> QuantityChanged(productId, quantity, epoch)))))
            | t when t = Tags.LineRemovedEvent ->
                decodeExact<CartWire.LineRemovedDto> eventTypeName json
                |> Result.bind (fun dto ->
                    productIdOf eventTypeName dto.ProductId
                    |> Result.bind (fun productId ->
                        tryEpoch eventTypeName "epoch" dto.Epoch
                        |> Result.map (fun epoch -> LineRemoved(productId, epoch))))
            | t when t = Tags.ClearedEvent ->
                decodeExact<CartWire.ClearedDto> eventTypeName json
                |> Result.bind (fun dto -> tryEpoch eventTypeName "epoch" dto.Epoch |> Result.map Cleared)
            | t when t = Tags.MergeRequestedEvent ->
                decodeExact<CartWire.MergeRequestedDto> eventTypeName json
                |> Result.bind (fun dto ->
                    tryGuid eventTypeName "mergeId" dto.MergeId
                    |> Result.map (fun mergeId -> MergeRequested(mergeId, dto.Target)))
            | t when t = Tags.MergeSnapshotCapturedEvent -> decodeMergeCallback MergeSnapshotCaptured
            | t when t = Tags.MergeAppliedEvent -> decodeMergeCallback MergeApplied
            | t when t = Tags.MergeFailedEvent -> decodeMergeCallback MergeFailed
            | t when t = Tags.ApplyMergeEvent ->
                decodeExact<CartWire.ApplyMergeDto> eventTypeName json
                |> Result.bind (fun dto ->
                    tryGuid eventTypeName "mergeId" dto.MergeId
                    |> Result.bind (fun mergeId ->
                        linesOfDto eventTypeName dto.Lines
                        |> Result.map (fun lines -> ApplyMerge(mergeId, dto.SourceCartId, lines))))
            | t when t = Tags.AbandonmentTimerFiredEvent ->
                decodeExact<CartWire.AbandonmentDto> eventTypeName json
                |> Result.bind (fun dto ->
                    tryEpoch eventTypeName "generation" dto.Generation
                    |> Result.bind (fun generation ->
                        try
                            Ok(
                                AbandonmentTimerFired(
                                    generation,
                                    DateTimeOffset.FromUnixTimeMilliseconds dto.Deadline
                                )
                            )
                        with _ ->
                            Error(decodeFailure eventTypeName "'deadline' epoch was invalid.")))
            | _ -> Error(unknownTag eventTypeName tag))

    let private actionTypeName = "CartAction"

    let private encodeAction (action: CartAction) : Result<string, CodecError> =
        match action with
        | RecordCartTouch(generation, hasLines) ->
            let dto: CartWire.RecordCartTouchDto =
                { Tag = Tags.RecordCartTouchAction
                  Generation = generation
                  HasLines = hasLines }

            encodeValue actionTypeName dto
        | CaptureMergeSnapshot(mergeId, target, lines) ->
            let dto: CartWire.CaptureMergeSnapshotDto =
                { Tag = Tags.CaptureMergeSnapshotAction
                  MergeId = mergeId.ToString("D")
                  Target = target
                  Lines = linesDto lines }

            encodeValue actionTypeName dto
        | SubmitMergeSnapshot(mergeId, target, lines) ->
            let dto: CartWire.SubmitMergeSnapshotDto =
                { Tag = Tags.SubmitMergeSnapshotAction
                  MergeId = mergeId.ToString("D")
                  Target = target
                  Lines = linesDto lines }

            encodeValue actionTypeName dto
        | NotifyMergeApplied(mergeId, sourceCartId) ->
            let dto: CartWire.NotifyMergeAppliedDto =
                { Tag = Tags.NotifyMergeAppliedAction
                  MergeId = mergeId.ToString("D")
                  SourceCartId = sourceCartId }

            encodeValue actionTypeName dto
        | RevokeGuestCapability mergeId ->
            let dto: CartWire.MergeCallbackDto =
                { Tag = Tags.RevokeGuestCapabilityAction
                  MergeId = mergeId.ToString("D") }

            encodeValue actionTypeName dto

    let private decodeAction (json: string) : Result<CartAction, CodecError> =
        readTag actionTypeName json
        |> Result.bind (fun tag ->
            let decodeMerge (wrap: Guid -> CartAction) =
                decodeExact<CartWire.MergeCallbackDto> actionTypeName json
                |> Result.bind (fun dto ->
                    if dto.Tag <> tag then
                        Error(unknownTag actionTypeName dto.Tag)
                    else
                        tryGuid actionTypeName "mergeId" dto.MergeId |> Result.map wrap)

            match tag with
            | t when t = Tags.RecordCartTouchAction ->
                decodeExact<CartWire.RecordCartTouchDto> actionTypeName json
                |> Result.bind (fun dto ->
                    tryEpoch actionTypeName "generation" dto.Generation
                    |> Result.map (fun generation -> RecordCartTouch(generation, dto.HasLines)))
            | t when t = Tags.CaptureMergeSnapshotAction ->
                decodeExact<CartWire.CaptureMergeSnapshotDto> actionTypeName json
                |> Result.bind (fun dto ->
                    tryGuid actionTypeName "mergeId" dto.MergeId
                    |> Result.bind (fun mergeId ->
                        linesOfDto actionTypeName dto.Lines
                        |> Result.map (fun lines -> CaptureMergeSnapshot(mergeId, dto.Target, lines))))
            | t when t = Tags.SubmitMergeSnapshotAction ->
                decodeExact<CartWire.SubmitMergeSnapshotDto> actionTypeName json
                |> Result.bind (fun dto ->
                    tryGuid actionTypeName "mergeId" dto.MergeId
                    |> Result.bind (fun mergeId ->
                        linesOfDto actionTypeName dto.Lines
                        |> Result.map (fun lines -> SubmitMergeSnapshot(mergeId, dto.Target, lines))))
            | t when t = Tags.NotifyMergeAppliedAction ->
                decodeExact<CartWire.NotifyMergeAppliedDto> actionTypeName json
                |> Result.bind (fun dto ->
                    tryGuid actionTypeName "mergeId" dto.MergeId
                    |> Result.map (fun mergeId -> NotifyMergeApplied(mergeId, dto.SourceCartId)))
            | t when t = Tags.RevokeGuestCapabilityAction -> decodeMerge RevokeGuestCapability
            | _ -> Error(unknownTag actionTypeName tag))

    let private errorTypeName = "CartEffectError"

    let private errorTag =
        function
        | CartActionError.InvalidCartEntityId -> Tags.InvalidCartEntityIdError
        | CartActionError.CapabilityLookupFailed -> Tags.CapabilityLookupFailedError
        | CartActionError.MergeSnapshotAlreadyCaptured -> Tags.MergeSnapshotAlreadyCapturedError
        | CartActionError.MergeTargetNotFound -> Tags.MergeTargetNotFoundError
        | CartActionError.CallbackEncodingFailed -> Tags.CallbackEncodingFailedError
        | CartActionError.ActionReceiptMismatch -> Tags.ActionReceiptMismatchError

    let private encodeError (reason: CartActionError) : Result<string, CodecError> =
        let dto: CartWire.TaggedDto = { Tag = errorTag reason }
        encodeValue errorTypeName dto

    let private decodeError (json: string) : Result<CartActionError, CodecError> =
        decodeExact<CartWire.TaggedDto> errorTypeName json
        |> Result.bind (fun dto ->
            match dto.Tag with
            | Tags.InvalidCartEntityIdError -> Ok CartActionError.InvalidCartEntityId
            | Tags.CapabilityLookupFailedError -> Ok CartActionError.CapabilityLookupFailed
            | Tags.MergeSnapshotAlreadyCapturedError -> Ok CartActionError.MergeSnapshotAlreadyCaptured
            | Tags.MergeTargetNotFoundError -> Ok CartActionError.MergeTargetNotFound
            | Tags.CallbackEncodingFailedError -> Ok CartActionError.CallbackEncodingFailed
            | Tags.ActionReceiptMismatchError -> Ok CartActionError.ActionReceiptMismatch
            | tag -> Error(unknownTag errorTypeName tag))

    let state: Codec<CartState> = Codec.create encodeState decodeState

    let event: Codec<CartEvent> = Codec.create encodeEvent decodeEvent

    let action: Codec<CartAction> = Codec.create encodeAction decodeAction

    let error: Codec<CartActionError> = Codec.create encodeError decodeError

    let storeOptions
        (context: PostgresContext)
        : MachineStoreOptions<CartId, CartState, CartEvent, CartAction, CartActionError> =
        { MachineStoreOptions.forEntityId<Cart, CartState, CartEvent, CartAction, CartActionError>
              context
              Cart.ActionQueue with
            StateCodec = state
            EventCodec = event
            ActionCodec = action
            ErrorCodec = error }

    let workerStore (context: PostgresContext) =
        storeOptions context |> PostgresMachineStore

    let clientStore (context: PostgresContext) =
        { storeOptions context with
            Listener = ListenerConnection.Off }
        |> PostgresMachineStore

    let private build (log: ILogger) storeArg =
        machine<CartId, CartState, CartEvent, CartAction, CartActionError> (machineId Cart.MachineKey) {
            chart Cart.chartValue
            chartVersion 1
            initialState Cart.initialState
            store storeArg
            logger log
        }

    let buildWorker (log: ILogger) (context: PostgresContext) =
        build log (workerStore context :> IMachineStore<CartId, CartState, CartEvent, CartAction, CartActionError>)

    let buildClient (log: ILogger) (context: PostgresContext) =
        build log (clientStore context :> IMachineStore<CartId, CartState, CartEvent, CartAction, CartActionError>)
