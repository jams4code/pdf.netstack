#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: pdfa-invoice invoice-pdfa3.pdf 0.25
using System.Text;
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

byte[] xml = Encoding.UTF8.GetBytes(
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Invoice><ID>2026-114</ID><Total currency=\"EUR\">1250.00</Total></Invoice>\n");

#region invoice
// PDF/A embeds every font: draw with an OpenType font, not the standard 14.
PdfFont regular = PdfOpenTypeFont.Load("fonts/Inter-Regular.woff2");
PdfFont bold = PdfOpenTypeFont.Load("fonts/Inter-Bold.woff2");

PdfDocument doc = PdfDocument.Create();
doc.Info.Title = "Invoice 2026-114";
doc.Info.Author = "Accounts";
doc.Language = "en";
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())
{
    g.DrawString("Invoice 2026-114", bold, 24, PdfColor.Black, 56, 90);
    g.DrawString("Total due: 1,250.00 EUR", regular, 12, PdfColor.Black, 56, 130);
}

doc.Attachments.Add("invoice.xml", xml, "text/xml", "Structured invoice data");
doc.Save("invoice-pdfa3.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
#endregion

#region relationship
// PDF/A-3 declares how each attachment relates to the document. The default is Unspecified;
// set another value through the low level dictionary before saving.
PdfDocument data = PdfDocument.Open("invoice-pdfa3.pdf");
data.Attachments["invoice.xml"]!.Dictionary.Set("AFRelationship", new PdfName("Alternative"));
data.Save("invoice-alternative.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
#endregion

#region problems
// What cannot be repaired is listed together, in one exception.
PdfDocument plain = PdfDocument.Open("report.pdf");
plain.Attachments.Add("notes.txt", Encoding.UTF8.GetBytes("notes"), "text/plain");
try
{
    plain.Save("report-pdfa2.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA2B });
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message);
}
#endregion
