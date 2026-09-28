namespace App.Domain

/// <summary>The three account-flow purposes. Each kind has its own Identity token purpose and
/// its own expiry policy; the wire tags are stable forever.</summary>
type FlowKind =
    | EmailVerification
    | PasswordReset
    | EmailChange

[<RequireQualifiedAccess>]
module FlowKind =

    [<Literal>]
    let EmailVerificationWire = "email-verification"

    [<Literal>]
    let PasswordResetWire = "password-reset"

    [<Literal>]
    let EmailChangeWire = "email-change"

    let wireName =
        function
        | EmailVerification -> EmailVerificationWire
        | PasswordReset -> PasswordResetWire
        | EmailChange -> EmailChangeWire

    let tryParseWireName =
        function
        | EmailVerificationWire -> Some EmailVerification
        | PasswordResetWire -> Some PasswordReset
        | EmailChangeWire -> Some EmailChange
        | _ -> None
