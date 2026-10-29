#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using System.Text;
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

File.WriteAllText("invoice-42.xml", """
    <?xml version="1.0" encoding="UTF-8"?>
    <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"
        xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100">
      <rsm:ExchangedDocument><ram:ID>42</ram:ID><ram:TypeCode>380</ram:TypeCode></rsm:ExchangedDocument>
    </rsm:CrossIndustryInvoice>
    """);

#region invoice
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

// The invoice XML from your billing system, in Cross Industry Invoice syntax.
byte[] xml = File.ReadAllBytes("invoice-42.xml");
PdfInvoice.Attach(doc, xml, PdfInvoiceProfile.EN16931);    // embedded as factur-x.xml
doc.Save("invoice-42.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });

PdfDocument check = PdfDocument.Open("invoice-42.pdf");
Console.WriteLine(string.Join(", ", check.Attachments.Select(a => a.Name)));
#endregion
