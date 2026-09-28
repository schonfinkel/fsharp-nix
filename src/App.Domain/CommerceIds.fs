namespace App.Domain

open System

/// <summary>A catalog product's non-secret identifier.</summary>
[<Struct>]
type ProductId = private ProductId of value: Guid

module ProductId =

    let create (value: Guid) : Result<ProductId, string> =
        if value = Guid.Empty then
            Error "A product id must not be empty."
        else
            Ok(ProductId value)

    let value (ProductId id) = id

    let tryParse (value: string) : Result<ProductId, string> =
        match Guid.TryParseExact(value, "D") with
        | true, id -> create id
        | false, _ -> Error "A product id must be a GUID in \"D\" format."

    let wireString (id: ProductId) = (value id).ToString("D")

/// <summary>A product SKU: non-empty, bounded, upper-cased at construction, and never
/// normalized again, so a cart line snapshot stays byte-for-byte what the operator entered.</summary>
type Sku = private Sku of value: string

module Sku =

    let create (value: string) : Result<Sku, string> =
        if isNull value then
            Error "A SKU is required."
        else
            let trimmed = value.Trim().ToUpperInvariant()

            if trimmed.Length = 0 then
                Error "A SKU is required."
            elif trimmed.Length > 64 then
                Error "A SKU must be at most 64 characters."
            elif
                trimmed
                |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c || c = '-' || c = '_'))
            then
                Error "A SKU may contain only ASCII letters, digits, hyphens, and underscores."
            else
                Ok(Sku trimmed)

    let value (Sku value) = value

    let tryParse (value: string) : Result<Sku, string> = create value

/// <summary>A positive order/cart quantity.</summary>
[<Struct>]
type Quantity = private Quantity of value: int

module Quantity =
    let maxValue = 999

    let create (value: int) : Result<Quantity, string> =
        if value < 1 then
            Error "A quantity must be at least 1."
        elif value > maxValue then
            Error $"A quantity must be at most %d{maxValue}."
        else
            Ok(Quantity value)

    let value (Quantity value) = value

    let tryParse (value: int) : Result<Quantity, string> = create value

/// <summary>A monotonically increasing catalog price version. Bumped whenever a product's
/// price changes, so a later reservation can reject a stale price snapshot.</summary>
[<Struct>]
type PriceVersion = private PriceVersion of value: int64

module PriceVersion =
    let create (value: int64) : Result<PriceVersion, string> =
        if value < 1L then
            Error "A price version must be positive."
        else
            Ok(PriceVersion value)

    let value (PriceVersion value) = value

/// <summary>Stable identifier for one immutable order line.</summary>
[<Struct>]
type OrderLineId = private OrderLineId of Guid

module OrderLineId =
    let create (value: Guid) =
        if value = Guid.Empty then
            Error "An order-line id must not be empty."
        else
            Ok(OrderLineId value)

    let value (OrderLineId value) = value

    let wireString id = (value id).ToString("D")

/// <summary>Stable identifier for a stock reservation ledger row.</summary>
[<Struct>]
type ReservationId = private ReservationId of Guid

module ReservationId =
    let create (value: Guid) =
        if value = Guid.Empty then
            Error "A reservation id must not be empty."
        else
            Ok(ReservationId value)

    let value (ReservationId value) = value

    let wireString id = (value id).ToString("D")

/// <summary>Reference to a restricted immutable checkout snapshot.</summary>
[<Struct>]
type OrderSnapshotId = private OrderSnapshotId of Guid

module OrderSnapshotId =
    let create (value: Guid) =
        if value = Guid.Empty then
            Error "An order snapshot id must not be empty."
        else
            Ok(OrderSnapshotId value)

    let value (OrderSnapshotId value) = value

    let wireString id = (value id).ToString("D")
