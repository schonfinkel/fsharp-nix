namespace App

open System
open System.Globalization
open System.Threading.Tasks
open App.Database
open App.Domain
open App.Views
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker

[<RequireQualifiedAccess>]
module CatalogEndpoints =

    let private modelOf (products: ProductSnapshot list) (query: string) : CatalogModel =
        { Query = query
          Products =
            products
            |> List.map (fun product ->
                { ProductId = ProductId.wireString product.ProductId
                  Sku = Sku.value product.Sku
                  Name = NonEmptyString.value product.Name
                  Description = product.Description |> Option.defaultValue ""
                  UnitPrice = Money.format product.UnitPrice
                  OnHand = product.OnHand
                  Active = product.Active }) }

    let private render (context: HttpContext) (model: CatalogModel) =
        task {
            Web.noStore context
            Web.varyHtmx context
            let content = CatalogViews.catalog context model

            if Web.isHtmx context then
                return! context.WriteHtmlView content
            else
                return! context.WriteHtmlView(SharedViews.layout context "Catalog" content)
        }

    let index: EndpointHandler =
        fun context ->
            task {
                let catalog = context.GetService<CatalogStore>()
                let! products = catalog.Browse(true, context.RequestAborted)
                return! render context (modelOf products "")
            }

    let search: EndpointHandler =
        fun context ->
            task {
                let query = context.Request.Query["q"] |> string
                let catalog = context.GetService<CatalogStore>()
                let! products = catalog.Search(query, context.RequestAborted)
                return! render context (modelOf products query)
            }
