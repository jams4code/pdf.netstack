using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Signing;

namespace PdfNetStack.Tests;

/// <summary>
/// PDF/A files made by the library, for veraPDF in CI: set PDFNETSTACK_PDFA_SAMPLES to a folder
/// and the files are written there, named after the flavour veraPDF should check them against.
/// Without it they are only built and read back.
/// </summary>
[TestClass]
public sealed class PdfASampleTests
{
    public const string FolderVariable = "PDFNETSTACK_PDFA_SAMPLES";

    private static readonly byte[] InvoiceXml = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"><rsm:ExchangedDocument/></rsm:CrossIndustryInvoice>");

    private static PdfDocument Page(string title)
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = title;
        doc.Info.Author = "PdfNetStack";
        doc.Language = "en";
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString("AV To", PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf")), 24, PdfColor.Black, 72, 72);
            g.DrawRectangle(new PdfPen(PdfColor.Rgb(0.1, 0.3, 0.6), 1), PdfColor.Gray(0.9), 72, 120, 200, 80);
        }

        page.Annotations.AddLink(72, 72, 100, 30, new Uri("https://example.com/"));
        return doc;
    }

    /// <summary>Every sample: its file name (flavour first) and bytes.</summary>
    private static IEnumerable<(string Name, byte[] Data)> Samples()
    {
        yield return ("1b-plain.pdf", Page("PDF/A-1b").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA1B }));
        yield return ("1b-unoptimized.pdf", Page("PDF/A-1b, not optimized").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA1B, Optimize = false }));
        byte[] twoB = Page("PDF/A-2b").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA2B });
        yield return ("2b-plain.pdf", twoB);
        yield return ("2u-plain.pdf", Page("PDF/A-2u").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA2U }));
        yield return ("3u-plain.pdf", Page("PDF/A-3u").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3U, UseObjectStreams = false }));

        PdfDocument attached = Page("PDF/A-3b with an attachment");
        attached.Attachments.Add("data.csv", "a,b\n1,2\n"u8.ToArray(), "text/csv");
        yield return ("3b-attachment.pdf", attached.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));

        PdfDocument facturX = Page("Factur-X EN 16931");
        PdfInvoice.Attach(facturX, InvoiceXml, PdfInvoiceProfile.EN16931);
        byte[] invoice = facturX.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
        yield return ("3b-facturx-en16931.pdf", invoice);

        PdfDocument xrechnung = Page("ZUGFeRD XRECHNUNG");
        PdfInvoice.Attach(xrechnung, InvoiceXml, PdfInvoiceProfile.XRechnung);
        yield return ("3u-zugferd-xrechnung.pdf", xrechnung.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3U }));

        yield return ("4-plain.pdf", Page("PDF/A-4").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4 }));
        yield return ("4e-plain.pdf", Page("PDF/A-4e").ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4E }));

        PdfDocument nested = Page("PDF/A-4 with an embedded PDF/A-2b file");
        nested.Attachments.Add("earlier.pdf", twoB, "application/pdf");
        yield return ("4-embedded-pdfa.pdf", nested.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4 }));

        PdfDocument files = Page("PDF/A-4f with an attachment");
        files.Attachments.Add("data.csv", "a,b\n1,2\n"u8.ToArray(), "text/csv");
        yield return ("4f-attachment.pdf", files.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA4F }));

        // A signed invoice with a signature timestamp, validation data and a document timestamp (PAdES B-LTA).
        var pki = new TestPki();
        byte[] signed = PdfSigner.Sign(invoice, pki.Signer, new PdfSignatureOptions { Chain = [pki.Intermediate], Timestamp = pki.TimestampClient(), Reason = "Issued" });
        byte[] lt = PdfSigner.AddValidationData(signed, pki.GoodRevocation(), [pki.Root]);
        byte[] lta = PdfSigner.AddDocumentTimestamp(lt, pki.TimestampClient());
        yield return ("3b-facturx-signed-lta.pdf", PdfSigner.AddValidationData(lta, pki.GoodRevocation(), [pki.Root]));
    }

    [TestMethod]
    public void SamplesAreReadable()
    {
        string? folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        foreach ((string name, byte[] data) in Samples())
        {
            PdfDocument back = PdfDocument.Open(data);
            Assert.IsNotNull(back.Catalog["Metadata"], name);
            if (!string.IsNullOrEmpty(folder)) File.WriteAllBytes(Path.Combine(folder, name), data);
        }
    }
}
