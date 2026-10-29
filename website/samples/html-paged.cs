#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.1
// preview: html-paged report.pdf
using System.Text;
using PdfNetStack;
using PdfNetStack.Html;

#region css
const string Css = """
    @page {
      size: A4;
      margin: 22mm 18mm 24mm;
      font-family: sans-serif;
      @top-left { content: string(chapter); font-size: 8pt; color: #64748b }
      @top-right { content: element(brand) }
      @bottom-center { content: "Page " counter(page) " of " counter(pages); font-size: 8pt }
    }
    @page :first { @top-left { content: none } }
    @page :left { @bottom-left { content: "Quarterly report"; font-size: 8pt; color: #64748b } }
    @page :right { @bottom-right { content: "Quarterly report"; font-size: 8pt; color: #64748b } }
    @page wide { size: A4 landscape }

    body { font-family: sans-serif; font-size: 10pt }
    .brand { position: running(brand); text-align: right; font-size: 8pt; font-weight: bold; color: #1e3a8a }
    .draft { position: fixed; z-index: -1; top: 110mm; left: 25mm; font-size: 90pt;
             color: #e0e7ff; transform: rotate(-30deg) }

    h1 { string-set: chapter content(text); color: #1e3a8a }
    .chapter + .chapter { break-before: page }
    .annex { page: wide }
    h2 { break-after: avoid }
    p { orphans: 3; widows: 3 }

    table { width: 100%; border-collapse: collapse; font-size: 9pt }
    thead th { background: #1e3a8a; color: white; text-align: left; padding: 4pt 6pt }
    td { padding: 3pt 6pt; border-bottom: 0.5pt solid #cbd5e1 }
    tr { break-inside: avoid }
    """;
#endregion

// A report with two chapters, a table that runs over several pages, and a landscape annex.
var html = new StringBuilder($"<html lang=\"en\"><head><title>Quarterly report</title><style>{Css}</style></head><body>");
html.Append("<div class=\"brand\">Example Studio</div><div class=\"draft\">DRAFT</div>");
html.Append("<section class=\"chapter\"><h1>Sales by region</h1><p>Orders per region for the third quarter, in units.</p>");
html.Append("<table><thead><tr><th>Region</th><th>July</th><th>August</th><th>September</th></tr></thead><tbody>");
for (int i = 1; i <= 70; i++)
{
    html.Append($"<tr><td>Region {i}</td><td>{100 + (i * 7 % 50)}</td><td>{120 + (i * 11 % 60)}</td><td>{90 + (i * 13 % 70)}</td></tr>");
}
html.Append("</tbody></table></section>");
html.Append("<section class=\"chapter\"><h1>Outlook</h1>");
for (int i = 1; i <= 6; i++)
{
    html.Append($"<h2>Topic {i}</h2><p>{string.Concat(Enumerable.Repeat("The next quarter brings new orders, steady margins and a wider range of products. ", 6))}</p>");
}
html.Append("</section>");
html.Append("<div class=\"back-matter\"><section class=\"annex\"><h1>Annex</h1><p>This section is laid out on a landscape page of its own.</p></section></div>");
html.Append("</body></html>");

PdfDocument doc = HtmlConverter.ConvertToDocument(html.ToString());
doc.Save("report.pdf");

foreach (PdfPage page in doc.Pages)
{
    Console.WriteLine($"{page.Width:0} x {page.Height:0} pt");
}
