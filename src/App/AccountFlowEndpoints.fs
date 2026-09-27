namespace App

open System
open System.ComponentModel.DataAnnotations
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Auth
open App.Database
open App.Domain
open App.Views
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Oxpecker

/// <summary>Public registration and account-recovery workflows. Public request responses are
/// deliberately uniform across account existence; flow IDs and Identity tokens stay in the
/// restricted request/payload boundary until rendered into the emailed link.</summary>
[<RequireQualifiedAccess>]
module AccountFlowEndpoints =

    let private genericRequestMessage =
        "If the request can be completed, an email will be sent shortly."

    let private genericRegistrationMessage =
        "If registration can proceed, check your email for the next step."

    let private accountPage (context: HttpContext) title fragment =
        Web.noStore context
        Web.varyHtmx context

        if Web.isHtmx context then
            context.WriteHtmlView fragment
        else
            context.WriteHtmlView(SharedViews.layout context title fragment)

    let private readForm (context: HttpContext) =
        context.Request.ReadFormAsync context.RequestAborted

    let private field name (form: IFormCollection) =
        match form.TryGetValue name with
        | true, value -> string value
        | false, _ -> ""

    let private query (context: HttpContext) name = context.Request.Query[name] |> string

    let private parseFlowId (value: string) =
        match Guid.TryParse value with
        | true, id when id <> Guid.Empty -> Some id
        | _ -> None

    let private idempotencyKeyValid (value: string) =
        value.Length >= 16
        && value.Length <= 128
        && value
           |> Seq.forall (fun character ->
               Char.IsAsciiLetterOrDigit character
               || character = '-'
               || character = '_'
               || character = '.'
               || character = ':')

    let private keyHashes (key: string) (userId: Guid) (kind: FlowKind) (destination: string) =
        let keyHash = SHA256.HashData(Encoding.UTF8.GetBytes key)

        // userId is high-entropy and kind-separated, so the persisted request fingerprint
        // cannot be used as an offline email dictionary.
        let canonical =
            $"{userId:N}\n{FlowKind.wireName kind}\n{destination.Trim().ToUpperInvariant()}"

        keyHash, SHA256.HashData(Encoding.UTF8.GetBytes canonical)

    let private newIdempotencyKey () = Guid.NewGuid().ToString("N")

    let private responseNotice
        (context: HttpContext)
        (title: string)
        (status: int)
        (message: string)
        (error: string option)
        =
        context.Response.StatusCode <- status
        accountPage context title (AccountViews.notice title { Message = message; Error = error })

    let private requestModel email message error key : AccountRequestModel =
        { Email = email
          Message = message
          Error = error
          IdempotencyKey = key }

    let private createExistingUserRequest
        (context: HttpContext)
        (user: ApplicationUser)
        (kind: FlowKind)
        (destination: string)
        (idempotencyKey: string)
        (expiresAt: DateTimeOffset)
        (ct: CancellationToken)
        =
        task {
            let keyHash, requestHash = keyHashes idempotencyKey user.Id kind destination
            let dataSource = context.GetService<Npgsql.NpgsqlDataSource>()

            return! AccountFlowRequests.create dataSource kind user.Id destination expiresAt keyHash requestHash ct
        }

    let registerPage: EndpointHandler =
        fun context ->
            accountPage
                context
                "Create an account"
                (AccountViews.register
                    context
                    { Email = ""
                      Error = None
                      IdempotencyKey = newIdempotencyKey () })

    let register: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let email = field "email" form |> fun value -> value.Trim()
                let password = field "password" form
                let confirmation = field "confirmPassword" form
                let key = field "idempotencyKey" form

                let invalid message =
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                    accountPage
                        context
                        "Create an account"
                        (AccountViews.register
                            context
                            { Email = email
                              Error = Some message
                              IdempotencyKey =
                                if idempotencyKeyValid key then
                                    key
                                else
                                    newIdempotencyKey () })

                if not (idempotencyKeyValid key) then
                    context.Response.StatusCode <- StatusCodes.Status400BadRequest

                    return!
                        accountPage
                            context
                            "Create an account"
                            (AccountViews.register
                                context
                                { Email = email
                                  Error = Some "The request could not be processed. Please try again."
                                  IdempotencyKey = newIdempotencyKey () })
                elif not (EmailAddressAttribute().IsValid email) then
                    return! invalid "Enter a valid email address."
                elif password <> confirmation then
                    return! invalid "The passwords do not match."
                elif
                    password.Length < 12
                    || not (password |> Seq.exists Char.IsUpper)
                    || not (password |> Seq.exists Char.IsLower)
                    || not (password |> Seq.exists Char.IsDigit)
                    || not (password |> Seq.exists (Char.IsLetterOrDigit >> not))
                then
                    return!
                        invalid
                            "Use at least 12 characters, including upper- and lower-case letters, a number, and a symbol."
                else
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let operationContext = context.GetService<AccountOperationContext>()
                    let user = ApplicationUser()
                    user.Id <- Guid.NewGuid()
                    user.UserName <- email
                    user.NormalizedUserName <- users.NormalizeName email
                    user.Email <- email
                    user.NormalizedEmail <- users.NormalizeEmail email
                    user.EmailConfirmed <- false

                    let expiresAt =
                        context.GetService<TimeProvider>().GetUtcNow()
                        + AccountTokenLifespans.EmailConfirmation

                    match AccountOperation.forRegistration EmailVerification user.Id email expiresAt with
                    | Error _ ->
                        context.Response.StatusCode <- StatusCodes.Status500InternalServerError

                        return!
                            accountPage
                                context
                                "Create an account"
                                (AccountViews.notice
                                    "Create an account"
                                    { Message = "The request could not be completed."
                                      Error = None })
                    | Ok operation ->
                        match operationContext.Attach operation with
                        | Error _ ->
                            context.Response.StatusCode <- StatusCodes.Status500InternalServerError

                            return!
                                accountPage
                                    context
                                    "Create an account"
                                    (AccountViews.notice
                                        "Create an account"
                                        { Message = "The request could not be completed."
                                          Error = None })
                        | Ok() ->
                            let! result = users.CreateAsync(user, password)

                            // Duplicate email/username and successful creation have the same
                            // public result; no Identity error descriptions are rendered.
                            if not result.Succeeded then
                                context.Response.StatusCode <- StatusCodes.Status202Accepted

                            return!
                                responseNotice
                                    context
                                    "Check your email"
                                    StatusCodes.Status202Accepted
                                    genericRegistrationMessage
                                    None
            }

    let forgotPasswordPage: EndpointHandler =
        fun context ->
            accountPage
                context
                "Reset your password"
                (AccountViews.forgotPassword context (requestModel "" None None (newIdempotencyKey ())))

    let forgotPassword: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let email = field "email" form |> fun value -> value.Trim()
                let key = field "idempotencyKey" form

                if not (idempotencyKeyValid key) || not (EmailAddressAttribute().IsValid email) then
                    context.Response.StatusCode <- StatusCodes.Status400BadRequest

                    return!
                        accountPage
                            context
                            "Reset your password"
                            (AccountViews.forgotPassword
                                context
                                (requestModel
                                    email
                                    None
                                    (Some "The request could not be processed. Please try again.")
                                    (if idempotencyKeyValid key then
                                         key
                                     else
                                         newIdempotencyKey ())))
                else
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let! user = users.FindByEmailAsync email

                    if not (isNull user) then
                        let expiresAt =
                            context.GetService<TimeProvider>().GetUtcNow()
                            + AccountTokenLifespans.PasswordReset

                        let! _ =
                            createExistingUserRequest
                                context
                                user
                                PasswordReset
                                user.Email
                                key
                                expiresAt
                                context.RequestAborted

                        ()

                    context.Response.StatusCode <- StatusCodes.Status202Accepted

                    return!
                        accountPage
                            context
                            "Check your email"
                            (AccountViews.forgotPassword
                                context
                                (requestModel email (Some genericRequestMessage) None (newIdempotencyKey ())))
            }

    let changeEmailPage: EndpointHandler =
        fun context ->
            task {
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! user = users.GetUserAsync context.User

                return!
                    accountPage
                        context
                        "Change email address"
                        (AccountViews.changeEmail
                            context
                            (requestModel (if isNull user then "" else user.Email) None None (newIdempotencyKey ())))
            }

    let changeEmail: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let email = field "email" form |> fun value -> value.Trim()
                let key = field "idempotencyKey" form
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! user = users.GetUserAsync context.User

                if isNull user then
                    return! Web.redirect "/account/login" context
                elif not (idempotencyKeyValid key) || not (EmailAddressAttribute().IsValid email) then
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                    return!
                        accountPage
                            context
                            "Change email address"
                            (AccountViews.changeEmail
                                context
                                (requestModel
                                    email
                                    None
                                    (Some "Enter a valid email address and try again.")
                                    (if idempotencyKeyValid key then
                                         key
                                     else
                                         newIdempotencyKey ())))
                elif String.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase) then
                    return!
                        accountPage
                            context
                            "Change email address"
                            (AccountViews.changeEmail
                                context
                                (requestModel
                                    email
                                    None
                                    (Some "That is already your email address.")
                                    (newIdempotencyKey ())))
                else
                    let expiresAt =
                        context.GetService<TimeProvider>().GetUtcNow()
                        + AccountTokenLifespans.ChangeEmail

                    let! result =
                        createExistingUserRequest context user EmailChange email key expiresAt context.RequestAborted

                    match result with
                    | Ok(AccountFlowRequestResult.Created _)
                    | Ok(AccountFlowRequestResult.Replayed _) ->
                        context.Response.StatusCode <- StatusCodes.Status202Accepted

                        return!
                            accountPage
                                context
                                "Check your new email"
                                (AccountViews.changeEmail
                                    context
                                    (requestModel email (Some genericRequestMessage) None (newIdempotencyKey ())))
                    | Ok AccountFlowRequestResult.IdempotencyConflict ->
                        context.Response.StatusCode <- StatusCodes.Status409Conflict

                        return!
                            accountPage
                                context
                                "Change email address"
                                (AccountViews.changeEmail
                                    context
                                    (requestModel
                                        email
                                        None
                                        (Some "That request key was already used for different details.")
                                        key))
                    | Ok AccountFlowRequestResult.UserNotFound
                    | Error _ ->
                        context.Response.StatusCode <- StatusCodes.Status503ServiceUnavailable

                        return!
                            accountPage
                                context
                                "Change email address"
                                (AccountViews.changeEmail
                                    context
                                    (requestModel
                                        email
                                        None
                                        (Some "The request could not be completed. Please try again.")
                                        key))
            }

    let resendPage: EndpointHandler =
        fun context ->
            accountPage
                context
                "Resend an account email"
                (AccountViews.resend context (requestModel "" None None (newIdempotencyKey ())))

    let resend: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let email = field "email" form |> fun value -> value.Trim()
                let key = field "idempotencyKey" form

                let kind =
                    match field "kind" form with
                    | "email-verification" -> Some EmailVerification
                    | "password-reset" -> Some PasswordReset
                    | _ -> None

                if
                    not (idempotencyKeyValid key)
                    || not (EmailAddressAttribute().IsValid email)
                    || kind.IsNone
                then
                    context.Response.StatusCode <- StatusCodes.Status400BadRequest

                    return!
                        accountPage
                            context
                            "Resend an account email"
                            (AccountViews.resend
                                context
                                (requestModel
                                    email
                                    None
                                    (Some "The request could not be processed. Please try again.")
                                    (newIdempotencyKey ())))
                else
                    let dataSource = context.GetService<Npgsql.NpgsqlDataSource>()

                    let! flowId =
                        AccountFlowRequests.tryFindActiveForEmail dataSource kind.Value email context.RequestAborted

                    match flowId with
                    | Some id ->
                        let keyHash = SHA256.HashData(Encoding.UTF8.GetBytes key) |> Convert.ToHexString
                        let envelopeKey = $"account-resend:v1:{id:N}:{keyHash}"
                        let flows = context.GetService<AccountFlowMachineClient>()

                        let! _ =
                            Machine.enqueue
                                flows.Flows
                                (entityId (id.ToString("D")))
                                (EventEnvelope.create envelopeKey ResendRequested)
                                context.RequestAborted

                        ()
                    | None -> ()

                    context.Response.StatusCode <- StatusCodes.Status202Accepted

                    return!
                        accountPage
                            context
                            "Check your email"
                            (AccountViews.resend
                                context
                                (requestModel email (Some genericRequestMessage) None (newIdempotencyKey ())))
            }

    let private tryLoadFlow
        (context: HttpContext)
        (flowId: Guid)
        (expectedKind: FlowKind)
        : Task<(AccountFlowRequestRow * ApplicationUser) option> =
        task {
            let dataSource = context.GetService<Npgsql.NpgsqlDataSource>()

            match! AccountFlowRequests.tryLoadActive dataSource flowId context.RequestAborted with
            | Some request when request.Kind = expectedKind ->
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! user = users.FindByIdAsync(request.UserId.ToString("D"))
                return if isNull user then None else Some(request, user)
            | _ -> return None
        }

    let private tokenModelFromQuery (context: HttpContext) : AccountTokenModel =
        { FlowId = query context "flowId"
          Token = query context "token"
          Error = None }

    let confirmEmailPage: EndpointHandler =
        fun context ->
            task {
                let model = tokenModelFromQuery context

                match parseFlowId model.FlowId with
                | None ->
                    return!
                        responseNotice
                            context
                            "Confirm email"
                            StatusCodes.Status400BadRequest
                            "This link is invalid or expired."
                            None
                | Some flowId ->
                    match! tryLoadFlow context flowId EmailVerification with
                    | None ->
                        return!
                            responseNotice
                                context
                                "Confirm email"
                                StatusCodes.Status400BadRequest
                                "This link is invalid or expired."
                                None
                    | Some _ ->
                        return!
                            accountPage
                                context
                                "Confirm email"
                                (AccountViews.emailAction context "Confirm email" "/account/confirm-email" model)
            }

    let confirmEmail: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let flowIdText = field "flowId" form
                let token = field "token" form

                match parseFlowId flowIdText with
                | None ->
                    return!
                        responseNotice
                            context
                            "Email confirmation"
                            StatusCodes.Status400BadRequest
                            "This link is invalid or expired."
                            None
                | Some flowId ->
                    match! tryLoadFlow context flowId EmailVerification with
                    | None ->
                        return!
                            responseNotice
                                context
                                "Email confirmation"
                                StatusCodes.Status400BadRequest
                                "This link is invalid or expired."
                                None
                    | Some(request, user) ->
                        let completedAt = context.GetService<TimeProvider>().GetUtcNow()

                        match AccountOperation.forCompletion request.FlowId request.UserId request.Kind completedAt with
                        | Error _ ->
                            return!
                                responseNotice
                                    context
                                    "Email confirmation"
                                    StatusCodes.Status500InternalServerError
                                    "The request could not be completed."
                                    None
                        | Ok operation ->
                            let operationContext = context.GetService<AccountOperationContext>()

                            match operationContext.Attach operation with
                            | Error _ ->
                                return!
                                    responseNotice
                                        context
                                        "Email confirmation"
                                        StatusCodes.Status500InternalServerError
                                        "The request could not be completed."
                                        None
                            | Ok() ->
                                let users = context.GetService<UserManager<ApplicationUser>>()
                                let! result = users.ConfirmEmailAsync(user, token)

                                if result.Succeeded then
                                    return!
                                        responseNotice
                                            context
                                            "Email confirmed"
                                            StatusCodes.Status200OK
                                            "Your email address has been confirmed."
                                            None
                                else
                                    return!
                                        responseNotice
                                            context
                                            "Email confirmation"
                                            StatusCodes.Status400BadRequest
                                            "This link is invalid or expired."
                                            None
            }

    let resetPasswordPage: EndpointHandler =
        fun context ->
            task {
                let model = tokenModelFromQuery context

                match parseFlowId model.FlowId with
                | None ->
                    return!
                        responseNotice
                            context
                            "Reset password"
                            StatusCodes.Status400BadRequest
                            "This link is invalid or expired."
                            None
                | Some flowId ->
                    match! tryLoadFlow context flowId PasswordReset with
                    | None ->
                        return!
                            responseNotice
                                context
                                "Reset password"
                                StatusCodes.Status400BadRequest
                                "This link is invalid or expired."
                                None
                    | Some _ ->
                        return!
                            accountPage
                                context
                                "Reset password"
                                (AccountViews.resetPassword
                                    context
                                    { FlowId = model.FlowId
                                      Token = model.Token
                                      Error = None })
            }

    let resetPassword: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let flowIdText = field "flowId" form
                let token = field "token" form
                let password = field "password" form
                let confirmation = field "confirmPassword" form

                let model =
                    { FlowId = flowIdText
                      Token = token
                      Error = None }

                let invalid message =
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                    accountPage
                        context
                        "Reset password"
                        (AccountViews.resetPassword context { model with Error = Some message })

                if password <> confirmation then
                    return! invalid "The passwords do not match."
                elif
                    password.Length < 12
                    || not (password |> Seq.exists Char.IsUpper)
                    || not (password |> Seq.exists Char.IsLower)
                    || not (password |> Seq.exists Char.IsDigit)
                    || not (password |> Seq.exists (Char.IsLetterOrDigit >> not))
                then
                    return!
                        invalid
                            "Use at least 12 characters, including upper- and lower-case letters, a number, and a symbol."
                else
                    match parseFlowId flowIdText with
                    | None ->
                        return!
                            responseNotice
                                context
                                "Reset password"
                                StatusCodes.Status400BadRequest
                                "This link is invalid or expired."
                                None
                    | Some flowId ->
                        match! tryLoadFlow context flowId PasswordReset with
                        | None ->
                            return!
                                responseNotice
                                    context
                                    "Reset password"
                                    StatusCodes.Status400BadRequest
                                    "This link is invalid or expired."
                                    None
                        | Some(request, user) ->
                            let completedAt = context.GetService<TimeProvider>().GetUtcNow()

                            match
                                AccountOperation.forCompletion request.FlowId request.UserId request.Kind completedAt
                            with
                            | Error _ ->
                                return!
                                    responseNotice
                                        context
                                        "Reset password"
                                        StatusCodes.Status500InternalServerError
                                        "The request could not be completed."
                                        None
                            | Ok operation ->
                                match (context.GetService<AccountOperationContext>()).Attach operation with
                                | Error _ ->
                                    return!
                                        responseNotice
                                            context
                                            "Reset password"
                                            StatusCodes.Status500InternalServerError
                                            "The request could not be completed."
                                            None
                                | Ok() ->
                                    let users = context.GetService<UserManager<ApplicationUser>>()
                                    let! result = users.ResetPasswordAsync(user, token, password)

                                    if result.Succeeded then
                                        return!
                                            responseNotice
                                                context
                                                "Password reset"
                                                StatusCodes.Status200OK
                                                "Your password has been reset. You can now sign in."
                                                None
                                    else
                                        return!
                                            responseNotice
                                                context
                                                "Reset password"
                                                StatusCodes.Status400BadRequest
                                                "This link is invalid or expired."
                                                None
            }

    let confirmEmailChangePage: EndpointHandler =
        fun context ->
            task {
                let model = tokenModelFromQuery context

                match parseFlowId model.FlowId with
                | None ->
                    return!
                        responseNotice
                            context
                            "Confirm email change"
                            StatusCodes.Status400BadRequest
                            "This link is invalid or expired."
                            None
                | Some flowId ->
                    match! tryLoadFlow context flowId EmailChange with
                    | None ->
                        return!
                            responseNotice
                                context
                                "Confirm email change"
                                StatusCodes.Status400BadRequest
                                "This link is invalid or expired."
                                None
                    | Some _ ->
                        return!
                            accountPage
                                context
                                "Confirm email change"
                                (AccountViews.emailAction
                                    context
                                    "Confirm email change"
                                    "/account/confirm-email-change"
                                    model)
            }

    let confirmEmailChange: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let flowIdText = field "flowId" form
                let token = field "token" form
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! current = users.GetUserAsync context.User

                if isNull current then
                    return! Web.redirect "/account/login" context
                else
                    match parseFlowId flowIdText with
                    | None ->
                        return!
                            responseNotice
                                context
                                "Confirm email change"
                                StatusCodes.Status400BadRequest
                                "This link is invalid or expired."
                                None
                    | Some flowId ->
                        match! tryLoadFlow context flowId EmailChange with
                        | None ->
                            return!
                                responseNotice
                                    context
                                    "Confirm email change"
                                    StatusCodes.Status400BadRequest
                                    "This link is invalid or expired."
                                    None
                        | Some(request, user) when user.Id <> current.Id ->
                            return!
                                responseNotice
                                    context
                                    "Confirm email change"
                                    StatusCodes.Status404NotFound
                                    "This link is invalid or expired."
                                    None
                        | Some(request, user) ->
                            let completedAt = context.GetService<TimeProvider>().GetUtcNow()

                            match
                                AccountOperation.forCompletion request.FlowId request.UserId request.Kind completedAt
                            with
                            | Error _ ->
                                return!
                                    responseNotice
                                        context
                                        "Confirm email change"
                                        StatusCodes.Status500InternalServerError
                                        "The request could not be completed."
                                        None
                            | Ok operation ->
                                match (context.GetService<AccountOperationContext>()).Attach operation with
                                | Error _ ->
                                    return!
                                        responseNotice
                                            context
                                            "Confirm email change"
                                            StatusCodes.Status500InternalServerError
                                            "The request could not be completed."
                                            None
                                | Ok() ->
                                    let! result = users.ChangeEmailAsync(user, request.DestinationEmail, token)

                                    if result.Succeeded then
                                        return!
                                            responseNotice
                                                context
                                                "Email changed"
                                                StatusCodes.Status200OK
                                                "Your email address has been changed."
                                                None
                                    else
                                        return!
                                            responseNotice
                                                context
                                                "Confirm email change"
                                                StatusCodes.Status400BadRequest
                                                "This link is invalid or expired."
                                                None
            }
