namespace App.Views

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module InvoiceViews =
    let list (context: HttpContext) (model: InvoiceListModel) =
        let content =
            section (class' = "card") {
                h1 () { model.Title }

                if model.Rows.IsEmpty then
                    p (class' = "muted") { "No invoices yet." }
                else
                    table () {
                        thead () {
                            tr () {
                                th () { "Number" }
                                th () { "Type" }
                                th () { "Issued" }
                                th () { "Order" }
                                th () { "Total" }
                                th () { "Status" }
                                th () { "" }
                            }
                        }

                        tbody () {
                            for row in model.Rows do
                                tr () {
                                    td () { row.Number }
                                    td () { row.Kind }
                                    td () { row.IssuedOn }
                                    td () { row.OrderId }
                                    td () { row.Total }
                                    td () { row.Status }

                                    td () {
                                        match row.DownloadUrl with
                                        | Some url -> a (href = url) { "Download PDF" }
                                        | None -> ()

                                        match row.Retry with
                                        | Some(url, key) ->
                                            form (action = url, method = "post") {
                                                context.GetAntiforgeryInput()
                                                input (type' = "hidden", name = "key", value = key)
                                                button (type' = "submit") { "Retry render" }
                                            }
                                        | None -> ()
                                    }
                                }
                        }
                    }

                match model.OlderUrl with
                | Some url -> a (href = url) { "Older invoices" }
                | None -> ()
            }

        SharedViews.layout context model.Title content
