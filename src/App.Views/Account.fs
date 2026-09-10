namespace App.Views

open System
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module AccountViews =
    let private antiforgeryForm (context: HttpContext) (action: string) (content: HtmlElement) =
        form(action = action, method = "post")
            .hxPost(action)
            .hxTarget("#account-panel")
            .hxSwap("outerHTML")
            .hxDisable("find button")
            .hxIndicator ("find .htmx-indicator") {
            context.GetAntiforgeryInput()
            content
            span(class' = "htmx-indicator").attr ("role", "status") { "Working…" }
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

                for key in Option.toList model.DevelopmentKey do
                    div (class' = "development-authenticator-key") {
                        p (class' = "muted") { "Development authenticator key:" }
                        p () { code () { key } }
                        p (class' = "muted") { "Run make totp and paste this key to generate a test code." }
                    }

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
