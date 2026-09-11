namespace App.Views

open App.Domain
open Oxpecker.Htmx
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module DemoViews =
    let private demoPath =
        function
        | NewDashboard -> "/demo/dashboard"
        | BetaCheckout -> "/demo/checkout"

    let fragment flag enabled =
        let name = FeatureFlag.persistedName flag
        let stateClass = if enabled then "demo enabled" else "demo disabled"

        section(id = $"demo-{name}", class' = stateClass)
            .hxGet(demoPath flag)
            .hxTrigger("feature-change from:body")
            .hxTarget("this")
            .hxSwap ("outerMorph") {
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

            button(type' = "button").hxGet(demoPath flag).hxTarget($"#demo-{name}").hxSwap ("outerHTML") {
                "Refresh from PostgreSQL"
            }
        }
