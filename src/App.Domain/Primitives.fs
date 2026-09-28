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
