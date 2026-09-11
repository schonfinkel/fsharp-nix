namespace App

open System
open App.Domain
open App.Views
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

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

    let private model (store: IFeatureFlagStore) form flag cancellationToken =
        task {
            let! schedule = store.GetSchedule(flag, cancellationToken)
            return schedule |> Option.map (fun value -> { Schedule = value; Form = form })
        }

    let private writeCard status form flag (context: HttpContext) =
        task {
            Web.noStore context
            context.Response.StatusCode <- status
            let store = context.GetService<IFeatureFlagStore>()
            let now = context.GetService<TimeProvider>().GetUtcNow()
            let! found = model store form flag context.RequestAborted

            match found with
            | Some value -> return! context.WriteHtmlView(AdminViews.card context now value)
            | None -> return! context.WriteHtmlView(p (class' = "error") { "Feature flag not found." })
        }

    let index: EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                Web.varyHtmx context
                let store = context.GetService<IFeatureFlagStore>()
                let now = context.GetService<TimeProvider>().GetUtcNow()

                let! models =
                    FeatureFlag.all
                    |> List.map (fun flag -> model store AdminViews.emptyForm flag context.RequestAborted)
                    |> System.Threading.Tasks.Task.WhenAll

                let existing = models |> Array.choose id |> Array.toList

                if Web.isHtmx context then
                    return!
                        context.WriteHtmlView(
                            Fragment() {
                                for item in existing do
                                    AdminViews.card context now item
                            }
                        )
                else
                    return! context.WriteHtmlView(AdminViews.page context now existing)
            }

    let featureCard: EndpointHandler =
        fun context ->
            task {
                match context.TryGetRouteValue("name") |> Option.bind FeatureFlag.tryParse with
                | None ->
                    context.Response.StatusCode <- StatusCodes.Status404NotFound
                    return! context.WriteHtmlView(p (class' = "error") { "Feature flag not found." })
                | Some flag -> return! writeCard StatusCodes.Status200OK AdminViews.emptyForm flag context
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

                    let formValue name =
                        match form.TryGetValue name with
                        | true, value -> string value
                        | false, _ -> ""

                    let localEffectiveAt = formValue "effectiveAtLocal"
                    let effectiveAt = formValue "effectiveAt"

                    let timestampToParse =
                        if
                            String.IsNullOrWhiteSpace effectiveAt
                            && not (String.IsNullOrWhiteSpace localEffectiveAt)
                        then
                            localEffectiveAt
                        else
                            effectiveAt

                    let displayedEffectiveAt =
                        if String.IsNullOrWhiteSpace localEffectiveAt then
                            effectiveAt
                        else
                            localEffectiveAt

                    let submitted error =
                        { Enabled = enabled
                          EffectiveAt = displayedEffectiveAt
                          Error = Some error }

                    let now = context.GetService<TimeProvider>().GetUtcNow()

                    match Scheduling.parseEffectiveTime now (Some timestampToParse) with
                    | Error EffectiveTimeIsInPast ->
                        return!
                            writeCard
                                StatusCodes.Status422UnprocessableEntity
                                (submitted "The effective time cannot be in the past.")
                                flag
                                context
                    | Error(InvalidEffectiveTime _) ->
                        return!
                            writeCard
                                StatusCodes.Status422UnprocessableEntity
                                (submitted "Enter a valid local date and time.")
                                flag
                                context
                    | Ok effectiveTime ->
                        let change =
                            { Flag = flag
                              State = FeatureState.ofBool enabled
                              EffectiveTime = effectiveTime }

                        let store = context.GetService<IFeatureFlagStore>()
                        let! result = Workflow.schedule store now change context.RequestAborted

                        match result with
                        | Ok Changed ->
                            context.Response.Headers[HxResponseHeader.Trigger] <- "feature-change"
                            return! writeCard StatusCodes.Status200OK AdminViews.emptyForm flag context
                        | Ok Unchanged -> return! writeCard StatusCodes.Status200OK AdminViews.emptyForm flag context
                        | Error FlagNotFound ->
                            context.Response.StatusCode <- StatusCodes.Status404NotFound
                            return! context.WriteHtmlView(p (class' = "error") { "Feature flag not found." })
                        | Error Conflict ->
                            return!
                                writeCard
                                    StatusCodes.Status409Conflict
                                    (submitted "That time is a gap or an existing schedule boundary.")
                                    flag
                                    context
            }
