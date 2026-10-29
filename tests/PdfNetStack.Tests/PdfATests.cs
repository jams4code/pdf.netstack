using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class PdfATests
{
    private static PdfOpenTypeFont Font() => PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf"));

    private static PdfDocument Document(Action<PdfGraphics>? draw = null)
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = "Invoice & <Receipt>";
        doc.Info.Author = "JADEV";
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawString("AV To", Font(), 18, PdfColor.Black, 72, 72);
        draw?.Invoke(g);
        return doc;
    }

    private static PdfDocument Save(PdfDocument doc, PdfConformance conformance) =>
        PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = conformance }));

    [TestMethod]
    public void PdfA3bCarriesMetadataAndAnOutputIntent()
    {
        PdfDocument back = Save(Document(), PdfConformance.PdfA3B);

        var metadata = (PdfStream)back.Resolve(back.Catalog["Metadata"])!;
        Assert.AreEqual("XML", metadata.Dictionary.GetName("Subtype"));
        Assert.IsNull(metadata.Dictionary["Filter"]);
        string xmp = Encoding.UTF8.GetString(back.GetStreamData(metadata));
        StringAssert.Contains(xmp, "<pdfaid:part>3</pdfaid:part>");
        StringAssert.Contains(xmp, "<pdfaid:conformance>B</pdfaid:conformance>");
        StringAssert.Contains(xmp, "Invoice &amp; &lt;Receipt&gt;");
        StringAssert.Contains(xmp, "<rdf:li>JADEV</rdf:li>");

        // The dates in the information dictionary and the XMP must agree.
        DateTimeOffset created = back.Info.CreationDate!.Value;
        StringAssert.Contains(xmp, "<xmp:CreateDate>" + created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z</xmp:CreateDate>");
        Assert.AreEqual("PdfNetStack", back.Info.Producer);

        var intent = (PdfDictionary)back.Resolve(((PdfArray)back.Resolve(back.Catalog["OutputIntents"])!)[0])!;
        Assert.AreEqual("GTS_PDFA1", intent.GetName("S"));
        var profile = (PdfStream)back.Resolve(intent["DestOutputProfile"])!;
        Assert.AreEqual(3, ((PdfNumber)profile.Dictionary["N"]!).ToDouble());
        byte[] icc = back.GetStreamData(profile);
        Assert.AreEqual("acsp", Encoding.ASCII.GetString(icc, 36, 4));
        Assert.AreEqual("mntr", Encoding.ASCII.GetString(icc, 12, 4));
    }

    [TestMethod]
    public void PdfA2uMarksUnicodeConformance()
    {
        PdfDocument back = Save(Document(), PdfConformance.PdfA2U);
        string xmp = Encoding.UTF8.GetString(back.GetStreamData((PdfStream)back.Resolve(back.Catalog["Metadata"])!));
        StringAssert.Contains(xmp, "<pdfaid:part>2</pdfaid:part>");
        StringAssert.Contains(xmp, "<pdfaid:conformance>U</pdfaid:conformance>");
    }

    [TestMethod]
    public void StandardFontsAreRefusedWithAClearReason()
    {
        PdfDocument doc = Document(g => g.DrawString("x", PdfStandardFont.Helvetica, 12, PdfColor.Black, 10, 10));
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA2B }));
        StringAssert.Contains(e.Message, "Font Helvetica is not embedded");
        StringAssert.Contains(e.Message, "PdfOpenTypeFont");
    }

    [TestMethod]
    public void EveryProblemIsListedAtOnce()
    {
        PdfDocument doc = Document(g =>
        {
            g.DrawString("x", PdfStandardFont.TimesRoman, 12, PdfColor.Black, 10, 10);
            g.DrawRectangle(null, PdfColor.Cmyk(0, 1, 1, 0), 0, 0, 10, 10);
        });
        doc.Security.Encrypt("u", "o");
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));
        StringAssert.Contains(e.Message, "encrypted");
        StringAssert.Contains(e.Message, "DeviceCMYK");
        StringAssert.Contains(e.Message, "Times-Roman");
    }

    [TestMethod]
    public void AttachmentsNeedPdfA3()
    {
        PdfDocument doc = Document();
        doc.Attachments.Add("invoice.xml", "<invoice/>"u8.ToArray(), "text/xml");
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA2B }));
        StringAssert.Contains(e.Message, "PDF/A-3");

        PdfDocument back = Save(doc, PdfConformance.PdfA3B);
        var af = (PdfArray)back.Resolve(back.Catalog["AF"])!;
        var spec = (PdfDictionary)back.Resolve(af[0])!;
        Assert.AreEqual("Unspecified", spec.GetName("AFRelationship"));
        PdfAttachment attachment = back.Attachments.Single();
        Assert.AreEqual("text/xml", attachment.MimeType);
        var file = (PdfStream)back.Resolve(((PdfDictionary)back.Resolve(spec["EF"])!)["F"])!;
        Assert.IsNotNull(((PdfDictionary)back.Resolve(file.Dictionary["Params"])!)["ModDate"]);
    }

    [TestMethod]
    public void AnnotationsArePrintable()
    {
        PdfDocument doc = Document();
        PdfAnnotation link = doc.Pages[0].Annotations.AddLink(0, 0, 50, 10, new Uri("https://example.com/"));
        link.Dictionary.Set("F", PdfNumber.FromInteger(2));
        PdfDocument back = Save(doc, PdfConformance.PdfA3B);
        Assert.AreEqual(4, ((PdfNumber)back.Pages[0].Annotations[0].Dictionary["F"]!).ToDouble());
    }

    [TestMethod]
    public void SavingTwiceKeepsOneOutputIntent()
    {
        PdfDocument doc = Document();
        doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B });
        PdfDocument back = Save(doc, PdfConformance.PdfA3B);
        Assert.HasCount(1, (PdfArray)back.Resolve(back.Catalog["OutputIntents"])!);
    }
}
