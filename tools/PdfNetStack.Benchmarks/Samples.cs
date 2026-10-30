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

    /// <summary>
    /// A 20 page invoice: the one page invoice's header and parties, then 640 line items in a table
    /// whose header repeats on every page, a footer row, totals and terms.
    /// </summary>
    public static string LongInvoice()
    {
        var rows = new System.Text.StringBuilder();
        var random = new Random(23);
        string[] services = ["Maintenance service", "Spare part", "Inspection visit", "Calibration", "Travel and expenses", "Remote support"];
        double subtotal = 0;
        for (int i = 1; i <= 640; i++)
        {
            int quantity = 1 + random.Next(9);
            double price = Math.Round(5 + (random.NextDouble() * 400), 2);
            subtotal += quantity * price;
            string note = i % 3 == 0 ? $"<br><small>Reference SV-{1000 + i}, ordered {1 + (i % 28)} September 2026</small>" : "";
            rows.Append(System.FormattableString.Invariant(
                $"<tr><td>{i}</td><td>{services[i % services.Length]}, line item {i}{note}</td><td class=\"num\">{quantity}</td><td class=\"num\">{price:N2}</td><td class=\"num\">{quantity * price:N2}</td></tr>"));
        }

        string Money(double v) => v.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>Invoice 2026-115</title>
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
              table.items td { border-bottom: 1px solid #ddd; padding: 5px 6px; vertical-align: top; }
              table.items tr:nth-child(even) td { background: #fafafa; }
              table.items tfoot td { font-weight: bold; border-top: 2px solid #1f4e79; }
              .num { text-align: right; }
              .totals { width: 40%; margin-left: auto; margin-top: 12px; border-collapse: collapse; break-inside: avoid; }
              .totals td { padding: 4px 6px; }
              .totals tr.grand td { font-weight: bold; font-size: 14px; border-top: 2px solid #1f4e79; }
              footer { margin-top: 24px; font-size: 10px; color: #555; }
              footer ol { padding-left: 18px; }
            </style></head>
            <body>
            <header><div><h1>INVOICE</h1><p>Example Supplier Ltd<br>12 Harbour Road<br>Springfield</p></div>
            <table class="meta"><tr><td>Invoice</td><td>2026-115</td></tr><tr><td>Date</td><td>30 October 2026</td></tr><tr><td>Due</td><td>29 November 2026</td></tr></table></header>
            <section class="parties"><div><strong>Bill to</strong><br>Example Customer Inc<br>400 Market Street<br>Riverside</div>
            <div><strong>Ship to</strong><br>Example Customer Inc<br>Warehouse 3<br>Riverside</div></section>
            <table class="items"><thead><tr><th>#</th><th>Description</th><th class="num">Qty</th><th class="num">Unit price</th><th class="num">Amount</th></tr></thead>
            <tfoot><tr><td></td><td>Carried to totals</td><td></td><td></td><td class="num">{{Money(subtotal)}}</td></tr></tfoot>
            <tbody>{{rows}}</tbody></table>
            <table class="totals"><tr><td>Subtotal</td><td class="num">{{Money(subtotal)}}</td></tr><tr><td>VAT 20%</td><td class="num">{{Money(subtotal * 0.2)}}</td></tr><tr class="grand"><td>Total due</td><td class="num">{{Money(subtotal * 1.2)}}</td></tr></table>
            <footer><p>Terms:</p><ol><li>Payment by bank transfer within 30 days.</li><li>Please quote the invoice number with your payment.</li><li>Late payments carry interest at the statutory rate.</li></ol></footer>
            </body></html>
            """;
    }

    /// <summary>
    /// A report of about a dozen pages: eight sections, each with headings, paragraphs with inline
    /// markup, a bulleted and a numbered list, a raster image and an SVG chart, a three column grid
    /// of cards, a data table and a note.
    /// </summary>
    public static string RichReport()
    {
        var random = new Random(31);
        string Words(int count)
        {
            var w = new string[count];
            for (int i = 0; i < count; i++) w[i] = Vocabulary[random.Next(Vocabulary.Length)];
            w[0] = char.ToUpperInvariant(w[0][0]) + w[0][1..];
            return string.Join(' ', w);
        }

        string Paragraph(int words) => $"<p>{Words(words / 3)} <em>{Words(words / 6)}</em> {Words(words / 6)} <strong>{Words(4)}</strong> {Words(words / 3)}.</p>";
        string png = "data:image/png;base64," + Convert.ToBase64String(GradientPng(240, 120));
        var body = new System.Text.StringBuilder();
        for (int s = 1; s <= 8; s++)
        {
            body.Append(System.FormattableString.Invariant($"<h2 id=\"s{s}\">{s}. {Words(4)}</h2>"));
            body.Append(Paragraph(110)).Append(Paragraph(80));
            body.Append("<h3>Key points</h3><ul>");
            for (int i = 0; i < 5; i++) body.Append("<li>").Append(Words(10 + random.Next(12))).Append("</li>");
            body.Append("</ul><figure><img src=\"").Append(png).Append("\" width=\"240\" height=\"120\" alt=\"Trend\"><figcaption>Figure ").Append(s).Append(": ").Append(Words(8)).Append("</figcaption></figure>");
            body.Append("<div class=\"grid\">");
            for (int c = 0; c < 6; c++)
            {
                body.Append(System.FormattableString.Invariant($"<div class=\"card\"><h4>{Words(2)}</h4><p class=\"figure\">{random.Next(100, 999)}</p><p>{Words(12)}</p></div>"));
            }

            body.Append("</div>");
            body.Append(Chart(random));
            body.Append("<h3>Actions</h3><ol>");
            for (int i = 0; i < 4; i++) body.Append("<li>").Append(Words(14 + random.Next(10))).Append("</li>");
            body.Append("</ol><table><thead><tr><th>Region</th><th>Orders</th><th>Returns</th><th>Growth</th></tr></thead><tbody>");
            for (int r = 0; r < 6; r++)
            {
                body.Append(System.FormattableString.Invariant($"<tr><td>Region {r + 1}</td><td>{random.Next(100, 999)}</td><td>{random.Next(10, 99)}</td><td>{random.NextDouble() * 20:F1}%</td></tr>"));
            }

            body.Append("</tbody></table><div class=\"note\">").Append(Paragraph(50)).Append("</div>");
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>Annual operations review</title>
            <style>
              @page { margin: 20mm; @top-right { content: string(section); font-size: 8pt; color: #777; } @bottom-center { content: counter(page); font-size: 8pt; } }
              body { font-family: Georgia, "Times New Roman", serif; font-size: 10.5pt; line-height: 1.45; color: #1a1a1a; }
              h1 { font-family: Arial, sans-serif; font-size: 24pt; border-bottom: 2px solid #335; margin-bottom: 4px; }
              h2 { font-family: Arial, sans-serif; font-size: 15pt; color: #335; margin-top: 22px; string-set: section content(); break-after: avoid; }
              h3 { font-family: Arial, sans-serif; font-size: 11.5pt; color: #555; margin: 12px 0 4px; }
              p { text-align: justify; margin: 0 0 8px; }
              ul, ol { margin: 4px 0 8px; padding-left: 22px; }
              li { margin-bottom: 2px; }
              figure { margin: 10px 0; text-align: center; break-inside: avoid; }
              figcaption { font-size: 9pt; color: #666; font-style: italic; }
              .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; margin: 10px 0; }
              .card { border: 1px solid #ccd; border-radius: 6px; padding: 8px 10px; background: #f7f8fc; break-inside: avoid; }
              .card h4 { margin: 0 0 4px; font-family: Arial, sans-serif; font-size: 10pt; color: #335; }
              .card .figure { font-size: 18pt; font-weight: bold; text-align: left; margin: 0; color: #1f4e79; }
              .card p { font-size: 9pt; text-align: left; }
              .chart { display: block; margin: 8px auto; }
              table { border-collapse: collapse; width: 100%; font-size: 9.5pt; margin: 8px 0; }
              th, td { border: 1px solid #bbb; padding: 3px 8px; text-align: right; }
              th { background: #eef; }
              th:first-child, td:first-child { text-align: left; }
              .note { background: #fff8e1; border-left: 4px solid #f0b400; padding: 8px 12px; margin: 10px 0; }
              nav ol { columns: 2; }
            </style></head>
            <body>
            <h1>Annual operations review</h1>
            <nav><ol>{{string.Concat(Enumerable.Range(1, 8).Select(i => $"<li><a href=\"#s{i}\">Section {i}</a></li>"))}}</ol></nav>
            {{body}}
            </body></html>
            """;
    }

    /// <summary>An SVG bar chart of twelve months, inline in the HTML.</summary>
    private static string Chart(Random random)
    {
        var svg = new System.Text.StringBuilder("<svg class=\"chart\" width=\"420\" height=\"140\" viewBox=\"0 0 420 140\"><rect x=\"0\" y=\"0\" width=\"420\" height=\"140\" fill=\"#fbfbfd\" stroke=\"#ccd\"/>");
        for (int m = 0; m < 12; m++)
        {
            int h = 20 + random.Next(100);
            svg.Append(System.FormattableString.Invariant($"<rect x=\"{12 + (m * 34)}\" y=\"{130 - h}\" width=\"24\" height=\"{h}\" fill=\"#4a6fa5\"/>"));
        }

        return svg.Append("<polyline points=\"12,100 80,80 150,90 220,50 290,60 360,30 400,40\" fill=\"none\" stroke=\"#c33\" stroke-width=\"2\"/></svg>").ToString();
    }

    /// <summary>A small RGB gradient as a PNG, written here so the sample needs no image file.</summary>
    private static byte[] GradientPng(int width, int height)
    {
        var raw = new byte[height * ((width * 3) + 1)];
        for (int y = 0, i = 0; y < height; y++)
        {
            raw[i++] = 0;
            for (int x = 0; x < width; x++)
            {
                raw[i++] = (byte)(255 * x / width);
                raw[i++] = (byte)(255 * y / height);
                raw[i++] = (byte)(128 + (64 * Math.Sin(x / 12.0)));
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            png.Write(BigEndian((uint)data.Length));
            png.Write(typeBytes);
            png.Write(data);
            png.Write(BigEndian(Crc32([.. typeBytes, .. data])));
        }

        var header = new byte[13];
        BigEndian((uint)width).CopyTo(header, 0);
        BigEndian((uint)height).CopyTo(header, 4);
        header[8] = 8;
        header[9] = 2;
        Chunk("IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            Chunk("IDAT", compressed.ToArray());
        }

        Chunk("IEND", []);
        return png.ToArray();

        static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }

            return ~crc;
        }
    }

    /// <summary>
    /// A 50 page text: chapters of justified paragraphs with emphasis, links and the odd quotation,
    /// in a serif face, about 28,000 words.
    /// </summary>
    public static string LongText()
    {
        var random = new Random(53);
        string Words(int count)
        {
            var w = new string[count];
            for (int i = 0; i < count; i++) w[i] = Vocabulary[random.Next(Vocabulary.Length)];
            return string.Join(' ', w);
        }

        var body = new System.Text.StringBuilder();
        for (int chapter = 1; chapter <= 10; chapter++)
        {
            body.Append(System.FormattableString.Invariant($"<h2 id=\"c{chapter}\">Chapter {chapter}</h2>"));
            for (int p = 0; p < 33; p++)
            {
                int n = 50 + random.Next(60);
                string first = Words(n / 2);
                first = char.ToUpperInvariant(first[0]) + first[1..];
                body.Append(random.Next(8) == 0
                    ? $"<blockquote>{first}.</blockquote>"
                    : $"<p>{first} <em>{Words(3)}</em> {Words(n / 4)} <a href=\"#c{chapter}\">{Words(2)}</a> {Words(n / 4)}.</p>");
            }
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>Long text</title>
            <style>
              @page { size: A4; margin: 25mm 22mm; @bottom-center { content: counter(page); font-size: 9pt; } }
              body { font-family: "Times New Roman", serif; font-size: 11pt; line-height: 1.5; }
              h2 { font-size: 16pt; margin: 24px 0 10px; break-before: page; }
              h2:first-child { break-before: auto; }
              p { text-align: justify; margin: 0; text-indent: 1.5em; }
              blockquote { margin: 8px 30px; font-style: italic; }
              a { color: #224; text-decoration: none; }
            </style></head>
            <body>{{body}}</body></html>
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
