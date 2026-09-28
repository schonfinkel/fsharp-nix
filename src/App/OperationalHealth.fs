namespace App

open App.Database
open App.Views
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Npgsql
open Oxpecker

[<RequireQualifiedAccess>]
module OperationalHealthEndpoints =
    let private phaseCode =
        function
        | RuntimePhase.Starting -> "starting"
        | RuntimePhase.Healthy -> "healthy"
        | RuntimePhase.Failed -> "failed"
        | RuntimePhase.Stopped -> "stopped"

    let private failureCode =
        function
        | RuntimeFailure.InvalidChart -> "invalid-chart"
        | RuntimeFailure.StartupRefused -> "startup-refused"
        | RuntimeFailure.StartupFailed -> "startup-failed"
        | RuntimeFailure.PassFailed -> "pass-failed"

    let private outbox name (snapshot: OutboxHealthSnapshot) : OutboxHealthModel =
        { Name = name
          Pending = snapshot.Pending
          Claimable = snapshot.Claimable
          Leased = snapshot.Leased
          Sent = snapshot.Sent
          Dead = snapshot.Dead
          OldestPendingAt = snapshot.OldestPendingAt
          MaximumPendingAttempts = snapshot.MaximumPendingAttempts }

    let index: EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let runtime = context.RequestServices.GetRequiredService<RuntimeHealth>()
                let! database = OperationalHealth.capture dataSource context.RequestAborted

                let runtimeModels =
                    runtime.Snapshot()
                    |> Map.toList
                    |> List.sortBy (fst >> RuntimeComponent.code)
                    |> List.map (fun (_, observation) ->
                        { Component = RuntimeComponent.code observation.Component
                          Phase = phaseCode observation.Phase
                          LastSuccessAt = observation.LastSuccessAt
                          ConsecutiveFailures = observation.ConsecutiveFailures
                          Failure = observation.Failure |> Option.map failureCode })

                let model: OperationalHealthModel =
                    { CapturedAt = database.CapturedAt
                      Runtime = runtimeModels
                      Outboxes =
                        [ outbox "integration" database.IntegrationOutbox
                          outbox "account-email" database.EmailOutbox ]
                      FlowRequests =
                        database.FlowRequests
                        |> List.map (fun row ->
                            { Kind = row.Kind
                              Status = row.Status
                              Count = row.Count
                              ExpiredRequested = row.ExpiredRequested
                              OldestCreatedAt = row.OldestCreatedAt })
                      Deadlines =
                        database.Deadlines
                        |> List.map (fun row ->
                            { Kind = row.Kind
                              Status = row.Status
                              Count = row.Count
                              Due = row.Due
                              Leased = row.Leased
                              EarliestPendingDeadline = row.EarliestPendingDeadline }) }

                return! context.WriteHtmlView(OperationalHealthViews.page context model)
            }
