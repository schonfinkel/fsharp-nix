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
module CatalogAdminEndpoints =

    let private field name (form: IFormCollection) =
        match form.TryGetValue name with
        | true, value -> string value
        | false, _ -> ""

    let private emptyForm: ProductFormModel =
        { ProductId = None
          Sku = ""
          Name = ""
          Description = ""
          Price = ""
          Currency = "USD"
          OnHand = ""
          Error = None }

    let private productModel (product: ProductSnapshot) : CatalogProductModel =
        { ProductId = ProductId.wireString product.ProductId
          Sku = Sku.value product.Sku
          Name = NonEmptyString.value product.Name
          Description = product.Description |> Option.defaultValue ""
          UnitPrice = Money.format product.UnitPrice
          OnHand = product.OnHand
          Active = product.Active }

    let private render (context: HttpContext) (form: ProductFormModel) (status: int) =
        task {
            Web.noStore context
            Web.varyHtmx context
            context.Response.StatusCode <- status
            let catalog = context.GetService<CatalogStore>()
            let! products = catalog.Browse(false, context.RequestAborted)

            let model: AdminCatalogModel =
                { Products = products |> List.map productModel
                  Form = form }

            return!
                context.WriteHtmlView(
                    SharedViews.layout context "Catalog administration" (CatalogViews.admin context model)
                )
        }

    let index: EndpointHandler =
        fun context ->
            task {
                let catalog = context.GetService<CatalogStore>()
                let! products = catalog.Browse(false, context.RequestAborted)

                let model: AdminCatalogModel =
                    { Products = products |> List.map productModel
                      Form = emptyForm }

                return!
                    context.WriteHtmlView(
                        SharedViews.layout context "Catalog administration" (CatalogViews.admin context model)
                    )
            }

    /// <summary>Creates or updates a product. Editing a product bumps its price version, so
    /// any later reservation against a stale snapshot is rejected.</summary>
    let save: EndpointHandler =
        fun context ->
            task {
                let! form = context.Request.ReadFormAsync context.RequestAborted
                let productIdText = field "productId" form
                let skuText = field "sku" form
                let nameText = field "name" form
                let descriptionText = field "description" form
                let priceText = field "price" form
                let currencyText = field "currency" form
                let onHandText = field "onHand" form

                let description =
                    if String.IsNullOrWhiteSpace descriptionText then
                        None
                    else
                        Some descriptionText

                let fail message =
                    render
                        context
                        { ProductId =
                            (if String.IsNullOrWhiteSpace productIdText then
                                 None
                             else
                                 Some productIdText)
                          Sku = skuText
                          Name = nameText
                          Description = descriptionText
                          Price = priceText
                          Currency = currencyText
                          OnHand = onHandText
                          Error = Some message }
                        StatusCodes.Status422UnprocessableEntity

                let productId =
                    if String.IsNullOrWhiteSpace productIdText then
                        Some(Guid.NewGuid())
                    else
                        match Guid.TryParse productIdText with
                        | true, id when id <> Guid.Empty -> Some id
                        | _ -> None

                match productId with
                | None -> return! fail "The product could not be found."
                | Some productId ->
                    match Sku.create skuText with
                    | Error _ -> return! fail "Enter a valid SKU."
                    | Ok sku ->
                        match NonEmptyString.create 200 nameText with
                        | Error _ -> return! fail "Enter a product name."
                        | Ok name ->
                            match Decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture) with
                            | false, _ -> return! fail "Enter a valid price."
                            | true, price ->
                                match Money.create price currencyText with
                                | Error _ -> return! fail "Enter a valid ISO 4217 currency code."
                                | Ok unitPrice ->
                                    let onHand =
                                        match Int32.TryParse onHandText with
                                        | true, value when value >= 0 -> value
                                        | _ -> 0

                                    let catalog = context.GetService<CatalogStore>()

                                    if String.IsNullOrWhiteSpace productIdText then
                                        let! result =
                                            catalog.Create(
                                                productId,
                                                sku,
                                                name,
                                                description,
                                                unitPrice,
                                                onHand,
                                                context.RequestAborted
                                            )

                                        match result with
                                        | Ok() -> return! Web.redirect "/admin/catalog" context
                                        | Error CatalogFailure.SkuAlreadyExists ->
                                            return! fail "That SKU is already in use."
                                        | Error _ -> return! fail "The product could not be created."
                                    else
                                        let! result =
                                            catalog.Update(
                                                productId,
                                                name,
                                                description,
                                                unitPrice,
                                                context.RequestAborted
                                            )

                                        match result with
                                        | Ok() -> return! Web.redirect "/admin/catalog" context
                                        | Error _ -> return! fail "The product could not be updated."
            }

    let retire: EndpointHandler =
        fun context ->
            task {
                match context.TryGetRouteValue("productId") with
                | None ->
                    return!
                        render
                            context
                            { emptyForm with
                                Error = Some "The product could not be found." }
                            StatusCodes.Status404NotFound
                | Some value ->
                    match ProductId.tryParse value with
                    | Ok productId ->
                        let catalog = context.GetService<CatalogStore>()
                        let! _ = catalog.Retire(ProductId.value productId, context.RequestAborted)
                        return! Web.redirect "/admin/catalog" context
                    | Error _ ->
                        return!
                            render
                                context
                                { emptyForm with
                                    Error = Some "The product could not be found." }
                                StatusCodes.Status404NotFound
            }

    let adjustStock: EndpointHandler =
        fun context ->
            task {
                let! form = context.Request.ReadFormAsync context.RequestAborted
                let deltaText = field "delta" form

                match context.TryGetRouteValue("productId"), Int32.TryParse deltaText with
                | None, _
                | _, (false, _) ->
                    return!
                        render
                            context
                            { emptyForm with
                                Error = Some "Enter a valid stock adjustment." }
                            StatusCodes.Status422UnprocessableEntity
                | Some value, (true, delta) ->
                    match ProductId.tryParse value with
                    | Ok productId ->
                        let catalog = context.GetService<CatalogStore>()
                        let! result = catalog.AdjustStock(ProductId.value productId, delta, context.RequestAborted)

                        match result with
                        | Ok() -> return! Web.redirect "/admin/catalog" context
                        | Error CatalogFailure.ProductNotFound ->
                            return!
                                render
                                    context
                                    { emptyForm with
                                        Error = Some "The product could not be found." }
                                    StatusCodes.Status404NotFound
                        | Error CatalogFailure.InsufficientStock ->
                            return!
                                render
                                    context
                                    { emptyForm with
                                        Error = Some "Stock cannot go negative." }
                                    StatusCodes.Status409Conflict
                        | Error _ ->
                            return!
                                render
                                    context
                                    { emptyForm with
                                        Error = Some "The stock adjustment failed." }
                                    StatusCodes.Status500InternalServerError
                    | _ ->
                        return!
                            render
                                context
                                { emptyForm with
                                    Error = Some "The product could not be found." }
                                StatusCodes.Status404NotFound
            }
