namespace App.Domain

open NodaMoney

type RefundRequest =
    { RefundId: RefundId
      AllocationId: RefundAllocationId
      OperationId: PaymentOperationId
      Origin: RefundOrigin
      Amount: Money }

type ApprovedRefund =
    { Request: RefundRequest
      PaymentReference: string }

type RefundBalance =
    { Captured: Money
      Refunded: Money
      Pending: Money }

[<RequireQualifiedAccess>]
module RefundAllocation =
    /// Payment is the authority: pending reservations count against the captured balance.
    let available (balance: RefundBalance) =
        if
            Money.currencyCode balance.Captured <> Money.currencyCode balance.Refunded
            || Money.currencyCode balance.Captured <> Money.currencyCode balance.Pending
        then
            Error "Refund currency differs from capture currency."
        elif Money.amount balance.Refunded < 0m || Money.amount balance.Pending < 0m then
            Error "Refund balances cannot be negative."
        elif balance.Refunded + balance.Pending > balance.Captured then
            Error "Refunds and allocations exceed captured funds."
        else
            Ok(balance.Captured - balance.Refunded - balance.Pending)

    let reserve balance amount =
        available balance
        |> Result.bind (fun remaining ->
            if
                Money.currencyCode remaining <> Money.currencyCode amount
                || Money.amount amount <= 0m
                || amount > remaining
            then
                Error "Refund exceeds the available captured amount."
            else
                Ok
                    { balance with
                        Pending = balance.Pending + amount })
