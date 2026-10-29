#:project ../../src/PdfNetStack.Html/PdfNetStack.Html.csproj
// since: 1.1
using System.Text;
using PdfNetStack;
using PdfNetStack.Fonts;
using PdfNetStack.Html;

string html = """
    <html lang="en"><head><title>Delivery note 881</title></head>
    <body>
      <h1>Delivery note 881</h1>
      <p>Three parcels, delivered on 29 October 2026.</p>
      <h2>Contents</h2>
      <ul><li>Printed manuals, 40 copies</li><li>Spare cables, 12 units</li></ul>
    </body></html>
    """;

#region options
var fonts = new PdfFontCollection();
fonts.AddDirectory("fonts");                               // .ttf, .otf, .woff and .woff2 files

var options = new HtmlConverterOptions
{
    BaseUri = new Uri(Path.GetFullPath(".") + Path.DirectorySeparatorChar),   // relative URLs and file access
    PageStyle = "size: A5; margin: 15mm",                  // applies before the document's @page rules
    StyleSheet = "body { font-family: Inter, sans-serif; font-size: 10pt }",  // a house style
    Fonts = fonts,                                         // looked up before installed fonts
    UseSystemFonts = false,                                // same output on every machine
    Bookmarks = true,                                      // headings become bookmarks (default)
    Tagged = true,                                         // structure tree for screen readers (default)
};
#endregion

#region document
PdfDocument doc = HtmlConverter.ConvertToDocument(html, options);

// The result is a PdfDocument like any other: attach, sign, encrypt, merge or stamp it.
doc.Info.Author = "Logistics";
doc.Attachments.Add("delivery.xml", Encoding.UTF8.GetBytes("<delivery id=\"881\"/>"), "text/xml");
doc.Save("delivery-note.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3U });
#endregion

#region stream
using (FileStream output = File.Create("delivery-note-plain.pdf"))
{
    HtmlConverter.ConvertToPdf(html, output, options);
}
#endregion

Console.WriteLine($"{doc.Pages.Count} page(s), {doc.Outlines.Count} bookmark(s), {doc.Attachments.Count} attachment(s)");
