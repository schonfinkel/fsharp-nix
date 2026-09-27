namespace App.Views

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module ProbeViews =

    let page (context: HttpContext) (model: ProbeModel) =
        let content =
            Fragment() {
                h1 () { "Automata probe" }

                p () { $"phase: {model.Phase} · runs: {model.Runs} · epoch: {model.Epoch}" }

                p () { $"command: {model.CommandResult} · receipts: {model.Receipts} · outbox: {model.Outbox}" }

                form (action = "/admin/probe/run", method = "post") {
                    context.GetAntiforgeryInput()
                    button (type' = "submit") { "Run probe" }
                }
            }

        SharedViews.layout context "Probe" content
