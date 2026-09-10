namespace App.Views

open System
open System.Globalization
open App.Domain
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module AdminViews =
    let private timestamp (value: DateTimeOffset option) =
        match value with
        | Some value -> value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
        | None -> "∞"

    let private state =
        function
        | Enabled -> "Enabled"
        | Disabled -> "Disabled"

    let emptyForm =
        { Enabled = false
          EffectiveAt = ""
          Error = None }

    let card (context: HttpContext) (now: DateTimeOffset) (model: AdminFeatureModel) =
        let current = model.Schedule.Current
        let name = FeatureFlag.persistedName current.Flag
        let errorId = $"schedule-error-{name}"

        let stateClass =
            if current.State = Enabled then
                "card enabled"
            else
                "card disabled"

        let future =
            model.Schedule.History |> List.filter (fun item -> item.ValidFrom > now)

        let enabledInput =
            input (type' = "checkbox", id = $"enabled-{name}", name = "enabled", value = "true")

        let enabledInput =
            if model.Form.Enabled then
                enabledInput.attr ("checked", "checked")
            else
                enabledInput

        let effectiveInput =
            input (
                type' = "datetime-local",
                id = $"effective-{name}",
                name = "effectiveAtLocal",
                value = model.Form.EffectiveAt
            )

        let effectiveInput = effectiveInput.attr ("data-effective-local", "")

        let effectiveInstantInput =
            input (type' = "hidden", id = $"effective-utc-{name}", name = "effectiveAt", value = "")

        let effectiveInstantInput = effectiveInstantInput.attr ("data-effective-utc", "")

        let effectiveInput =
            match model.Form.Error with
            | Some _ -> effectiveInput.attr("aria-invalid", "true").attr ("aria-describedby", errorId)
            | None -> effectiveInput

        article(id = $"feature-{name}", class' = stateClass)
            .hxGet($"/admin/features/{name}")
            .hxTrigger("feature-change from:body")
            .hxTarget("this")
            .hxSwap ("outerMorph") {
            span (class' = "badge") { state current.State }
            h2 () { name }
            p (class' = "muted") { $"Current interval began {timestamp (Some current.ValidFrom)}." }

            for message in Option.toList model.Form.Error do
                p (id = errorId, class' = "error") { message }

            form(action = $"/admin/features/{name}/schedule", method = "post")
                .attr("data-feature-schedule", "")
                .hxPost($"/admin/features/{name}/schedule")
                .hxTarget($"#feature-{name}")
                .hxSwap("outerHTML")
                .hxStatus("422", $"swap:outerHTML target:#feature-{name}")
                .hxStatus("409", $"swap:outerHTML target:#feature-{name}")
                .hxDisable("find button")
                .hxIndicator ($"#schedule-indicator-{name}") {
                context.GetAntiforgeryInput()
                label (for' = $"enabled-{name}") { "New state" }
                enabledInput
                label (for' = $"effective-{name}") { "Effective at (your local time; blank means now)" }
                effectiveInput
                effectiveInstantInput

                div (class' = "schedule-actions") {
                    button (type' = "submit") { "Schedule change" }

                    span(id = $"schedule-indicator-{name}", class' = "htmx-indicator").attr ("role", "status") {
                        "Saving…"
                    }
                }
            }

            h3 (class' = "section-heading") { "Future schedule" }

            if List.isEmpty future then
                p (class' = "muted") { "No future changes." }
            else
                ul () {
                    for item in future do
                        li () { $"{timestamp (Some item.ValidFrom)} — {state item.State}" }
                }

            h3 (class' = "section-heading") { "History" }

            table () {
                thead () {
                    tr () {
                        th () { "State" }
                        th () { "From" }
                        th () { "Until" }
                    }
                }

                tbody () {
                    for item in model.Schedule.History do
                        tr () {
                            td () { state item.State }
                            td () { timestamp (Some item.ValidFrom) }
                            td () { timestamp item.ValidTo }
                        }
                }
            }
        }

    let page (context: HttpContext) now (models: AdminFeatureModel list) =
        let content =
            Fragment() {
                SharedViews.featureEventStream
                p (class' = "badge") { "MFA-PROTECTED ADMIN" }
                h1 () { "Feature schedule" }

                p (class' = "lede") {
                    "Each update creates a new closed-open interval. Existing history is append-only and future boundaries are preserved."
                }

                div (class' = "grid") {
                    for model in models do
                        card context now model
                }
            }

        SharedViews.layout context "Admin" content
