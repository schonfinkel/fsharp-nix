namespace App

open System
open System.Security.Claims
open FsToolkit.ErrorHandling
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Primitives
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

type LoginModel =
    { Email: string
      ReturnUrl: string
      Error: string option }

type TwoFactorModel =
    { ReturnUrl: string
      Error: string option }

type EnrollmentModel =
    { UserName: string
      Email: string
      SharedKey: string option
      AuthenticatorUri: string option
      RecoveryCodesLeft: int
      IsEnabled: bool
      Error: string option }

[<RequireQualifiedAccess>]
module AccountViews =
    let private antiforgeryForm (context: HttpContext) (action: string) (content: HtmlElement) =
        form(action = action, method = "post").hxPost(action).hxTarget("#account-panel").hxSwap ("outerHTML") {
            context.GetAntiforgeryInput()
            content
        }

    let private panel (title: string) (content: HtmlElement) =
        section (id = "account-panel", class' = "card account-card") {
            h1 () { title }
            content
        }

    let private errorMessage (error: string option) =
        Fragment() {
            for message in Option.toList error do
                p (class' = "error") { message }
        }

    let login (context: HttpContext) (model: LoginModel) =
        panel
            "Sign in"
            (Fragment() {
                p (class' = "muted") { "Use the email address assigned to your bootstrap account." }
                errorMessage model.Error

                antiforgeryForm
                    context
                    "/account/login"
                    (Fragment() {
                        input (type' = "hidden", name = "returnUrl", value = model.ReturnUrl)
                        label (for' = "email") { "Email" }

                        (input (type' = "email", id = "email", name = "email", value = model.Email, required = true))
                            .attr ("autocomplete", "username")

                        label (for' = "password") { "Password" }

                        (input (type' = "password", id = "password", name = "password", required = true))
                            .attr ("autocomplete", "current-password")

                        div (class' = "actions") { button (type' = "submit") { "Sign in" } }
                    })
            })

    let twoFactor (context: HttpContext) (model: TwoFactorModel) =
        panel
            "Authenticator code"
            (Fragment() {
                p (class' = "muted") { "Enter the six-digit code from your authenticator app." }
                errorMessage model.Error

                antiforgeryForm
                    context
                    "/account/login/2fa"
                    (Fragment() {
                        input (type' = "hidden", name = "returnUrl", value = model.ReturnUrl)
                        label (for' = "code") { "Authenticator code" }

                        (input (type' = "text", id = "code", name = "code", required = true))
                            .attr("inputmode", "numeric")
                            .attr ("autocomplete", "one-time-code")

                        div (class' = "actions") { button (type' = "submit") { "Verify" } }
                    })

                p () {
                    a (href = $"/account/login/recovery?returnUrl={Uri.EscapeDataString model.ReturnUrl}") {
                        "Use a recovery code"
                    }
                }
            })

    let recoveryLogin (context: HttpContext) (model: TwoFactorModel) =
        panel
            "Recovery code"
            (Fragment() {
                p (class' = "muted") { "Each recovery code can be used once." }
                errorMessage model.Error

                antiforgeryForm
                    context
                    "/account/login/recovery"
                    (Fragment() {
                        input (type' = "hidden", name = "returnUrl", value = model.ReturnUrl)
                        label (for' = "recovery-code") { "Recovery code" }

                        (input (type' = "text", id = "recovery-code", name = "code", required = true))
                            .attr ("autocomplete", "one-time-code")

                        div (class' = "actions") { button (type' = "submit") { "Sign in" } }
                    })

                p () {
                    a (href = $"/account/login/2fa?returnUrl={Uri.EscapeDataString model.ReturnUrl}") {
                        "Use an authenticator code"
                    }
                }
            })

    let enrollment (context: HttpContext) (model: EnrollmentModel) =
        panel
            "Two-factor authentication"
            (Fragment() {
                p () { $"Signed in as {model.UserName} ({model.Email})." }
                errorMessage model.Error

                if model.IsEnabled then
                    p (class' = "success") { "Two-factor authentication is enabled." }
                    p (class' = "muted") { $"{model.RecoveryCodesLeft} recovery codes remain." }

                    antiforgeryForm
                        context
                        "/account/2fa/recovery-codes"
                        (Fragment() {
                            div (class' = "actions") { button (type' = "submit") { "Generate new recovery codes" } }
                        })

                    antiforgeryForm
                        context
                        "/account/2fa/reset"
                        (Fragment() {
                            div (class' = "actions") {
                                button (type' = "submit", class' = "danger") { "Reset authenticator" }
                            }
                        })
                else
                    p (class' = "error") {
                        "Two-factor authentication is required before feature administration is available."
                    }

                    match model.SharedKey, model.AuthenticatorUri with
                    | Some sharedKey, Some authenticatorUri ->
                        p () { "Scan this QR code or enter the key manually, then verify a generated code." }
                        (div (id = "qr-code")).data ("url", authenticatorUri) { }

                        p () {
                            span (class' = "muted") { "Manual key: " }
                            code () { sharedKey }
                        }

                        antiforgeryForm
                            context
                            "/account/2fa/enable"
                            (Fragment() {
                                label (for' = "code") { "Authenticator code" }

                                (input (type' = "text", id = "code", name = "code", required = true))
                                    .attr("inputmode", "numeric")
                                    .attr ("autocomplete", "one-time-code")

                                div (class' = "actions") { button (type' = "submit") { "Enable 2FA" } }
                            })

                    | _ ->
                        p () { "Create an authenticator key to begin enrollment." }

                        antiforgeryForm
                            context
                            "/account/2fa/key"
                            (Fragment() { div (class' = "actions") { button (type' = "submit") { "Begin setup" } } })
            })

    let recoveryCodes (codes: string list) =
        panel
            "Save your recovery codes"
            (Fragment() {
                p (class' = "error") { "These codes are shown once. Store them securely before continuing." }

                ul (class' = "recovery-codes") {
                    for recoveryCode in codes do
                        li () { code () { recoveryCode } }
                }

                p () { a (href = "/admin/features") { "Continue to feature administration" } }
            })

[<RequireQualifiedAccess>]
module Account =
    type private AuthenticatorFailure =
        | InvalidCode
        | CreateKeyFailed
        | EnableFailed
        | SecureSessionFailed
        | ResetFailed

    let private defaultReturnUrl = "/admin/features"

    let private localReturnUrl (value: string) =
        if
            not (String.IsNullOrWhiteSpace value)
            && value.StartsWith("/", StringComparison.Ordinal)
            && not (value.StartsWith("//", StringComparison.Ordinal))
            && not (value.StartsWith("/\\", StringComparison.Ordinal))
        then
            value
        else
            defaultReturnUrl

    let private queryReturnUrl (context: HttpContext) =
        context.Request.Query["returnUrl"] |> string |> localReturnUrl

    let private isHtmx (context: HttpContext) =
        context.Request.Headers.ContainsKey HxRequestHeader.Request

    let private noStore (context: HttpContext) =
        context.Response.Headers.CacheControl <- StringValues "no-store"
        context.Response.Headers.Pragma <- StringValues "no-cache"

    let private redirect (location: string) (context: HttpContext) =
        task {
            if isHtmx context then
                context.Response.StatusCode <- StatusCodes.Status204NoContent
                context.Response.Headers[HxResponseHeader.Redirect] <- StringValues location
            else
                context.Response.Redirect location
        }

    let private accountPage context title fragment =
        noStore context

        if isHtmx context then
            context.WriteHtmlView fragment
        else
            context.WriteHtmlView(Views.layout context title fragment)

    let private readForm (context: HttpContext) =
        context.Request.ReadFormAsync context.RequestAborted

    let private field name (form: IFormCollection) =
        match form.TryGetValue name with
        | true, value -> string value
        | false, _ -> ""

    let private normalizedCode (value: string) =
        value.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal)

    let private currentUser (context: HttpContext) =
        context.GetService<UserManager<ApplicationUser>>().GetUserAsync context.User

    let private enrollmentModel error (user: ApplicationUser) sharedKey uri recoveryCodesLeft =
        { UserName = user.UserName
          Email = user.Email
          SharedKey = sharedKey
          AuthenticatorUri = uri
          RecoveryCodesLeft = recoveryCodesLeft
          IsEnabled = user.TwoFactorEnabled
          Error = error }

    let private formattedKey (key: string) =
        key.ToLowerInvariant()
        |> Seq.chunkBySize 4
        |> Seq.map String
        |> String.concat " "

    let private authenticatorUri (user: ApplicationUser) key =
        let issuer = Uri.EscapeDataString "fsnix"
        let account = Uri.EscapeDataString user.Email
        $"otpauth://totp/{issuer}:{account}?secret={key}&issuer={issuer}&digits=6"

    let private requireIdentitySuccess error (result: IdentityResult) =
        result.Succeeded |> Result.requireTrue error

    let private createAuthenticatorKey (users: UserManager<ApplicationUser>) user =
        taskResult {
            let! reset = users.ResetAuthenticatorKeyAsync user
            do! requireIdentitySuccess CreateKeyFailed reset

            let! key = users.GetAuthenticatorKeyAsync user

            return!
                key
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Result.requireSome CreateKeyFailed
        }

    let private enableAuthenticator (users: UserManager<ApplicationUser>) user code =
        taskResult {
            let! valid = users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)
            do! valid |> Result.requireTrue InvalidCode

            let! enabled = users.SetTwoFactorEnabledAsync(user, true)
            do! requireIdentitySuccess EnableFailed enabled

            let! stamp = users.UpdateSecurityStampAsync user
            do! requireIdentitySuccess SecureSessionFailed stamp

            let! codes = users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10)
            return Seq.toList codes
        }

    let private resetAuthenticatorState (users: UserManager<ApplicationUser>) user =
        taskResult {
            let! disabled = users.SetTwoFactorEnabledAsync(user, false)
            do! requireIdentitySuccess ResetFailed disabled

            let! reset = users.ResetAuthenticatorKeyAsync user
            do! requireIdentitySuccess ResetFailed reset

            let! stamp = users.UpdateSecurityStampAsync user
            do! requireIdentitySuccess ResetFailed stamp
        }

    let requireAuthenticated (next: EndpointHandler) : EndpointHandler =
        fun context ->
            task {
                if context.User.Identity.IsAuthenticated then
                    return! next context
                else
                    let returnUrl = localReturnUrl context.Request.Path.Value
                    return! redirect $"/account/login?returnUrl={Uri.EscapeDataString returnUrl}" context
            }

    let requireMfa (next: EndpointHandler) : EndpointHandler =
        fun context ->
            task {
                if not context.User.Identity.IsAuthenticated then
                    let returnUrl = localReturnUrl context.Request.Path.Value
                    return! redirect $"/account/login?returnUrl={Uri.EscapeDataString returnUrl}" context
                else
                    let authorization = context.GetService<IAuthorizationService>()
                    let! result = authorization.AuthorizeAsync(context.User, null, "MfaAdmin")

                    if result.Succeeded then
                        return! next context
                    else
                        return! redirect "/account/2fa" context
            }

    let loginPage: EndpointHandler =
        fun context ->
            let returnUrl = queryReturnUrl context

            accountPage
                context
                "Sign in"
                (AccountViews.login
                    context
                    { Email = ""
                      ReturnUrl = returnUrl
                      Error = None })

    let login: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let email = field "email" form
                let password = field "password" form
                let returnUrl = field "returnUrl" form |> localReturnUrl

                let failure () =
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                    accountPage
                        context
                        "Sign in"
                        (AccountViews.login
                            context
                            { Email = email
                              ReturnUrl = returnUrl
                              Error = Some "Unable to sign in with those credentials." })

                if String.IsNullOrWhiteSpace email || String.IsNullOrWhiteSpace password then
                    return! failure ()
                else
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let signIn = context.GetService<SignInManager<ApplicationUser>>()
                    let! user = users.FindByEmailAsync email

                    if isNull user then
                        return! failure ()
                    else
                        let! result = signIn.PasswordSignInAsync(user, password, false, true)

                        if result.RequiresTwoFactor then
                            return! redirect $"/account/login/2fa?returnUrl={Uri.EscapeDataString returnUrl}" context
                        elif result.Succeeded then
                            return! redirect "/account/2fa" context
                        else
                            return! failure ()
            }

    let twoFactorPage: EndpointHandler =
        fun context ->
            task {
                noStore context
                let signIn = context.GetService<SignInManager<ApplicationUser>>()
                let! user = signIn.GetTwoFactorAuthenticationUserAsync()

                if isNull user then
                    return! redirect "/account/login" context
                else
                    let model =
                        { ReturnUrl = queryReturnUrl context
                          Error = None }

                    return! accountPage context "Authenticator code" (AccountViews.twoFactor context model)
            }

    let twoFactor: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let returnUrl = field "returnUrl" form |> localReturnUrl
                let code = field "code" form |> normalizedCode
                let signIn = context.GetService<SignInManager<ApplicationUser>>()
                let! result = signIn.TwoFactorAuthenticatorSignInAsync(code, false, false)

                if result.Succeeded then
                    return! redirect returnUrl context
                else
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                    return!
                        accountPage
                            context
                            "Authenticator code"
                            (AccountViews.twoFactor
                                context
                                { ReturnUrl = returnUrl
                                  Error = Some "The authenticator code is invalid." })
            }

    let recoveryLoginPage: EndpointHandler =
        fun context ->
            task {
                noStore context
                let signIn = context.GetService<SignInManager<ApplicationUser>>()
                let! user = signIn.GetTwoFactorAuthenticationUserAsync()

                if isNull user then
                    return! redirect "/account/login" context
                else
                    let model =
                        { ReturnUrl = queryReturnUrl context
                          Error = None }

                    return! accountPage context "Recovery code" (AccountViews.recoveryLogin context model)
            }

    let recoveryLogin: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let returnUrl = field "returnUrl" form |> localReturnUrl
                let code = field "code" form
                let signIn = context.GetService<SignInManager<ApplicationUser>>()
                let! result = signIn.TwoFactorRecoveryCodeSignInAsync code

                if result.Succeeded then
                    return! redirect returnUrl context
                else
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                    return!
                        accountPage
                            context
                            "Recovery code"
                            (AccountViews.recoveryLogin
                                context
                                { ReturnUrl = returnUrl
                                  Error = Some "The recovery code is invalid." })
            }

    let enrollmentPage: EndpointHandler =
        fun context ->
            task {
                let! user = currentUser context

                if isNull user then
                    return! redirect "/account/login" context
                else
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let! key = users.GetAuthenticatorKeyAsync user
                    let! remaining = users.CountRecoveryCodesAsync user

                    let keyOption = Option.ofObj key |> Option.filter (String.IsNullOrWhiteSpace >> not)

                    let model =
                        enrollmentModel
                            None
                            user
                            (keyOption |> Option.map formattedKey)
                            (keyOption |> Option.map (authenticatorUri user))
                            remaining

                    return! accountPage context "Two-factor authentication" (AccountViews.enrollment context model)
            }

    let createKey: EndpointHandler =
        fun context ->
            task {
                let! user = currentUser context
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! result = createAuthenticatorKey users user

                match result with
                | Error _ ->
                    context.Response.StatusCode <- StatusCodes.Status500InternalServerError

                    let model =
                        enrollmentModel (Some "The authenticator key could not be created.") user None None 0

                    return! accountPage context "Two-factor authentication" (AccountViews.enrollment context model)
                | Ok key ->
                    let model =
                        enrollmentModel None user (Some(formattedKey key)) (Some(authenticatorUri user key)) 0

                    return! accountPage context "Two-factor authentication" (AccountViews.enrollment context model)
            }

    let enableTwoFactor: EndpointHandler =
        fun context ->
            task {
                let! form = readForm context
                let code = field "code" form |> normalizedCode
                let! user = currentUser context
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! result = enableAuthenticator users user code

                match result with
                | Error InvalidCode ->
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity
                    let! key = users.GetAuthenticatorKeyAsync user

                    let model =
                        enrollmentModel
                            (Some "The authenticator code is invalid.")
                            user
                            (Option.ofObj key |> Option.map formattedKey)
                            (Option.ofObj key |> Option.map (authenticatorUri user))
                            0

                    return! accountPage context "Two-factor authentication" (AccountViews.enrollment context model)
                | Error failure ->
                    context.Response.StatusCode <- StatusCodes.Status500InternalServerError

                    let message =
                        match failure with
                        | SecureSessionFailed -> "The authenticated session could not be secured."
                        | _ -> "Two-factor authentication could not be enabled."

                    let model = enrollmentModel (Some message) user None None 0
                    return! accountPage context "Two-factor authentication" (AccountViews.enrollment context model)
                | Ok codes ->
                    let signIn = context.GetService<SignInManager<ApplicationUser>>()
                    do! signIn.SignInWithClaimsAsync(user, false, [ Claim("amr", "mfa") ])
                    return! accountPage context "Recovery codes" (AccountViews.recoveryCodes codes)
            }

    let regenerateRecoveryCodes: EndpointHandler =
        fun context ->
            task {
                let! user = currentUser context
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! codes = users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10)
                return! accountPage context "Recovery codes" (AccountViews.recoveryCodes (Seq.toList codes))
            }

    let resetAuthenticator: EndpointHandler =
        fun context ->
            task {
                let! user = currentUser context
                let users = context.GetService<UserManager<ApplicationUser>>()
                let! result = resetAuthenticatorState users user

                match result with
                | Ok() ->
                    let signIn = context.GetService<SignInManager<ApplicationUser>>()
                    do! signIn.SignInWithClaimsAsync(user, false, [ Claim("amr", "pwd") ])
                    return! redirect "/account/2fa" context
                | Error _ ->
                    context.Response.StatusCode <- StatusCodes.Status500InternalServerError

                    let model =
                        enrollmentModel (Some "The authenticator could not be reset.") user None None 0

                    return! accountPage context "Two-factor authentication" (AccountViews.enrollment context model)
            }

    let logout: EndpointHandler =
        fun context ->
            task {
                let signIn = context.GetService<SignInManager<ApplicationUser>>()
                do! signIn.SignOutAsync()
                return! redirect "/" context
            }
