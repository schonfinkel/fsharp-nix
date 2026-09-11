namespace App

open System
open System.Security.Claims
open App.Database
open App.Views
open FsToolkit.ErrorHandling
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Hosting
open Oxpecker

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

    let private accountPage context title fragment =
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

    let private developmentAuthenticatorKey
        (context: HttpContext)
        (users: UserManager<ApplicationUser>)
        (user: ApplicationUser)
        =
        task {
            let environment = context.GetService<IHostEnvironment>()

            if environment.IsDevelopment() && not (isNull user) then
                let! key = users.GetAuthenticatorKeyAsync user

                return
                    key
                    |> Option.ofObj
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.map formattedKey
            else
                return None
        }

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
                    return! Web.redirect $"/account/login?returnUrl={Uri.EscapeDataString returnUrl}" context
            }

    let requireMfa (next: EndpointHandler) : EndpointHandler =
        fun context ->
            task {
                if not context.User.Identity.IsAuthenticated then
                    let returnUrl = localReturnUrl context.Request.Path.Value
                    return! Web.redirect $"/account/login?returnUrl={Uri.EscapeDataString returnUrl}" context
                else
                    let authorization = context.GetService<IAuthorizationService>()
                    let! result = authorization.AuthorizeAsync(context.User, null, "MfaAdmin")

                    if result.Succeeded then
                        return! next context
                    else
                        return! Web.redirect "/account/2fa" context
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
                            return!
                                Web.redirect $"/account/login/2fa?returnUrl={Uri.EscapeDataString returnUrl}" context
                        elif result.Succeeded then
                            return! Web.redirect "/account/2fa" context
                        else
                            return! failure ()
            }

    let twoFactorPage: EndpointHandler =
        fun context ->
            task {
                let signIn = context.GetService<SignInManager<ApplicationUser>>()
                let! user = signIn.GetTwoFactorAuthenticationUserAsync()

                if isNull user then
                    return! Web.redirect "/account/login" context
                else
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let! developmentKey = developmentAuthenticatorKey context users user

                    return!
                        accountPage
                            context
                            "Authenticator code"
                            (AccountViews.twoFactor
                                context
                                { ReturnUrl = queryReturnUrl context
                                  DevelopmentKey = developmentKey
                                  Error = None })
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
                    return! Web.redirect returnUrl context
                else
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity
                    let! user = signIn.GetTwoFactorAuthenticationUserAsync()
                    let users = context.GetService<UserManager<ApplicationUser>>()
                    let! developmentKey = developmentAuthenticatorKey context users user

                    return!
                        accountPage
                            context
                            "Authenticator code"
                            (AccountViews.twoFactor
                                context
                                { ReturnUrl = returnUrl
                                  DevelopmentKey = developmentKey
                                  Error = Some "The authenticator code is invalid." })
            }

    let recoveryLoginPage: EndpointHandler =
        fun context ->
            task {
                let signIn = context.GetService<SignInManager<ApplicationUser>>()
                let! user = signIn.GetTwoFactorAuthenticationUserAsync()

                if isNull user then
                    return! Web.redirect "/account/login" context
                else
                    return!
                        accountPage
                            context
                            "Recovery code"
                            (AccountViews.recoveryLogin
                                context
                                { ReturnUrl = queryReturnUrl context
                                  DevelopmentKey = None
                                  Error = None })
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
                    return! Web.redirect returnUrl context
                else
                    context.Response.StatusCode <- StatusCodes.Status422UnprocessableEntity

                    return!
                        accountPage
                            context
                            "Recovery code"
                            (AccountViews.recoveryLogin
                                context
                                { ReturnUrl = returnUrl
                                  DevelopmentKey = None
                                  Error = Some "The recovery code is invalid." })
            }

    let enrollmentPage: EndpointHandler =
        fun context ->
            task {
                let! user = currentUser context

                if isNull user then
                    return! Web.redirect "/account/login" context
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
                        | InvalidCode
                        | CreateKeyFailed
                        | EnableFailed
                        | ResetFailed -> "Two-factor authentication could not be enabled."

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
                    return! Web.redirect "/account/2fa" context
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
                return! Web.redirect "/" context
            }
