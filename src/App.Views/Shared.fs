namespace App.Views

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.Htmx.Extensions
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module SharedViews =
    let layout (context: HttpContext) (pageTitle: string) (content: HtmlElement) =
        html (lang = "en") {
            head () {
                meta (charset = "utf-8")
                meta (name = "viewport", content = "width=device-width, initial-scale=1")
                title () { $"{pageTitle} · Temporal Flags" }
                script (src = "/assets/htmx.min.js") { }
                script (src = "/assets/hx-sse.min.js") { }
                script (src = "/assets/qrcode.min.js") { }
                link (rel = "stylesheet", href = "/style/app.css")
                script (src = "/js/authenticator.js", defer = true) { }
                script (src = "/js/scheduling.js", defer = true) { }
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

    let featureEventStream = div().hxSseConnect("/events/features").hxSwap ("none") { }
