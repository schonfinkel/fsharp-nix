namespace App.Domain

open System
open NodaMoney

type ReturnLine =
    { OrderLineId: OrderLineId
      Quantity: int
      RefundAmount: Money }

type ReturnRequest =
    { ReturnId: ReturnId
      AuthorizationId: ReturnAuthorizationId
      OrderId: string
      Lines: ReturnLine list
      Currency: string
      WindowEndsAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module ReturnRequest =
    let validate (request: ReturnRequest) =
        if String.IsNullOrWhiteSpace request.OrderId || request.Lines.IsEmpty then
            Error "A return needs an order and at least one line."
        elif
            request.Lines
            |> List.exists (fun line -> line.Quantity <= 0 || Money.amount line.RefundAmount < 0m)
        then
            Error "Return quantities must be positive and amounts nonnegative."
        elif
            request.Lines
            |> List.countBy _.OrderLineId
            |> List.exists (fun (_, count) -> count > 1)
        then
            Error "A return may mention an order line only once."
        elif
            request.Lines
            |> List.exists (fun line -> Money.currencyCode line.RefundAmount <> request.Currency)
        then
            Error "Return line currencies must agree."
        elif request.WindowEndsAt <= DateTimeOffset.UnixEpoch then
            Error "A return requires a valid deadline."
        else
            Ok request

    let total request =
        request.Lines
        |> List.fold
            (fun sum line -> sum + line.RefundAmount)
            (Money.zero request.Currency |> Result.defaultWith invalidOp)
