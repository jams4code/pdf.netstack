using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

namespace PdfNetStack.Benchmarks;

/// <summary>The documents the benchmarks read and write, all generated here from fixed seeds.</summary>
internal static class Samples
{
    public const int Pages = 60;
    public const int WordsPerPage = 450;

    private static readonly string[] Vocabulary =
    [
        "the", "of", "and", "to", "in", "is", "that", "for", "it", "as", "with", "was", "on", "be", "by",
        "this", "are", "from", "at", "or", "an", "which", "have", "not", "were", "its", "their", "has",
        "document", "page", "report", "quarter", "revenue", "analysis", "customer", "service", "delivery",
        "contract", "payment", "invoice", "schedule", "project", "budget", "estimate", "measurement",
        "performance", "operations", "agreement", "warranty", "inspection", "procedure", "equipment",
        "maintenance", "requirement", "specification", "compliance", "summary", "overview", "statement",
        "Department", "Committee", "January", "September", "Monday", "Friday", "Section", "Appendix",
        "12", "2026", "3.5", "1,250.00", "47%", "(see", "below)", "item;", "results:", "first,", "e.g.",
    ];

    /// <summary>
    /// A 60 page, 27,000 word report written by this library in an embedded TrueType font:
    /// justified paragraphs, kerned, a heading per page.
    /// </summary>
    public static byte[] TextReport()
    {
        PdfFont body = TrueTypeFont();
        var random = new Random(42);
        PdfDocument doc = PdfDocument.Create();
        for (int p = 0; p < Pages; p++)
        {
            PdfPage page = doc.Pages.Add(PdfRectangle.A4);
            using PdfGraphics g = page.CreateGraphics();
            g.DrawText($"Section {p + 1}: operations review", new PdfTextStyle(body, 16), 56, 56, 483);
            double y = 90;
            int left = WordsPerPage;
            while (left > 0)
            {
                int count = Math.Min(left, 60 + random.Next(40));
                left -= count;
                var words = new string[count];
                for (int i = 0; i < count; i++) words[i] = Vocabulary[random.Next(Vocabulary.Length)];
                words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
                var paragraph = new PdfParagraph { Alignment = PdfTextAlignment.Justify, LineHeight = 1.3 }
                    .Add(string.Join(' ', words) + ".", new PdfTextStyle(body, 10));
                y += g.DrawText(paragraph, 56, y, 483).Height + 8;
            }
        }

        return doc.ToArray();
    }

    /// <summary>
    /// An installed TrueType font with Latin outlines. The fixture fonts in the tests draw a dozen
    /// glyphs only, too few for running text.
    /// </summary>
    private static PdfFont TrueTypeFont()
    {
        foreach (string family in new[] { "Arial", "Liberation Sans", "DejaVu Sans", "Segoe UI", "Helvetica" })
        {
            if (PdfFontCollection.System.Find(family) is { } font) return font;
        }

        throw new InvalidOperationException("No TrueType font with Latin glyphs is installed (looked for Arial, Liberation Sans, DejaVu Sans, Segoe UI).");
    }

