namespace App.Views

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module CheckoutViews =
    let page (context: HttpContext) (model: CheckoutModel) =
        let content =
            section (class' = "card") {
                h1 () { "Checkout" }

                match model.Error with
                | Some error -> p (class' = "error") { error }
                | None -> ()

                form (action = "/checkout", method = "post") {
                    context.GetAntiforgeryInput()
                    input (type' = "hidden", name = "orderKey", value = model.OrderKey)

                    label () {
                        "Contact email"
                        input (type' = "email", name = "email", value = model.Email, required = true)
                    }

                    label () {
                        "Recipient"
                        input (name = "recipient", value = model.Recipient, required = true)
                    }

                    label () {
                        "Address"
                        input (name = "line1", value = model.Line1, required = true)
                    }

                    label () {
                        "Address line 2"
                        input (name = "line2", value = model.Line2)
                    }

                    label () {
                        "City"
                        input (name = "city", value = model.City, required = true)
                    }

                    label () {
                        "Region"
                        input (name = "region", value = model.Region)
                    }

                    label () {
                        "Postal code"
                        input (name = "postalCode", value = model.PostalCode, required = true)
                    }

                    label () {
                        "Country code"
                        input (name = "countryCode", value = model.CountryCode, required = true, maxlength = 2)
                    }

                    button (type' = "submit") { "Place order" }
                }
            }

        SharedViews.layout context "Checkout" content

    let orderPage (context: HttpContext) (model: OrderStatusModel) =
        let content =
            section (class' = "card") {
                h1 () { "Order status" }
                p () { $"Order: {model.OrderId}" }
                p () { model.Status }

                if model.Total <> "" then
                    p (class' = "total") { $"Total: {model.Total}" }

                match model.Error with
                | Some error -> p (class' = "error") { error }
                | None -> ()

                match model.Pay with
                | Some pay ->
                    form (action = $"/orders/{model.OrderId}/authorize", method = "post") {
                        context.GetAntiforgeryInput()
                        input (type' = "hidden", name = "attempt", value = pay.Attempt)

                        fieldset () {
                            legend () { "Payment method" }

                            for (value, caption) in pay.Methods do
                                label () {
                                    input (type' = "radio", name = "method", value = value, required = true)
                                    caption
                                }
                        }

                        button (type' = "submit") { "Authorize payment" }
                    }
                | None -> ()

                if model.TooLateToCancel then
                    p (class' = "notice") { "This order can no longer be cancelled here. Use returns after delivery." }

                if model.CanCancel then
                    form (action = $"/orders/{model.OrderId}/cancel", method = "post") {
                        context.GetAntiforgeryInput()
                        button (type' = "submit") { "Cancel order" }
                    }

                a (href = "/cart") { "Return to cart" }
            }

        SharedViews.layout context "Order" content
