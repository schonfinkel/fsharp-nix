namespace App.Views

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.Htmx
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module CatalogViews =
    let private card (context: HttpContext) (product: CatalogProductModel) =
        article (class' = "card product-card") {
            h3 () { product.Name }

            p (class' = "muted") { product.Sku }

            if product.Description <> "" then
                p () { product.Description }

            p (class' = "price") { product.UnitPrice }

            p (class' = "muted") { $"In stock: {product.OnHand}" }

            form(action = "/cart/items", method = "post").hxPost("/cart/items").hxTarget("this").hxSwap ("none") {
                context.GetAntiforgeryInput()
                input (type' = "hidden", name = "productId", value = product.ProductId)
                input (type' = "hidden", name = "quantity", value = "1")
                button (type' = "submit") { "Add to cart" }
            }
        }

    let catalog (context: HttpContext) (model: CatalogModel) =
        Fragment() {
            form (action = "/catalog", method = "get", class' = "search") {
                input (type' = "search", name = "q", value = model.Query, placeholder = "Search products")

                button (type' = "submit") { "Search" }
            }

            if model.Products.IsEmpty then
                p (class' = "muted") { "No products found." }
            else
                div (class' = "grid") {
                    for product in model.Products do
                        card context product
                }
        }

    let productForm (model: ProductFormModel) =
        Fragment() {
            match model.Error with
            | Some message -> p (class' = "error") { message }
            | None -> ()

            form (action = "/admin/catalog/products", method = "post") {
                match model.ProductId with
                | Some id -> input (type' = "hidden", name = "productId", value = id)
                | None -> ()

                label (for' = "sku") { "SKU" }
                input (type' = "text", id = "sku", name = "sku", value = model.Sku, required = true)

                label (for' = "name") { "Name" }
                input (type' = "text", id = "name", name = "name", value = model.Name, required = true)

                label (for' = "description") { "Description" }

                textarea (name = "description", id = "description") { model.Description }

                label (for' = "price") { "Price" }
                input (type' = "text", id = "price", name = "price", value = model.Price, required = true)

                label (for' = "currency") { "Currency" }

                input (type' = "text", id = "currency", name = "currency", value = model.Currency, required = true)

                label (for' = "onHand") { "On hand" }
                input (type' = "number", id = "onHand", name = "onHand", value = model.OnHand)

                div (class' = "actions") { button (type' = "submit") { "Save product" } }
            }
        }

    let admin (context: HttpContext) (model: AdminCatalogModel) =
        section (id = "catalog-admin", class' = "card") {
            h1 () { "Catalog" }
            productForm model.Form

            table () {
                thead () {
                    tr () {
                        th () { "SKU" }
                        th () { "Name" }
                        th () { "Price" }
                        th () { "On hand" }
                        th () { "Status" }
                        th () { "" }
                        th () { "" }
                    }
                }

                tbody () {
                    for product in model.Products do
                        tr () {
                            td () { product.Sku }
                            td () { product.Name }
                            td () { product.UnitPrice }
                            td () { string product.OnHand }

                            td () { if product.Active then "active" else "retired" }

                            td () {
                                if product.Active then
                                    form (
                                        action = $"/admin/catalog/products/{product.ProductId}/retire",
                                        method = "post"
                                    ) {
                                        button (type' = "submit") { "Retire" }
                                    }
                            }

                            td () {
                                form (
                                    action = $"/admin/catalog/products/{product.ProductId}/adjust-stock",
                                    method = "post",
                                    class' = "inline"
                                ) {
                                    input (type' = "number", name = "delta", value = "0")
                                    button (type' = "submit") { "Adjust stock" }
                                }
                            }
                        }
                }
            }
        }
