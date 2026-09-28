namespace App.Domain

open System

/// <summary>Stable identifier for one payment authorization entity (one per order).</summary>
[<Struct>]
type PaymentId = private PaymentId of value: Guid

module PaymentId =

    let create (value: Guid) =
        if value = Guid.Empty then
            Error "A payment id must not be empty."
        else
            Ok(PaymentId value)

    let value (PaymentId id) = id

    let wireString id = (value id).ToString("D")

/// <summary>A stable operation identifier passed to the payment gateway as the idempotency key.
/// One operation id names exactly one provider call (authorize or void).</summary>
type PaymentOperationId = private PaymentOperationId of value: string

module PaymentOperationId =

    // Leave room for the derived ":void" provider idempotency key.
    let maxValueLength = 123

    let create (value: string) : Result<PaymentOperationId, string> =
        if isNull value then
            Error "A payment operation id is required."
        else
            let trimmed = value.Trim()

            if trimmed.Length = 0 then
                Error "A payment operation id is required."
            elif trimmed.Length > maxValueLength then
                Error $"A payment operation id must be at most %d{maxValueLength} characters."
            elif
                trimmed
                |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c || c = '-' || c = ':' || c = '_'))
            then
                Error "A payment operation id may contain only ASCII letters, digits, hyphens, colons, and underscores."
            else
                Ok(PaymentOperationId trimmed)

    let value (PaymentOperationId value) = value

    let tryParse (value: string) = create value

    /// <summary>Derives the void operation id for one authorization attempt, so authorize and
    /// void calls never share a provider idempotency key.</summary>
    let voidOf (operationId: PaymentOperationId) : PaymentOperationId =
        PaymentOperationId $"{value operationId}:void"

/// <summary>An opaque payment-method reference handed to the gateway. It is a sandbox token by
/// construction: never a card number, CVV, or any other cardholder secret, and those values are
/// rejected by the allowed shape rather than trusted from callers.</summary>
type PaymentMethodReference = private PaymentMethodReference of value: string

module PaymentMethodReference =

    let maxValueLength = 64

    let create (value: string) : Result<PaymentMethodReference, string> =
        if isNull value then
            Error "A payment method reference is required."
        else
            let trimmed = value.Trim()

            if trimmed.Length = 0 then
                Error "A payment method reference is required."
            elif trimmed.Length > maxValueLength then
                Error $"A payment method reference must be at most %d{maxValueLength} characters."
            elif
                trimmed = "sandbox://success"
                || trimmed = "sandbox://decline"
                || trimmed = "sandbox://unknown"
            then
                Ok(PaymentMethodReference trimmed)
            else
                Error "The payment method reference is not a supported sandbox token."

    let value (PaymentMethodReference value) = value

    let tryParse (value: string) = create value

    /// <summary>Sandbox method references recognized by the simulated gateway.</summary>
    [<RequireQualifiedAccess>]
    module Sandbox =

        /// <summary>Authorizes successfully on the first call.</summary>
        let Success =
            "sandbox://success"
            |> create
            |> Result.defaultWith (fun _ -> invalidOp "fixed reference")

        /// <summary>Declines with a bounded reason code.</summary>
        let Decline =
            "sandbox://decline"
            |> create
            |> Result.defaultWith (fun _ -> invalidOp "fixed reference")

        /// <summary>Returns an unknown outcome on the first call and resolves deterministically
        /// when queried, exercising the query-before-retry protocol.</summary>
        let Unknown =
            "sandbox://unknown"
            |> create
            |> Result.defaultWith (fun _ -> invalidOp "fixed reference")
