namespace App.Domain

open System
open NodaMoney

type OrderTotals =
    { Subtotal: Money
      Shipping: Money
      Tax: Money
      Total: Money }

[<RequireQualifiedAccess>]
module OrderPricing =
    /// Prices each order line using currency-aware NodaMoney rounding. Shipping and tax must
    /// use the same currency as every line; tax is calculated on subtotal plus shipping.
    let compute (lines: (Money * Quantity) list) (shipping: Money) (taxRate: decimal) : Result<OrderTotals, string> =
        if lines.IsEmpty then
            Error "An order must contain at least one line."
        elif taxRate < 0m || taxRate > 1m then
            Error "The tax rate must be between zero and one."
        else
            let currency = Money.currencyCode shipping

            if lines |> List.exists (fun (price, _) -> Money.currencyCode price <> currency) then
                Error "All order lines and checkout charges must use the same currency."
            else
                let subtotal =
                    lines
                    |> List.fold
                        (fun total (price, quantity) ->
                            Money.add total (Money.multiply price (decimal (Quantity.value quantity))))
                        (Money.zero currency |> Result.defaultWith invalidOp)

                let taxable = Money.add subtotal shipping
                let tax = Money.multiply taxable taxRate

                Ok
                    { Subtotal = subtotal
                      Shipping = shipping
                      Tax = tax
                      Total = Money.add taxable tax }
