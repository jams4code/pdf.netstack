#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: pdfa-invoice invoice-pdfa3.pdf 0.25
using System.Text;
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

// The invoice data in UN/CEFACT Cross Industry Invoice syntax, shortened for the sample. Real
// invoices come from your billing system; PdfNetStack checks the XML is well formed, not that it
// is a valid invoice for the profile.
byte[] xml = Encoding.UTF8.GetBytes("""
    <?xml version="1.0" encoding="UTF-8"?>
    <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"
        xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100">
      <rsm:ExchangedDocumentContext>
        <ram:GuidelineSpecifiedDocumentContextParameter>
          <ram:ID>urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic</ram:ID>
        </ram:GuidelineSpecifiedDocumentContextParameter>
      </rsm:ExchangedDocumentContext>
      <rsm:ExchangedDocument><ram:ID>2026-114</ram:ID><ram:TypeCode>380</ram:TypeCode></rsm:ExchangedDocument>
    </rsm:CrossIndustryInvoice>
    """);

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

// factur-x.xml, related as Alternative for the BASIC profile, described in the XMP on save.
PdfInvoice.Attach(doc, xml, PdfInvoiceProfile.Basic);
doc.Save("invoice-pdfa3.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
#endregion

#region check
PdfDocument saved = PdfDocument.Open("invoice-pdfa3.pdf");
PdfAttachment attached = saved.Attachments[PdfInvoice.FacturXFileName]!;
string relationship = (saved.Resolve(attached.Dictionary["AFRelationship"]) as PdfName)?.Value ?? "none";
string xmp = Encoding.UTF8.GetString(saved.GetStreamData((PdfStream)saved.Resolve(saved.Catalog["Metadata"])!));
Console.WriteLine($"{attached.FileName}: {attached.GetData().Length} bytes, relationship {relationship}");
Console.WriteLine($"Factur-X metadata: {xmp.Contains("<fx:ConformanceLevel>BASIC</fx:ConformanceLevel>", StringComparison.Ordinal)}");
#endregion

#region relationship
// Other attachments are declared Unspecified unless they say otherwise. Set the relationship on
// the attachment's dictionary through the object model before saving.
PdfDocument data = PdfDocument.Open("invoice-pdfa3.pdf");
data.Attachments.Add("timesheet.csv", Encoding.UTF8.GetBytes("day,hours\nMonday,6\n"), "text/csv", "Hours billed")
    .Dictionary.Set("AFRelationship", new PdfName("Supplement"));
data.Save("invoice-with-timesheet.pdf", new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
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
