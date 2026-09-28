namespace App.Domain

open System
open System.Globalization
open NodaMoney

/// <summary>
/// Currency-aware money helpers built on NodaMoney. The durable wire representation is a
/// decimal string (invariant culture) plus an ISO 4217 code; it never uses NodaMoney's default
/// JSON converter, whose rounding is coupled to the ambient <c>MoneyContext</c>.
/// </summary>
[<RequireQualifiedAccess>]
module Money =

    /// <summary>Creates money from an amount and ISO 4217 currency code. The constructor
    /// rejects an unknown or empty code; the amount is rounded to the currency minor unit.</summary>
    let create (amount: decimal) (currencyCode: string) : Result<Money, string> =
        if String.IsNullOrWhiteSpace currencyCode then
            Error "A currency code is required."
        else
            try
                Ok(Money(amount, currencyCode))
            with
            | :? FormatException
            | :? ArgumentException
            | :? InvalidCurrencyException -> Error $"'{currencyCode}' is not a recognized ISO 4217 currency code."

    /// <summary>The decimal amount of a money value, already rounded to the currency's minor
    /// unit by the constructor.</summary>
    let amount (money: Money) : decimal = money.Amount

    /// <summary>The ISO 4217 currency code of a money value.</summary>
    let currencyCode (money: Money) : string = money.Currency.Code

    let zero (currencyCode: string) : Result<Money, string> = create 0m currencyCode

    let add (a: Money) (b: Money) : Money = a + b

    let multiply (money: Money) (factor: decimal) : Money = money * factor

    /// <summary>A stable display string: invariant-culture amount followed by the ISO code.</summary>
    let format (money: Money) : string =
        let amount = money.Amount.ToString("0.00", CultureInfo.InvariantCulture)
        $"{amount} {money.Currency.Code}"

    /// <summary>The canonical durable amount: an invariant-culture decimal string.</summary>
    let wireAmount (money: Money) : string =
        money.Amount.ToString("G29", CultureInfo.InvariantCulture)

    /// <summary>Parses a durable amount back to a decimal, or fails with a message.</summary>
    let tryWireAmount (value: string) : Result<decimal, string> =
        match Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture) with
        | true, amount -> Ok amount
        | false, _ -> Error $"'{value}' is not a valid money amount."

    /// <summary>Decodes a durable <c>(amount, currency)</c> pair back to money.</summary>
    let tryOfWire (amountValue: string) (currency: string) : Result<Money, string> =
        tryWireAmount amountValue |> Result.bind (fun value -> create value currency)
