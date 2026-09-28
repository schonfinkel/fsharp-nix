namespace App.Views

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module CartViews =
    let private lineRow (context: HttpContext) (epoch: int64) (line: CartLineModel) =
        tr (class' = "cart-line") {
            td () { line.Name }

            td () {
                form(action = $"/cart/items/{line.ProductId}", method = "post")
                    .hxPost($"/cart/items/{line.ProductId}")
                    .hxTarget("#cart-panel")
                    .hxSwap("outerHTML")
                    .hxStatus ("409", "swap:outerHTML target:#cart-panel") {
                    context.GetAntiforgeryInput()
                    input (type' = "number", name = "quantity", value = string line.Quantity, min = "1")
                    input (type' = "hidden", name = "epoch", value = string epoch)
                    button (type' = "submit") { "Update" }
                }
            }

            td () { line.UnitPrice }

            td () {
                form(action = $"/cart/items/{line.ProductId}/remove", method = "post")
                    .hxPost($"/cart/items/{line.ProductId}/remove")
                    .hxTarget("#cart-panel")
                    .hxSwap("outerHTML")
                    .hxStatus ("409", "swap:outerHTML target:#cart-panel") {
                    context.GetAntiforgeryInput()
                    input (type' = "hidden", name = "epoch", value = string epoch)
                    button (type' = "submit") { "Remove" }
                }
            }
        }

    /// <summary>The cart panel. It re-fetches itself on a <c>cart-change</c> SSE hint so other
    /// tabs stay current, and every mutation swaps the region back in place.</summary>
    let fragment (context: HttpContext) (model: CartModel) =
        section(id = "cart-panel", class' = "card cart-card")
            .hxGet("/cart")
            .hxTrigger("cart-change from:body")
            .hxTarget("this")
            .hxSwap ("outerHTML") {
            h1 () { "Your cart" }

            match model.Error with
            | Some message -> p (class' = "error") { message }
            | None -> ()

            if model.Lines.IsEmpty then
                p (class' = "muted") { "Your cart is empty." }
            else
                table (class' = "cart-lines") {
                    thead () {
                        tr () {
                            th () { "Item" }
                            th () { "Quantity" }
                            th () { "Price" }
                            th () { "" }
                        }
                    }

                    tbody () {
                        for line in model.Lines do
                            lineRow context model.Epoch line
                    }
                }

                p (class' = "total") { $"Total: {model.Total}" }

                form(action = "/cart/clear", method = "post")
                    .hxPost("/cart/clear")
                    .hxTarget("#cart-panel")
                    .hxSwap("outerHTML")
                    .hxStatus ("409", "swap:outerHTML target:#cart-panel") {
                    context.GetAntiforgeryInput()
                    input (type' = "hidden", name = "epoch", value = string model.Epoch)
                    button (type' = "submit") { "Clear cart" }
                }

            a (href = "/catalog") { "Continue shopping" }
        }

    /// <summary>Out-of-band cart count badge; swapped into <c>#cart-count</c> in the layout.</summary>
    let badge (count: int) =
        span(id = "cart-count", class' = "badge").attr ("hx-swap-oob", "true") {
            if count > 0 then
                string count
        }

    /// <summary>Out-of-band confirmation alert; swapped into <c>#cart-alert</c> in the layout.</summary>
    let alert (message: string) =
        div(id = "cart-alert", class' = "cart-alert").attr ("hx-swap-oob", "true") { span () { message } }
