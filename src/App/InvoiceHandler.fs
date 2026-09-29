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
          Name = value configuration "Invoicing:SellerName" "fsnix Store"
          Address = value configuration "Invoicing:SellerAddress" "1 Example Street, Springfield"
          TaxId = value configuration "Invoicing:SellerTaxId" "" }
        |> InvoiceIssuer.validate
        |> Result.defaultWith invalidOp

type InvoiceEffectHandler(dataSource: NpgsqlDataSource, issuer: InvoiceIssuer, timeProvider: TimeProvider) =
    interface IActionHandler<InvoiceEntityId, InvoiceAction, InvoiceActionError> with
        member _.HandleAsync(action: LeasedAction<InvoiceEntityId, InvoiceAction>, ct: CancellationToken) =
            match action.Work.Action with
            | IssueSnapshot _ -> InvoiceEffects.applyIssue dataSource issuer (timeProvider.GetUtcNow()) action.Work ct
            | RenderDocument _ -> InvoiceEffects.applyRender dataSource InvoicePdf.renderer action.Work ct
