#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using System.Text;
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfFont font = PdfOpenTypeFont.Load("fonts/Inter-Regular.woff2");   // PDF/A needs embedded fonts
PdfDocument doc = PdfDocument.Create();
doc.Info.Title = "Invoice 42";
doc.Language = "en";
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())
{
    g.DrawString("Invoice 42", font, 24, PdfColor.Black, 72, 100);
    g.DrawString("Total: 1,210.00 EUR", font, 12, PdfColor.Black, 72, 130);
}

byte[] xml = Encoding.UTF8.GetBytes("<Invoice><ID>42</ID><Total currency=\"EUR\">1210.00</Total></Invoice>");
doc.Attachments.Add("factur-x.xml", xml, "text/xml", "Invoice data");
doc.Save("invoice-pdfa3.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });

PdfDocument check = PdfDocument.Open("invoice-pdfa3.pdf");
Console.WriteLine(string.Join(", ", check.Attachments.Select(a => a.Name)));
