namespace App

open System
open System.Globalization
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

type AdminFeatureModel =
    { Current: CurrentDefinition
      History: HistoryEntry list }

[<RequireQualifiedAccess>]
module AdminViews =
    let private timestamp (value: DateTimeOffset option) =
        match value with
        | Some value -> value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
        | None -> "∞"

    let private state (value: bool) = if value then "Enabled" else "Disabled"

    let card (context: HttpContext) (model: AdminFeatureModel) (error: string option) =
        let name = FeatureFlag.persistedName model.Current.Flag

        let stateClass =
            if model.Current.Enabled then
                "card enabled"
            else
                "card disabled"

        let future =
            model.History
            |> List.filter (fun item -> item.ValidFrom > DateTimeOffset.UtcNow)

        article (id = $"feature-{name}", class' = stateClass) {
            span (class' = "badge") { state model.Current.Enabled }
            h2 () { name }
            p (class' = "muted") { $"Current interval began {timestamp (Some model.Current.ValidFrom)}." }

            for message in Option.toList error do
                p (class' = "error") { message }

            form(action = $"/admin/features/{name}/schedule", method = "post")
                .hxPost($"/admin/features/{name}/schedule")
                .hxTarget($"#feature-{name}")
                .hxSwap("outerHTML")
                .hxStatus("422", $"swap:outerHTML target:#feature-{name}")
                .hxStatus ("409", $"swap:outerHTML target:#feature-{name}") {
                context.GetAntiforgeryInput()
                label (for' = $"enabled-{name}") { "New state" }
                input (type' = "checkbox", id = $"enabled-{name}", name = "enabled", value = "true")
                label (for' = $"effective-{name}") { "Effective at (UTC; blank means now)" }
                input (type' = "datetime-local", id = $"effective-{name}", name = "effectiveAt")
                div (class' = "schedule-actions") { button (type' = "submit") { "Schedule change" } }
            }

            h3 (class' = "section-heading") { "Future schedule" }

            if List.isEmpty future then
                p (class' = "muted") { "No future changes." }
            else
                ul () {
                    for item in future do
                        li () { $"{timestamp (Some item.ValidFrom)} — {state item.Enabled}" }
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
                    for item in model.History do
                        tr () {
                            td () { state item.Enabled }
                            td () { timestamp (Some item.ValidFrom) }
                            td () { timestamp item.ValidTo }
                        }
                }
            }
        }

    let page (context: HttpContext) (models: AdminFeatureModel list) =
        let content =
            Fragment() {
                p (class' = "badge") { "MFA-PROTECTED ADMIN" }
                h1 () { "Feature schedule" }

                p (class' = "lede") {
                    "Each update creates a new closed-open interval. Existing history is append-only and future boundaries are preserved."
                }

                div (class' = "grid") {
                    for model in models do
                        card context model None
                }
            }

        Views.layout context "Admin" content

[<RequireQualifiedAccess>]
module Admin =
    let requireValidAntiforgery (next: EndpointHandler) : EndpointHandler =
        fun context ->
            task {
                let validation = context.Features.Get<IAntiforgeryValidationFeature>()

                if not (isNull validation) && not validation.IsValid then
                    context.Response.StatusCode <- StatusCodes.Status400BadRequest

                    return!
                        context.WriteHtmlView(p (class' = "error") { "The antiforgery token is invalid or missing." })
                else
                    return! next context
            }

    let private isHtmx (context: HttpContext) =
        context.Request.Headers.ContainsKey HxRequestHeader.Request

    let private model (store: IFeatureFlagStore) flag cancellationToken =
        task {
            let! current = store.GetCurrent(flag, cancellationToken)
            let! history = store.GetHistory(flag, cancellationToken)

            return current |> Option.map (fun value -> { Current = value; History = history })
        }

    let private writeCard status error flag (context: HttpContext) =
        task {
            context.Response.StatusCode <- status
            let store = context.GetService<IFeatureFlagStore>()
            let! found = model store flag context.RequestAborted

            match found with
            | Some value -> return! context.WriteHtmlView(AdminViews.card context value error)
            | None -> return! context.WriteHtmlView(p (class' = "error") { "Feature flag not found." })
        }

    let index: EndpointHandler =
        fun context ->
            task {
                let store = context.GetService<IFeatureFlagStore>()

                let! models =
                    FeatureFlag.all
                    |> List.map (fun flag -> model store flag context.RequestAborted)
                    |> System.Threading.Tasks.Task.WhenAll

                let existing = models |> Array.choose id |> Array.toList

                if isHtmx context then
                    return!
                        context.WriteHtmlView(
                            Fragment() {
                                for item in existing do
                                    AdminViews.card context item None
                            }
                        )
                else
                    return! context.WriteHtmlView(AdminViews.page context existing)
            }

    let featureCard: EndpointHandler =
        fun context ->
            task {
                match context.TryGetRouteValue("name") |> Option.bind FeatureFlag.tryParse with
                | None ->
                    context.Response.StatusCode <- StatusCodes.Status404NotFound
                    return! context.WriteHtmlView(p (class' = "error") { "Feature flag not found." })
                | Some flag -> return! writeCard StatusCodes.Status200OK None flag context
            }

    let schedule: EndpointHandler =
        fun context ->
            task {
                match context.TryGetRouteValue("name") |> Option.bind FeatureFlag.tryParse with
                | None ->
                    context.Response.StatusCode <- StatusCodes.Status404NotFound
                    return! context.WriteHtmlView(p (class' = "error") { "Feature flag not found." })
                | Some flag ->
                    let! form = context.Request.ReadFormAsync context.RequestAborted
                    let enabled = form.ContainsKey "enabled"

                    let effectiveAt =
                        match form.TryGetValue "effectiveAt" with
                        | true, value when not (String.IsNullOrWhiteSpace(string value)) -> Some(string value)
                        | _ -> None

                    let now = context.GetService<TimeProvider>().GetUtcNow()

                    match Scheduling.parseEffectiveTime now effectiveAt with
                    | Error EffectiveTimeIsInPast ->
                        return!
                            writeCard
                                StatusCodes.Status422UnprocessableEntity
                                (Some "The effective time cannot be in the past.")
                                flag
                                context
                    | Error(InvalidEffectiveTime _) ->
                        return!
                            writeCard
                                StatusCodes.Status422UnprocessableEntity
                                (Some "Enter a valid UTC date and time.")
                                flag
                                context
                    | Error(UnknownFlag _) ->
                        context.Response.StatusCode <- StatusCodes.Status404NotFound
                        return! context.WriteHtmlView(p (class' = "error") { "Feature flag not found." })
                    | Ok effectiveTime ->
                        let change =
                            { Flag = flag
                              Enabled = enabled
                              EffectiveTime = effectiveTime }

                        let store = context.GetService<IFeatureFlagStore>()
                        let! result = Workflow.schedule store now change context.RequestAborted

                        match result with
                        | Ok() ->
                            context.Response.Headers[HxResponseHeader.Trigger] <- "feature-change"
                            return! writeCard StatusCodes.Status200OK None flag context
                        | Error FlagNotFound ->
                            context.Response.StatusCode <- StatusCodes.Status404NotFound
                            return! context.WriteHtmlView(p (class' = "error") { "Feature flag not found." })
                        | Error Conflict ->
                            return!
                                writeCard
                                    StatusCodes.Status409Conflict
                                    (Some "That time is a gap or an existing schedule boundary.")
                                    flag
                                    context
                        | Error(DatabaseFailure _) ->
                            context.Response.StatusCode <- StatusCodes.Status500InternalServerError
                            return! context.WriteHtmlView(p (class' = "error") { "The change could not be saved." })
                        | Error(ValidationFailure _) ->
                            return!
                                writeCard
                                    StatusCodes.Status422UnprocessableEntity
                                    (Some "The submitted schedule is invalid.")
                                    flag
                                    context
            }
