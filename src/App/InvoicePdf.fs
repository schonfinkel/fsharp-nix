namespace App

open System
open System.Globalization
open App.Database
open App.Domain
open QuestPDF.Fluent
open QuestPDF.Helpers
open QuestPDF.Infrastructure

/// <summary>The invoice PDF template. Output depends only on the stored snapshot (the issue
/// date is the PDF timestamp), so rendering is deterministic and the SHA-256 is a stable content
/// address. Change <see cref="Name"/> whenever the layout changes.</summary>
[<RequireQualifiedAccess>]
module InvoicePdf =
    [<Literal>]
    let Name = "invoice-v1"

    let private money amount currency =
        Money.create amount currency
        |> Result.map Money.format
        |> Result.defaultValue $"{amount} {currency}"

    let private date (value: DateTimeOffset) =
        value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let render (document: InvoiceDocument) : byte array =
        Pdf.configure ()
        let number = InvoiceNumber.display document.Number
        let amount value = money value document.Currency

        let metadata =
            Pdf.metadata $"Invoice {number}"
            |> fun metadata ->
                metadata.CreationDate <- document.IssuedAt
                metadata.ModifiedDate <- document.IssuedAt
                metadata

        Document
            .Create(fun container ->
                container.Page(fun page ->
                    page.Size PageSizes.A4
                    page.Margin(2f, Unit.Centimetre)
                    page.DefaultTextStyle(fun style -> style.FontSize 10f)

                    page
                        .Header()
                        .Row(fun row ->
                            row
                                .RelativeItem()
                                .Column(fun column ->
                                    column.Item().Text(document.SellerName).SemiBold().FontSize(14f) |> ignore
                                    column.Item().Text(document.SellerAddress) |> ignore

                                    if document.SellerTaxId <> "" then
                                        column.Item().Text($"Tax ID: {document.SellerTaxId}") |> ignore)

                            row
                                .RelativeItem()
                                .AlignRight()
                                .Column(fun column ->
                                    column.Item().Text($"Invoice {number}").SemiBold().FontSize(16f) |> ignore
                                    column.Item().Text($"Issued {date document.IssuedAt}") |> ignore))

                    page
                        .Content()
                        .PaddingVertical(1f, Unit.Centimetre)
                        .Column(fun column ->
                            column.Spacing 12f

                            column
                                .Item()
                                .Column(fun billTo ->
                                    billTo.Item().Text("Bill to").SemiBold() |> ignore

                                    for line in document.BillingAddress do
                                        billTo.Item().Text(line) |> ignore

                                    billTo.Item().Text(document.BuyerEmail) |> ignore)

                            column
                                .Item()
                                .Table(fun table ->
                                    table.ColumnsDefinition(fun columns ->
                                        columns.RelativeColumn 2f
                                        columns.RelativeColumn 4f
                                        columns.RelativeColumn 1f
                                        columns.RelativeColumn 2f
                                        columns.RelativeColumn 2f)

                                    for heading in [ "SKU"; "Description"; "Qty"; "Unit price"; "Amount" ] do
                                        table.Cell().BorderBottom(1f).PaddingBottom(4f).Text(heading).SemiBold()
                                        |> ignore

                                    for line in document.Lines do
                                        table.Cell().PaddingVertical(2f).Text(line.Sku) |> ignore
                                        table.Cell().PaddingVertical(2f).Text(line.Description) |> ignore

                                        table.Cell().PaddingVertical(2f).AlignRight().Text(string line.Quantity)
                                        |> ignore

                                        table.Cell().PaddingVertical(2f).AlignRight().Text(amount line.UnitPrice)
                                        |> ignore

                                        table.Cell().PaddingVertical(2f).AlignRight().Text(amount line.Amount)
                                        |> ignore)

                            column
                                .Item()
                                .AlignRight()
                                .Width(220f)
                                .Table(fun totals ->
                                    totals.ColumnsDefinition(fun columns ->
                                        columns.RelativeColumn 1f
                                        columns.RelativeColumn 1f)

                                    for label, value, strong in
                                        [ "Subtotal", document.Subtotal, false
                                          "Shipping", document.Shipping, false
                                          "Tax", document.Tax, false
                                          "Total", document.Total, true ] do
                                        let caption = totals.Cell().Text(label)
                                        let figure = totals.Cell().AlignRight().Text(amount value)

                                        if strong then
                                            caption.SemiBold() |> ignore
                                            figure.SemiBold() |> ignore))

                    page
                        .Footer()
                        .AlignCenter()
                        .Text(fun text ->
                            text.Span($"{number} · page ") |> ignore
                            text.CurrentPageNumber() |> ignore
                            text.Span(" of ") |> ignore
                            text.TotalPages() |> ignore))
                |> ignore)
            .WithMetadata(metadata)
            .GeneratePdf()

    let renderer: InvoiceRenderer = { Name = Name; Render = render }
