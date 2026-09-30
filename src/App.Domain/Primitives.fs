namespace App.Domain

/// <summary>A trimmed, non-empty string of bounded length. The only free-text shape the
/// account domain persists; everything else is a typed identifier or counter.</summary>
type NonEmptyString = private NonEmptyString of value: string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module NonEmptyString =

    let create (maxLength: int) (value: string) : Result<NonEmptyString, string> =
        if maxLength < 1 then
            invalidArg (nameof maxLength) "A non-empty string bound must be positive."

        match value with
        | null -> Error "A required value was null."
        | value ->
            let trimmed = value.Trim()

            if trimmed.Length = 0 then
                Error "A required value was empty."
            elif trimmed.Length > maxLength then
                Error $"A required value exceeded %d{maxLength} characters."
            else
                Ok(NonEmptyString trimmed)

    let value (NonEmptyString trimmed) = trimmed

/// <summary>
/// An internal, non-secret reason code: a lowercase kebab-case slug of at most 64 characters
/// (<c>insufficient-stock</c>, <c>provider-declined</c>). Reason codes are the only failure
/// detail that reaches durable FSM data, logs, and customers; unrestricted provider error text
/// never does.
/// </summary>
type ReasonCode = private ReasonCode of value: string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ReasonCode =
    [<Literal>]
    let MaxLength = 64

    let private valid (value: string) =
        not (isNull value)
        && value.Length >= 1
        && value.Length <= MaxLength
        && value[0] <> '-'
        && value[value.Length - 1] <> '-'
        && value
           |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-')

    let create (value: string) : Result<ReasonCode, string> =
        if valid value then
            Ok(ReasonCode value)
        else
            Error "A reason code must be a lowercase kebab-case slug of at most 64 characters."

    /// <summary>For compile-time constant codes only; an invalid literal is a programming
    /// defect and fails at first use.</summary>
    let ofLiteral (value: string) =
        match create value with
        | Ok code -> code
        | Error message -> invalidArg (nameof value) message

    /// <summary>Normalises untrusted text (for example a provider decline code) into a reason
    /// code, falling back to <paramref name="fallback"/> when it cannot be represented.</summary>
    let sanitize (fallback: ReasonCode) (value: string) =
        match create value with
        | Ok code -> code
        | Error _ -> fallback

    let value (ReasonCode value) = value
