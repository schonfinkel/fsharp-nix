namespace App.Views

open System
open Microsoft.AspNetCore.Http
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module OperationalHealthViews =
    let private timestamp =
        function
        | Some(value: DateTimeOffset) -> value.ToString("u")
        | None -> "none"

    let page (context: HttpContext) (model: OperationalHealthModel) =
        let hasDeadRows = model.Outboxes |> List.exists (fun outbox -> outbox.Dead > 0L)

        let content =
            Fragment() {
                p (class' = "badge") { "MFA-PROTECTED OPERATIONS" }
                h1 () { "Runtime and delivery health" }

                p (class' = "muted") {
                    $"Captured {model.CapturedAt:u}. Values are aggregate and contain no payload data."
                }

                if hasDeadRows then
                    p (class' = "error") { "One or more outboxes contain dead rows requiring reconciliation." }

                h2 () { "Runtime" }

                table () {
                    thead () {
                        tr () {
                            th () { "Component" }
                            th () { "Phase" }
                            th () { "Last success" }
                            th () { "Failures" }
                            th () { "Classification" }
                        }
                    }

                    tbody () {
                        for item in model.Runtime do
                            tr () {
                                td () { item.Component }
                                td () { item.Phase }
                                td () { timestamp item.LastSuccessAt }
                                td () { string item.ConsecutiveFailures }
                                td () { defaultArg item.Failure "none" }
                            }
                    }
                }

                h2 () { "Outboxes" }

                table () {
                    thead () {
                        tr () {
                            th () { "Queue" }
                            th () { "Pending" }
                            th () { "Claimable" }
                            th () { "Leased" }
                            th () { "Sent" }
                            th () { "Dead" }
                            th () { "Oldest pending" }
                            th () { "Max attempts" }
                        }
                    }

                    tbody () {
                        for item in model.Outboxes do
                            tr () {
                                td () { item.Name }
                                td () { string item.Pending }
                                td () { string item.Claimable }
                                td () { string item.Leased }
                                td () { string item.Sent }
                                td () { string item.Dead }
                                td () { timestamp item.OldestPendingAt }
                                td () { item.MaximumPendingAttempts |> Option.map string |> Option.defaultValue "none" }
                            }
                    }
                }

                h2 () { "Account flow requests" }

                table () {
                    thead () {
                        tr () {
                            th () { "Kind" }
                            th () { "Status" }
                            th () { "Count" }
                            th () { "Expired active" }
                            th () { "Oldest" }
                        }
                    }

                    tbody () {
                        for item in model.FlowRequests do
                            tr () {
                                td () { item.Kind }
                                td () { item.Status }
                                td () { string item.Count }
                                td () { string item.ExpiredRequested }
                                td () { timestamp (Some item.OldestCreatedAt) }
                            }
                    }
                }

                h2 () { "Deadlines" }

                table () {
                    thead () {
                        tr () {
                            th () { "Kind" }
                            th () { "Status" }
                            th () { "Count" }
                            th () { "Due" }
                            th () { "Leased" }
                            th () { "Earliest pending" }
                        }
                    }

                    tbody () {
                        for item in model.Deadlines do
                            tr () {
                                td () { item.Kind }
                                td () { item.Status }
                                td () { string item.Count }
                                td () { string item.Due }
                                td () { string item.Leased }
                                td () { timestamp item.EarliestPendingDeadline }
                            }
                    }
                }

                h2 () { "Gateway reconciliation" }

                table () {
                    thead () {
                        tr () {
                            th () { "Unknown outcomes" }
                            th () { "Due for a check" }
                            th () { "Parked for review" }
                            th () { "Most checks" }
                        }
                    }

                    tbody () {
                        tr () {
                            td () { string model.Reconciliation.Unknown }
                            td () { string model.Reconciliation.Due }
                            td () { string model.Reconciliation.Parked }

                            td () {
                                model.Reconciliation.MaximumChecks
                                |> Option.map string
                                |> Option.defaultValue "-"
                            }
                        }
                    }
                }
            }

        SharedViews.layout context "Operations" content
