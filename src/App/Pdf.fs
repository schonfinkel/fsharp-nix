namespace App

open System
open QuestPDF
open QuestPDF.Fluent
open QuestPDF.Helpers
open QuestPDF.Infrastructure

/// <summary>
/// The QuestPDF runtime. Rendering must be identical in devenv, tests, the Nix package and the
/// OCI image, which has no <c>/etc/fonts</c>: system fonts are never used, only the Lato family
/// QuestPDF ships next to the application (<c>QuestPDF.Fonts.Lato.br</c>). A missing family or
/// glyph fails the render instead of silently substituting.
/// </summary>
[<RequireQualifiedAccess>]
module Pdf =
    let private configured =
        lazy
            (Settings.License <- LicenseType.Community
             Settings.UseSystemFonts <- false
             Settings.ThrowOnMissingFontFamilies <- true
             Settings.ThrowOnMissingTextGlyphs <- true)

    /// <summary>Applies the process-wide settings once; safe to call repeatedly.</summary>
    let configure () = configured.Force()

    /// <summary>Fixed so identical content renders to identical bytes, which lets rendered
    /// artifacts be content-addressed.</summary>
    let private fixedTimestamp = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

    let metadata title =
        DocumentMetadata(
            Title = title,
            Author = "fsnix",
            Creator = "fsnix",
            Producer = "fsnix",
            Language = "en-US",
            CreationDate = fixedTimestamp,
            ModifiedDate = fixedTimestamp
        )

    /// <summary>A fixed document exercising text, a table and non-ASCII Latin glyphs; used by
    /// <c>--render-check</c> and the tests to prove the native library and fonts load.</summary>
    let renderSample () : byte array =
        configure ()

        Document
            .Create(fun container ->
                container.Page(fun page ->
                    page.Size PageSizes.A4
                    page.Margin(2f, Unit.Centimetre)
                    page.DefaultTextStyle(fun style -> style.FontSize 11f)

                    page.Header().Text("fsnix render check").SemiBold().FontSize(18f) |> ignore

                    page
                        .Content()
                        .PaddingVertical(1f, Unit.Centimetre)
                        .Table(fun table ->
                            table.ColumnsDefinition(fun columns ->
                                columns.RelativeColumn 3f
                                columns.RelativeColumn 1f)

                            for label, amount in
                                [ "Espresso Beans — Café Ação", "24.90"
                                  "Shipping", "5.00"
                                  "Tax (8%)", "2.39"
                                  "Total", "32.29" ] do
                                table.Cell().Text(label) |> ignore
                                table.Cell().AlignRight().Text(amount) |> ignore)

                    page.Footer().AlignCenter().Text("Page 1") |> ignore)
                |> ignore)
            .WithMetadata(metadata "fsnix render check")
            .GeneratePdf()

    /// <summary>True when <paramref name="bytes"/> is a non-empty PDF.</summary>
    let isPdf (bytes: byte array) =
        not (isNull bytes)
        && bytes.Length > 5
        && Text.Encoding.ASCII.GetString(bytes, 0, 5) = "%PDF-"