    /// <summary>A one page invoice: a header with addresses, a line item table, totals and a footer.</summary>
    public static string Invoice()
    {
        var rows = new System.Text.StringBuilder();
        var random = new Random(7);
        for (int i = 1; i <= 18; i++)
        {
            int quantity = 1 + random.Next(9);
            double price = Math.Round(5 + (random.NextDouble() * 400), 2);
            rows.Append(System.FormattableString.Invariant(
                $"<tr><td>{i}</td><td>Maintenance service, line item {i}<br><small>Reference SV-{1000 + i}</small></td><td class=\"num\">{quantity}</td><td class=\"num\">{price:N2}</td><td class=\"num\">{quantity * price:N2}</td></tr>"));
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>Invoice 2026-114</title>
            <style>
              @page { size: A4; margin: 18mm 16mm 22mm; @bottom-center { content: "Page " counter(page) " of " counter(pages); font-size: 9px; color: #666; } }
              body { font-family: Arial, Helvetica, sans-serif; font-size: 12px; color: #222; }
              header { display: flex; justify-content: space-between; align-items: flex-start; border-bottom: 2px solid #1f4e79; padding-bottom: 12px; }
              h1 { color: #1f4e79; font-size: 28px; margin: 0; }
              .meta td { padding: 2px 8px; }
              .parties { display: flex; gap: 24px; margin: 18px 0; }
              .parties div { flex: 1; background: #f3f6fa; padding: 10px; border-radius: 4px; }
              table.items { width: 100%; border-collapse: collapse; margin-top: 8px; }
              table.items th { background: #1f4e79; color: white; text-align: left; padding: 6px; }
              table.items td { border-bottom: 1px solid #ddd; padding: 6px; vertical-align: top; }
              table.items tr:nth-child(even) td { background: #fafafa; }
              .num { text-align: right; }
              .totals { width: 40%; margin-left: auto; margin-top: 12px; border-collapse: collapse; }
              .totals td { padding: 4px 6px; }
              .totals tr.grand td { font-weight: bold; font-size: 14px; border-top: 2px solid #1f4e79; }
              footer { margin-top: 24px; font-size: 10px; color: #555; }
            </style></head>
            <body>
            <header><div><h1>INVOICE</h1><p>Example Supplier Ltd<br>12 Harbour Road<br>Springfield</p></div>
            <table class="meta"><tr><td>Invoice</td><td>2026-114</td></tr><tr><td>Date</td><td>29 October 2026</td></tr><tr><td>Due</td><td>28 November 2026</td></tr></table></header>
            <section class="parties"><div><strong>Bill to</strong><br>Example Customer Inc<br>400 Market Street<br>Riverside</div>
            <div><strong>Ship to</strong><br>Example Customer Inc<br>Warehouse 3<br>Riverside</div></section>
            <table class="items"><thead><tr><th>#</th><th>Description</th><th class="num">Qty</th><th class="num">Unit price</th><th class="num">Amount</th></tr></thead>
            <tbody>{{rows}}</tbody></table>
            <table class="totals"><tr><td>Subtotal</td><td class="num">12,480.00</td></tr><tr><td>VAT 20%</td><td class="num">2,496.00</td></tr><tr class="grand"><td>Total due</td><td class="num">14,976.00</td></tr></table>
            <footer><p>Payment by bank transfer within 30 days. Please quote the invoice number with your payment.</p></footer>
            </body></html>
            """;
    }

    /// <summary>A one page report: headings, paragraphs, a list and a data table.</summary>
    public static string Report()
    {
        var random = new Random(11);
        string Paragraph(int words)
        {
            var w = new string[words];
            for (int i = 0; i < words; i++) w[i] = Vocabulary[random.Next(Vocabulary.Length)];
            return string.Join(' ', w) + ".";
        }

        var rows = new System.Text.StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            rows.Append(System.FormattableString.Invariant($"<tr><td>Region {i + 1}</td><td>{random.Next(100, 999)}</td><td>{random.Next(100, 999)}</td><td>{random.NextDouble() * 20:F1}%</td></tr>"));
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>Quarterly report</title>
            <style>
              body { font-family: Georgia, "Times New Roman", serif; font-size: 11pt; line-height: 1.45; color: #1a1a1a; }
              h1 { font-family: Arial, sans-serif; font-size: 22pt; border-bottom: 1px solid #999; }
              h2 { font-family: Arial, sans-serif; font-size: 14pt; color: #444; margin-top: 18px; }
              p { text-align: justify; margin: 0 0 8px; }
              table { border-collapse: collapse; width: 100%; font-size: 10pt; }
              th, td { border: 1px solid #bbb; padding: 4px 8px; text-align: right; }
              th:first-child, td:first-child { text-align: left; }
              .note { background: #fff8e1; border-left: 4px solid #f0b400; padding: 8px 12px; }
            </style></head>
            <body>
            <h1>Quarterly operations report</h1>
            <p>{{Paragraph(90)}}</p>
            <h2>Highlights</h2>
            <ul><li>{{Paragraph(14)}}</li><li>{{Paragraph(18)}}</li><li>{{Paragraph(11)}}</li><li>{{Paragraph(16)}}</li></ul>
            <h2>Results by region</h2>
            <table><thead><tr><th>Region</th><th>Orders</th><th>Returns</th><th>Growth</th></tr></thead><tbody>{{rows}}</tbody></table>
            <h2>Discussion</h2>
            <p>{{Paragraph(110)}}</p>
            <div class="note"><p>{{Paragraph(40)}}</p></div>
            <p>{{Paragraph(80)}}</p>
            </body></html>
            """;
    }
}
