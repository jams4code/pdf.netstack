#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.1
using PdfNetStack.Html;

string html = """
    <html lang="en">
    <head>
      <title>Invoice 42</title>
      <style>
        @page { size: A4; margin: 2cm; @bottom-center { content: "Page " counter(page) " of " counter(pages) } }
        body { font-family: sans-serif; font-size: 10pt }
        table { width: 100%; border-collapse: collapse }
        th, td { padding: 6pt; border-bottom: 1px solid #CBD5E1; text-align: left }
        td.amount, th.amount { text-align: right }
        tfoot td { font-weight: bold }
      </style>
    </head>
    <body>
      <h1>Invoice 42</h1>
      <p>Issued 2026-10-29. Payment due within 30 days.</p>
      <table>
        <thead><tr><th>Item</th><th class="amount">Amount</th></tr></thead>
        <tbody>
          <tr><td>Consulting, 8 hours</td><td class="amount">800.00 EUR</td></tr>
          <tr><td>Support plan</td><td class="amount">200.00 EUR</td></tr>
        </tbody>
        <tfoot><tr><td>Total</td><td class="amount">1,000.00 EUR</td></tr></tfoot>
      </table>
    </body>
    </html>
    """;

byte[] pdf = HtmlConverter.ConvertToPdf(html);
File.WriteAllBytes("invoice-42.pdf", pdf);
