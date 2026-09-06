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

                style () {
                    raw
                        """
                        :root { color-scheme: light dark; font-family: Inter, ui-sans-serif, system-ui, sans-serif; }
                        body { margin: 0; background: #0b1020; color: #e7eaf0; }
                        nav, main { width: min(1050px, calc(100% - 2rem)); margin: auto; }
                        nav { display: flex; align-items: center; justify-content: space-between; padding: 1.25rem 0; }
                        nav a { color: #9cc3ff; text-decoration: none; margin-left: 1rem; }
                        nav .account { display: flex; align-items: center; gap: .75rem; }
                        nav form { display: inline; margin: 0; }
                        nav form button { background: transparent; color: #9cc3ff; padding: 0; }
                        main { padding: 2rem 0 4rem; }
                        h1 { font-size: clamp(2rem, 6vw, 4rem); margin: .25rem 0; }
                        h2, h3 { margin-top: 0; }
                        .lede { color: #acb8ce; max-width: 62ch; font-size: 1.1rem; }
                        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(290px, 1fr)); gap: 1rem; margin-top: 2rem; }
                        .card, .demo { border: 1px solid #263250; background: #111a30; border-radius: 16px; padding: 1.25rem; }
                        .enabled { border-color: #29b979; }
                        .disabled { border-color: #62708c; }
                        .badge { display: inline-block; border-radius: 999px; padding: .2rem .65rem; font-size: .78rem; font-weight: 700; }
                        .enabled .badge { background: #123f31; color: #67e8ae; }
                        .disabled .badge { background: #283145; color: #c2cad8; }
                        button { cursor: pointer; border: 0; border-radius: 9px; padding: .65rem 1rem; color: white; background: #3867db; }
                        button.danger { background: #9e3348; }
                        input { border: 1px solid #394867; border-radius: 8px; padding: .55rem; background: #0b1020; color: inherit; }
                        label { display: block; color: #c3ccdc; margin: .7rem 0 .3rem; }
                        table { width: 100%; border-collapse: collapse; font-size: .9rem; }
                        th, td { text-align: left; border-bottom: 1px solid #283652; padding: .5rem .25rem; }
                        .error { margin: .8rem 0; padding: .6rem; background: #4a1d28; border-radius: 8px; color: #ffc5cf; }
                        .success { color: #67e8ae; }
                        .muted { color: #96a2b9; }
                        code { background: #202b43; padding: .15rem .35rem; border-radius: 5px; }
                        .account-card { max-width: 560px; margin: 0 auto; }
                        .account-card input:not([type=hidden]) { box-sizing: border-box; width: 100%; }
                        .actions { margin-top: 1rem; }
                        .recovery-codes { columns: 2; padding-left: 1.25rem; }
                        #qr-code { width: 192px; min-height: 192px; padding: .75rem; background: white; border-radius: 8px; }
                        """
                }
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
