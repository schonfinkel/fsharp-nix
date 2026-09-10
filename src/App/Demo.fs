namespace App

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.FeatureManagement
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module Views =
    let layout (context: HttpContext) (pageTitle: string) (content: HtmlElement) =
        html (lang = "en") {
            head () {
                meta (charset = "utf-8")
                meta (name = "viewport", content = "width=device-width, initial-scale=1")
                title () { $"{pageTitle} · Temporal Flags" }
                script (src = "/assets/htmx.min.js") { }
                script (src = "/assets/qrcode.min.js") { }
                link (rel = "stylesheet", href = "/style/app.css")
                script (src = "/js/authenticator.js", defer = true) { }
            }

            body () {
                nav () {
                    strong () { "Temporal Flags" }

                    div (class' = "account") {
                        a (href = "/") { "Demo" }
                        a (href = "/admin/features") { "Admin" }

                        if context.User.Identity.IsAuthenticated then
                            span (class' = "muted") { context.User.Identity.Name }
                            a (href = "/account/2fa") { "2FA" }

                            form (action = "/account/logout", method = "post") {
                                context.GetAntiforgeryInput()
                                button (type' = "submit") { "Sign out" }
                            }
                        else
                            a (href = "/account/login") { "Sign in" }
                    }
                }

                main () { content }
            }
        }

    let demoFragment flag enabled =
        let name = FeatureFlag.persistedName flag
        let stateClass = if enabled then "demo enabled" else "demo disabled"

        section (id = $"demo-{name}", class' = stateClass) {
            span (class' = "badge") { if enabled then "ENABLED" else "DISABLED" }

            match flag, enabled with
            | NewDashboard, true ->
                h2 () { "New dashboard" }
                p () { "The redesigned analytics experience is active." }
            | NewDashboard, false ->
                h2 () { "Classic dashboard" }
                p () { "The stable dashboard remains active." }
            | BetaCheckout, true ->
                h2 () { "Express checkout" }
                p () { "Customers see the new one-step checkout." }
            | BetaCheckout, false ->
                h2 () { "Standard checkout" }
                p () { "Customers follow the standard checkout flow." }

            button(type' = "button")
                .hxGet(
                    if flag = NewDashboard then
                        "/demo/dashboard"
                    else
                        "/demo/checkout"
                )
                .hxTarget($"#demo-{name}")
                .hxSwap ("outerHTML") {
                "Refresh from PostgreSQL"
            }
        }

[<RequireQualifiedAccess>]
module Demo =
    let private isEnabled flag (context: HttpContext) =
        context.GetService<IFeatureManager>().IsEnabledAsync(FeatureFlag.persistedName flag)

    let fragment flag : EndpointHandler =
        fun context ->
            task {
                let! enabled = isEnabled flag context
                return! context.WriteHtmlView(Views.demoFragment flag enabled)
            }

    let index: EndpointHandler =
        fun context ->
            task {
                let! dashboard = isEnabled NewDashboard context
                let! checkout = isEnabled BetaCheckout context

                let content =
                    Fragment() {
                        p (class' = "badge") { "POSTGRESQL 18 · HTMX 4 · F#" }
                        h1 () { "Features that arrive on time." }

                        p (class' = "lede") {
                            "This demo evaluates temporal feature definitions from PostgreSQL. Use the local admin to change a flag now or schedule its next interval."
                        }

                        div (class' = "grid") {
                            Views.demoFragment NewDashboard dashboard
                            Views.demoFragment BetaCheckout checkout
                        }
                    }

                return! context.WriteHtmlView(Views.layout context "Demo" content)
            }
