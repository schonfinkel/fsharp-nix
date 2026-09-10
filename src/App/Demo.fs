namespace App

open App.Domain
open App.Views
open Microsoft.AspNetCore.Http
open Microsoft.FeatureManagement
open Oxpecker
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module Demo =
    let private isEnabled flag (context: HttpContext) =
        context.GetService<IFeatureManager>().IsEnabledAsync(FeatureFlag.persistedName flag)

    let fragment flag : EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                let! enabled = isEnabled flag context
                return! context.WriteHtmlView(DemoViews.fragment flag enabled)
            }

    let index: EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                let dashboardTask = isEnabled NewDashboard context
                let checkoutTask = isEnabled BetaCheckout context
                let! dashboard = dashboardTask
                let! checkout = checkoutTask

                let content =
                    Fragment() {
                        SharedViews.featureEventStream
                        p (class' = "badge") { "POSTGRESQL 18 · HTMX 4 · F#" }
                        h1 () { "Features that arrive on time." }

                        p (class' = "lede") {
                            "This demo evaluates temporal feature definitions from PostgreSQL. Use the local admin to change a flag now or schedule its next interval."
                        }

                        div (class' = "grid") {
                            DemoViews.fragment NewDashboard dashboard
                            DemoViews.fragment BetaCheckout checkout
                        }
                    }

                return! context.WriteHtmlView(SharedViews.layout context "Demo" content)
            }
