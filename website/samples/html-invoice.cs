#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.1
// preview: html-invoice invoice.pdf 0.62
using PdfNetStack;
using PdfNetStack.Html;

// The template: in an application it lives next to its style sheet, fonts and logo.
File.WriteAllText("invoice.html", """
    <!doctype html>
    <html lang="en">
    <head>
      <title>Invoice 2026-114</title>
      <meta name="author" content="Example Studio">
      <style>
        @font-face { font-family: Inter; src: url(fonts/Inter-Regular.woff2) format("woff2") }
        @font-face { font-family: Inter; font-weight: 700; src: url(fonts/Inter-Bold.woff2) format("woff2") }
        @page { size: A4; margin: 18mm 18mm 22mm }
        :root { --brand: #1e3a8a; --muted: #64748b }
        body { font: 10pt/1.45 Inter, sans-serif; color: #0f172a }
        header { display: flex; justify-content: space-between; align-items: center;
                 padding-bottom: 12pt; border-bottom: 2pt solid var(--brand) }
        header img { width: 120px }
        h1 { margin: 0; font-size: 22pt; color: var(--brand) }
        .parties { display: grid; grid-template-columns: 1fr 1fr; gap: 24pt; margin: 18pt 0 }
        .parties h2 { margin: 0 0 4pt; font-size: 8pt; text-transform: uppercase;
                      letter-spacing: 0.08em; color: var(--muted) }
        table { width: 100%; border-collapse: collapse }
        th { text-align: left; font-size: 8pt; text-transform: uppercase; color: var(--muted);
             border-bottom: 1pt solid #cbd5e1; padding: 6pt 4pt }
        td { padding: 7pt 4pt; border-bottom: 0.5pt solid #e2e8f0 }
        td.num, th.num { text-align: right }
        .items tbody tr:nth-child(even) { background: #f8fafc }
        .total { margin-left: auto; width: 45%; margin-top: 12pt }
        .total td { border: 0 }
        .total tr:last-child td { font-weight: 700; font-size: 12pt; color: var(--brand);
                                  border-top: 1.5pt solid var(--brand) }
        .note { margin-top: 24pt; padding: 10pt 12pt; border-radius: 6pt;
                background: linear-gradient(90deg, #eff6ff, #ffffff); color: var(--muted) }
      </style>
    </head>
    <body>
      <header>
        <img src="logo.png" alt="Example Studio">
        <div><h1>Invoice 2026-114</h1>Issued 29 October 2026, due in 30 days</div>
      </header>
      <section class="parties">
        <div><h2>From</h2>Example Studio<br>12 Harbour Street<br>1000 Brussels</div>
        <div><h2>Bill to</h2>Example Trading Ltd<br>4 Market Square<br>75001 Paris</div>
      </section>
      <table class="items">
        <thead><tr><th>Description</th><th class="num">Qty</th><th class="num">Unit</th><th class="num">Amount</th></tr></thead>
        <tbody>
          <tr><td>Design workshop</td><td class="num">2</td><td class="num">450.00</td><td class="num">900.00</td></tr>
          <tr><td>Brand guidelines document</td><td class="num">1</td><td class="num">1,200.00</td><td class="num">1,200.00</td></tr>
          <tr><td>Icon set, 24 icons</td><td class="num">24</td><td class="num">15.00</td><td class="num">360.00</td></tr>
          <tr><td>Project management</td><td class="num">6</td><td class="num">65.00</td><td class="num">390.00</td></tr>
        </tbody>
      </table>
      <table class="total">
        <tr><td>Subtotal</td><td class="num">2,850.00 EUR</td></tr>
        <tr><td>VAT 21%</td><td class="num">598.50 EUR</td></tr>
        <tr><td>Total due</td><td class="num">3,448.50 EUR</td></tr>
      </table>
      <p class="note">Please pay by bank transfer to BE00 0000 0000 0000, quoting the invoice number.</p>
    </body>
    </html>
    """);

#region file
// Relative URLs (fonts, logo) resolve against the file's folder, and only that folder is read.
PdfDocument doc = HtmlConverter.ConvertFileToDocument("invoice.html");
doc.Save("invoice.pdf");
#endregion

Console.WriteLine($"{doc.Pages.Count} page(s), title \"{doc.Info.Title}\", language {doc.Language}");
