namespace App

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open App.Database
open App.Domain
open MailKit
open MailKit.Net.Smtp
open MailKit.Security
open Microsoft.AspNetCore.DataProtection
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open MimeKit
open Npgsql

/// <summary>SMTP transport configuration. Development defaults target the devenv Mailpit
/// instance; production must configure the section explicitly and is validated at startup.</summary>
type EmailOptions =
    { Host: string
      Port: int
      UseTls: bool
      Username: string option
      Password: string option
      FromAddress: string
      FromName: string
      PublicOrigin: string }

[<RequireQualifiedAccess>]
module EmailOptions =

    let private nonEmpty (value: string) =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private parseWith (parse: string -> bool * 'T) (value: string option) =
        value
        |> Option.bind (fun text ->
            match parse text with
            | true, parsed -> Some parsed
            | _ -> None)

    let load (configuration: IConfiguration) : EmailOptions =
        let setting key =
            configuration[$"Email:{key}"] |> nonEmpty

        { Host = setting "Host" |> Option.defaultValue "127.0.0.1"
          Port = setting "Port" |> parseWith Int32.TryParse |> Option.defaultValue 1025
          UseTls = setting "UseTls" |> parseWith Boolean.TryParse |> Option.defaultValue false
          Username = setting "Username"
          Password = setting "Password"
          FromAddress = setting "FromAddress" |> Option.defaultValue "fsnix@example.test"
          FromName = setting "FromName" |> Option.defaultValue "fsnix"
          PublicOrigin = (setting "PublicOrigin" |> Option.defaultValue "http://localhost:5000").TrimEnd('/') }

    let private isAbsoluteHttpOrigin (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri -> uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps
        | _ -> false

    let private isLoopbackHost (host: string) =
        match host.Trim().ToLowerInvariant() with
        | "localhost"
        | "127.0.0.1"
        | "::1" -> true
        | _ -> false

    let private isValidMailbox (address: string) =
        let mutable parsed = Unchecked.defaultof<MailboxAddress>
        MailboxAddress.TryParse(address, &parsed)

    /// <summary>Startup validation. Production never silently delivers through a loopback
    /// catch-all or without TLS: both are development conveniences, not deployable states.</summary>
    let validate (environmentName: string) (options: EmailOptions) : Result<unit, string> =
        let isProduction =
            String.Equals(environmentName, "Production", StringComparison.Ordinal)

        if String.IsNullOrWhiteSpace options.Host then
            Error "Email:Host is required."
        elif options.Port < 1 || options.Port > 65535 then
            Error "Email:Port must be between 1 and 65535."
        elif not (isValidMailbox options.FromAddress) then
            Error "Email:FromAddress is not a valid mailbox address."
        elif not (isAbsoluteHttpOrigin options.PublicOrigin) then
            Error "Email:PublicOrigin must be an absolute http(s) origin."
        elif isProduction && isLoopbackHost options.Host then
            Error "Production requires an explicitly configured Email:Host; loopback SMTP is development-only."
        elif isProduction && not options.UseTls then
            Error "Production requires Email:UseTls."
        else
            Ok()

/// <summary>One fully rendered outbound email. <c>MessageId</c> is deterministic per flow
/// generation so duplicate SMTP submissions can be recognised downstream.</summary>
type OutgoingEmail =
    { To: string
      Subject: string
      TextBody: string
      HtmlBody: string
      MessageId: string }

[<RequireQualifiedAccess>]
type EmailFailure =
    | SmtpAuthentication
    | SmtpStatus of int
    | SmtpProtocol
    | SmtpAddressMalformed
    | SmtpConnection
    | PayloadUndecryptable
    | PayloadMalformed
    | PayloadFlowMismatch
    | RequestInactive

[<RequireQualifiedAccess>]
module EmailFailure =
    let wireCode =
        function
        | EmailFailure.SmtpAuthentication -> "smtp-authentication"
        | EmailFailure.SmtpStatus code -> $"smtp-status-{code}"
        | EmailFailure.SmtpProtocol -> "smtp-protocol"
        | EmailFailure.SmtpAddressMalformed -> "smtp-address-malformed"
        | EmailFailure.SmtpConnection -> "smtp-connection"
        | EmailFailure.PayloadUndecryptable -> "payload-undecryptable"
        | EmailFailure.PayloadMalformed -> "payload-malformed"
        | EmailFailure.PayloadFlowMismatch -> "payload-flow-mismatch"
        | EmailFailure.RequestInactive -> "request-inactive"

    let retryable =
        function
        | EmailFailure.SmtpStatus code -> code >= 400 && code < 500
        | EmailFailure.SmtpProtocol
        | EmailFailure.SmtpConnection -> true
        | EmailFailure.SmtpAuthentication
        | EmailFailure.SmtpAddressMalformed
        | EmailFailure.PayloadUndecryptable
        | EmailFailure.PayloadMalformed
        | EmailFailure.PayloadFlowMismatch
        | EmailFailure.RequestInactive -> false

/// <summary>The narrow transport boundary the email relay depends on. SMTP is at-least-once:
/// acceptance does not prove mailbox delivery, and a crash before settlement can resend. A
/// future API provider with idempotency and reconciliation support implements the same
/// interface without touching the relay.</summary>
type IEmailTransport =
    abstract Send: email: OutgoingEmail * ct: CancellationToken -> Task<Result<unit, EmailFailure>>

/// <summary>MailKit SMTP adapter. 4xx replies are retryable, 5xx replies permanent, and
/// connection/protocol failures retryable; nothing about the wire error escapes into the
/// classification beyond its status code.</summary>
type SmtpEmailTransport(options: EmailOptions) =

    let send (email: OutgoingEmail) (ct: CancellationToken) =
        task {
            use message = new MimeMessage()
            message.From.Add(MailboxAddress(options.FromName, options.FromAddress))
            message.To.Add(MailboxAddress.Parse email.To)
            message.Subject <- email.Subject
            message.MessageId <- email.MessageId

            let body = BodyBuilder()
            body.TextBody <- email.TextBody
            body.HtmlBody <- email.HtmlBody
            message.Body <- body.ToMessageBody()

            use client = new SmtpClient()

            let secure =
                if options.UseTls then
                    SecureSocketOptions.StartTls
                else
                    SecureSocketOptions.None

            do! client.ConnectAsync(options.Host, options.Port, secure, ct)

            match options.Username with
            | Some username -> do! client.AuthenticateAsync(username, defaultArg options.Password "", ct)
            | None -> ()

            let! _ = client.SendAsync(message, ct)
            do! client.DisconnectAsync(true, ct)
            return Ok()
        }

    interface IEmailTransport with
        member _.Send(email, ct) =
            task {
                try
                    return! send email ct
                with
                | :? OperationCanceledException when ct.IsCancellationRequested ->
                    return raise (OperationCanceledException ct)
                | :? MailKit.Security.AuthenticationException -> return Error EmailFailure.SmtpAuthentication
                | :? SmtpCommandException as error ->
                    let code = int error.StatusCode

                    return Error(EmailFailure.SmtpStatus code)
                | :? SmtpProtocolException -> return Error EmailFailure.SmtpProtocol
                | :? ParseException -> return Error EmailFailure.SmtpAddressMalformed
                | :? IOException
                | :? SocketException -> return Error EmailFailure.SmtpConnection
            }

/// <summary>Renders account-flow notification emails. The action link is the only secret the
/// message carries; it is URL-encoded for transport and HTML-encoded for the markup body.</summary>
[<RequireQualifiedAccess>]
module AccountEmailMessages =

    let private purposePath =
        function
        | EmailVerification -> "confirm-email"
        | PasswordReset -> "reset-password"
        | EmailChange -> "confirm-email-change"

    let actionLink (publicOrigin: string) (kind: FlowKind) (flowId: Guid) (token: string) =
        let origin = publicOrigin.TrimEnd('/')
        $"{origin}/account/{purposePath kind}?flowId={flowId:D}&token={Uri.EscapeDataString token}"

    /// <summary>Deterministic RFC message id: retries of the same notification generation
    /// present the same id, which downstream deduplication and diagnostics can key on.</summary>
    let messageId (flowId: Guid) (generation: int) = $"{flowId:N}.{generation}@fsnix.local"

    let private subject =
        function
        | EmailVerification -> "Confirm your email address"
        | PasswordReset -> "Reset your password"
        | EmailChange -> "Confirm your new email address"

    let private instruction =
        function
        | EmailVerification -> "confirm this email address"
        | PasswordReset -> "choose a new password"
        | EmailChange -> "confirm this as your new email address"

    let build (kind: FlowKind) (recipient: string) (link: string) (messageId: string) : OutgoingEmail =
        let title = subject kind
        let action = instruction kind

        let text =
            $"A request was made to {action} for this account.\n\n{link}\n\nIf you did not make this request, you can ignore this message.\n"

        let escapedLink = WebUtility.HtmlEncode link

        let html =
            $"<p>A request was made to {action} for this account.</p><p><a href=\"{escapedLink}\">{title}</a></p><p>If you did not make this request, you can ignore this message.</p>"

        { To = recipient
          Subject = title
          TextBody = text
          HtmlBody = html
          MessageId = messageId }

/// <summary>
/// One email-delivery pass: claim protected rows, unprotect and render them outside any
/// database transaction, hand them to the transport, then settle each row with its
/// generation-matched machine callback. Resolution failures (wrong key ring, malformed
/// payload, flow already completed) are permanent: the row is dead-lettered and the flow is
/// told its notification failed.
/// </summary>
[<RequireQualifiedAccess>]
module EmailDelivery =

    let private deliverOne
        (dataSource: NpgsqlDataSource)
        (dataProtection: IDataProtectionProvider)
        (transport: IEmailTransport)
        (options: EmailOptions)
        (email: ClaimedEmail)
        (ct: CancellationToken)
        : Task<Result<unit, EmailFailure>> =
        task {
            match AccountEmail.tryUnprotect dataProtection email.ProtectedPayload with
            | None -> return Error EmailFailure.PayloadUndecryptable
            | Some plaintext ->
                match AccountEmail.tryDecodePayload plaintext with
                | Error _ -> return Error EmailFailure.PayloadMalformed
                | Ok payload when payload.FlowId <> email.FlowId -> return Error EmailFailure.PayloadFlowMismatch
                | Ok payload ->
                    match! AccountFlowEffects.tryLoadRequest dataSource email.FlowId ct with
                    | None -> return Error EmailFailure.RequestInactive
                    | Some request ->
                        let link =
                            AccountEmailMessages.actionLink
                                options.PublicOrigin
                                payload.Kind
                                payload.FlowId
                                payload.Token

                        let message =
                            AccountEmailMessages.build
                                payload.Kind
                                request.DestinationEmail
                                link
                                (AccountEmailMessages.messageId email.FlowId email.Generation)

                        return! transport.Send(message, ct)
        }

    /// <summary>Claims and delivers one batch. Returns the number of rows settled as sent.
    /// Settlement defects (an impossible callback-encode failure) abort the pass; the rows
    /// stay leased and become claimable again after their lease expires.</summary>
    let deliverPending
        (dataSource: NpgsqlDataSource)
        (dataProtection: IDataProtectionProvider)
        (transport: IEmailTransport)
        (options: EmailOptions)
        (relayOptions: EmailOutbox.RelayOptions)
        (ct: CancellationToken)
        : Task<int> =
        task {
            let! claimed = EmailOutbox.claim dataSource relayOptions ct
            let mutable settled = 0

            for email in claimed do
                let! outcome = deliverOne dataSource dataProtection transport options email ct

                match outcome with
                | Ok() ->
                    match! EmailOutbox.settleSent dataSource relayOptions email ct with
                    | Ok true -> settled <- settled + 1
                    | Ok false -> ()
                    | Error message -> raise (InvalidOperationException message)
                | Error sendError ->
                    match!
                        EmailOutbox.fail
                            dataSource
                            relayOptions
                            email
                            (EmailFailure.wireCode sendError)
                            (not (EmailFailure.retryable sendError))
                            ct
                    with
                    | Ok() -> ()
                    | Error message -> raise (InvalidOperationException message)

            return settled
        }
