namespace App

open System
open System.Threading
open App.Database
open App.Invoices
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Microsoft.Extensions.Configuration
open Npgsql

[<RequireQualifiedAccess>]
module InvoiceIssuerConfig =
    let private value (configuration: IConfiguration) key fallback =
        match configuration[key] with
        | text when String.IsNullOrWhiteSpace text -> fallback
        | text -> text

    let load (configuration: IConfiguration) =
        { LegalEntity = value configuration "Invoicing:LegalEntity" "FSNIX"
          Series = value configuration "Invoicing:Series" "INV"
          CreditSeries = value configuration "Invoicing:CreditSeries" "CN"
          Name = value configuration "Invoicing:SellerName" "fsnix Store"
          Address = value configuration "Invoicing:SellerAddress" "1 Example Street, Springfield"
          TaxId = value configuration "Invoicing:SellerTaxId" "" }
        |> InvoiceIssuer.validate
        |> Result.defaultWith invalidOp

[<RequireQualifiedAccess>]
module InvoiceRenderPolicyConfig =
    /// <summary><c>Invoicing:RenderCheckAfter</c> (invariant TimeSpan) and
    /// <c>Invoicing:RenderMaxChecks</c>.</summary>
    let load (configuration: IConfiguration) =
        let checkAfter =
            match configuration["Invoicing:RenderCheckAfter"] with
            | text when String.IsNullOrWhiteSpace text -> InvoiceRenderPolicy.defaults.CheckAfter
            | text ->
                match TimeSpan.TryParse(text, Globalization.CultureInfo.InvariantCulture) with
                | true, value when value > TimeSpan.Zero -> value
                | _ -> invalidOp "Invoicing:RenderCheckAfter must be a positive TimeSpan."

        let maxChecks =
            match configuration["Invoicing:RenderMaxChecks"] with
            | text when String.IsNullOrWhiteSpace text -> InvoiceRenderPolicy.defaults.MaxChecks
            | text ->
                match
                    Int32.TryParse(text, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
                with
                | true, value -> value
                | _ -> invalidOp "Invoicing:RenderMaxChecks must be a non-negative integer."

        { CheckAfter = checkAfter
          MaxChecks = maxChecks }

type InvoiceEffectHandler
    (dataSource: NpgsqlDataSource, issuer: InvoiceIssuer, renderPolicy: InvoiceRenderPolicy, timeProvider: TimeProvider)
    =
    interface IActionHandler<InvoiceEntityId, InvoiceAction, InvoiceActionError> with
        member _.HandleAsync(action: LeasedAction<InvoiceEntityId, InvoiceAction>, ct: CancellationToken) =
            match action.Work.Action with
            | IssueSnapshot _ ->
                InvoiceEffects.applyIssue dataSource issuer renderPolicy (timeProvider.GetUtcNow()) action.Work ct
            | RenderDocument _ -> InvoiceEffects.applyRender dataSource InvoicePdf.renderer action.Work ct
