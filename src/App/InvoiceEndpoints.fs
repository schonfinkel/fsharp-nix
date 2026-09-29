namespace App

open System
open System.Globalization
open System.Threading.Tasks
open App.Database
open App.Domain
open App.Invoices
open App.Views
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Primitives
open Npgsql
open Oxpecker

[<RequireQualifiedAccess>]
module InvoiceEndpoints =
    [<Literal>]
    let private PageSize = 25

    let private routeId (context: HttpContext) =
        context.TryGetRouteValue("invoiceId")
        |> Option.defaultValue ""
        |> InvoiceId.tryParse
        |> Result.toOption

    /// <summary>A malformed cursor is ignored (first page) rather than an error.</summary>
    let private before (context: HttpContext) =
        match context.Request.Query.TryGetValue "before" with
        | true, value -> InvoiceId.tryParse (string value) |> Result.toOption
        | _ -> None

    let private customerId (context: HttpContext) =
        task {
            let users = context.GetService<UserManager<ApplicationUser>>()
            let! user = users.GetUserAsync context.User
            return if isNull user then None else Some user.Id
        }

    let private row (invoice: InvoiceListRow) status downloadUrl retry =
        { Number = InvoiceNumber.display invoice.Number
          IssuedOn = invoice.IssuedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
          Total =
            Money.create invoice.Total invoice.Currency
            |> Result.map Money.format
            |> Result.defaultValue $"{invoice.Total} {invoice.Currency}"
          OrderId = invoice.OrderId
          Status = status
          DownloadUrl = downloadUrl
          Retry = retry }

    let private olderUrl root (page: InvoicePage) =
        page.Next |> Option.map (fun id -> $"{root}?before={InvoiceId.wireString id}")

    let private sendPdf (document: StoredInvoiceDocument) (context: HttpContext) : Task =
        Web.noStore context
        let digest = DocumentDigest.value document.Digest
        context.Response.ContentType <- "application/pdf"
        context.Response.ContentLength <- document.Content.LongLength
        context.Response.Headers.ETag <- StringValues $"\"{digest}\""

        context.Response.Headers.ContentDisposition <-
            StringValues $"attachment; filename=\"{InvoiceNumber.display document.Number}.pdf\""

        context.Response.Headers.XContentTypeOptions <- StringValues "nosniff"
        context.Response.Body.WriteAsync(document.Content, context.RequestAborted).AsTask()

    /// <summary>Serves the stored PDF; 202 while the invoice exists but is not rendered yet, 404
    /// when it does not exist or is not visible to <paramref name="owner"/>.</summary>
    let private download (listUrl: string) (owner: Guid option) (invoiceId: InvoiceId) (context: HttpContext) =
        task {
            let dataSource = context.GetService<NpgsqlDataSource>()
            let! document = InvoiceQueries.tryDocument dataSource invoiceId owner context.RequestAborted

            match document with
            | Some document -> return! sendPdf document context
            | None ->
                let! exists = InvoiceQueries.exists dataSource invoiceId owner context.RequestAborted

                if exists then
                    return! HttpErrors.accepted listUrl context
                else
                    return! HttpErrors.notFound "Invoice not found." context
        }

    let list: EndpointHandler =
        fun context ->
            task {
                Web.noStore context

                match! customerId context with
                | None -> return! HttpErrors.notFound "Invoice not found." context
                | Some customer ->
                    let dataSource = context.GetService<NpgsqlDataSource>()

                    let! page =
                        InvoiceQueries.list dataSource (Some customer) (before context) PageSize context.RequestAborted

                    let rows =
                        page.Rows
                        |> List.map (fun invoice ->
                            if invoice.HasDocument then
                                row
                                    invoice
                                    "Ready"
                                    (Some $"/invoices/{InvoiceId.wireString invoice.InvoiceId}/pdf")
                                    None
                            else
                                row invoice "Being prepared" None None)

                    return!
                        context.WriteHtmlView(
                            InvoiceViews.list
                                context
                                { Title = "Invoices"
                                  Rows = rows
                                  OlderUrl = olderUrl "/invoices" page }
                        )
            }

    let customerPdf: EndpointHandler =
        fun context ->
            task {
                match routeId context with
                | None -> return! HttpErrors.notFound "Invoice not found." context
                | Some invoiceId ->
                    match! customerId context with
                    | None -> return! HttpErrors.notFound "Invoice not found." context
                    | Some customer -> return! download "/invoices" (Some customer) invoiceId context
            }

    let adminPdf: EndpointHandler =
        fun context ->
            task {
                match routeId context with
                | None -> return! HttpErrors.notFound "Invoice not found." context
                | Some invoiceId -> return! download "/admin/invoices" None invoiceId context
            }

    let adminList: EndpointHandler =
        fun context ->
            task {
                Web.noStore context
                let dataSource = context.GetService<NpgsqlDataSource>()
                let invoices = context.GetService<InvoiceMachineClient>()
                let! page = InvoiceQueries.list dataSource None (before context) PageSize context.RequestAborted

                let! rows =
                    page.Rows
                    |> List.map (fun invoice ->
                        task {
                            let id = InvoiceId.wireString invoice.InvoiceId

                            let! state =
                                Machine.state
                                    invoices.Invoices
                                    (Invoices.invoiceEntityId invoice.InvoiceId)
                                    context.RequestAborted

                            let status, retry =
                                match state with
                                | Ok(Some snapshot) ->
                                    match snapshot.State with
                                    | RenderFailed(_, reason) ->
                                        $"Render failed ({ReasonCode.value reason})",
                                        Some($"/admin/invoices/{id}/retry-render", Guid.NewGuid().ToString("D"))
                                    | Rendered _ -> "Ready", None
                                    | RenderPending _ -> "Rendering", None
                                    | SnapshotPending _ -> "Issuing", None
                                    | ManualReview(_, reason) -> $"Manual review ({ReasonCode.value reason})", None
                                    | Closed _ -> "Closed", None
                                    | Initial -> "unknown", None
                                | _ -> "unknown", None

                            let downloadUrl =
                                if invoice.HasDocument then
                                    Some $"/admin/invoices/{id}/pdf"
                                else
                                    None

                            return row invoice status downloadUrl retry
                        })
                    |> Task.WhenAll

                return!
                    context.WriteHtmlView(
                        InvoiceViews.list
                            context
                            { Title = "All invoices"
                              Rows = List.ofArray rows
                              OlderUrl = olderUrl "/admin/invoices" page }
                    )
            }

    /// <summary>Operator recovery for a failed render. The form's key makes a double submit one
    /// command; a retry outside <c>RenderFailed</c> is a 409.</summary>
    let retryRender: EndpointHandler =
        fun context ->
            task {
                match routeId context with
                | None -> return! HttpErrors.notFound "Invoice not found." context
                | Some invoiceId ->
                    let! form = context.Request.ReadFormAsync context.RequestAborted

                    match Guid.TryParseExact(string form["key"], "D") with
                    | false, _ -> return! HttpErrors.badRequest "Missing retry key." context
                    | true, key ->
                        let invoices = context.GetService<InvoiceMachineClient>()

                        let! outcome =
                            Machine.send
                                invoices.Invoices
                                (Invoices.invoiceEntityId invoiceId)
                                (EventEnvelope.create
                                    $"invoice-op:{InvoiceId.wireString invoiceId}:retry-render:{key:D}"
                                    RenderRetryRequested)
                                context.RequestAborted

                        return!
                            HttpErrors.respond
                                (HttpOutcome.ofSend outcome)
                                "The invoice render cannot be retried in its current state."
                                (fun context -> Web.redirect "/admin/invoices" context :> Task)
                                context
            }
