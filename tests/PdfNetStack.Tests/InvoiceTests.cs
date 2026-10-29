using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Factur-X and ZUGFeRD hybrid invoices on PDF/A-3.</summary>
[TestClass]
public sealed class InvoiceTests
{
    private static readonly byte[] Xml = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"><rsm:ExchangedDocument/></rsm:CrossIndustryInvoice>");

    private static PdfDocument Document()
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = "Invoice 2026-001";
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawString("AV To", PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf")), 18, PdfColor.Black, 72, 72);
        return doc;
    }

    private static string Xmp(PdfDocument doc) => Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Resolve(doc.Catalog["Metadata"])!));

    [TestMethod]
    public void En16931InvoiceIsAnAlternativeWithExtensionSchema()
    {
        PdfDocument doc = Document();
        PdfAttachment attachment = PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.EN16931);
        Assert.AreEqual("factur-x.xml", attachment.Name);
        PdfDocument back = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));

        string xmp = Xmp(back);
        StringAssert.Contains(xmp, "xmlns:fx=\"urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#\"");
        StringAssert.Contains(xmp, "<fx:DocumentType>INVOICE</fx:DocumentType>");
        StringAssert.Contains(xmp, "<fx:DocumentFileName>factur-x.xml</fx:DocumentFileName>");
        StringAssert.Contains(xmp, "<fx:Version>1.0</fx:Version>");
        StringAssert.Contains(xmp, "<fx:ConformanceLevel>EN 16931</fx:ConformanceLevel>");
        StringAssert.Contains(xmp, "<pdfaSchema:prefix>fx</pdfaSchema:prefix>");
        foreach (string property in new[] { "DocumentFileName", "DocumentType", "Version", "ConformanceLevel" }) StringAssert.Contains(xmp, "<pdfaProperty:name>" + property + "</pdfaProperty:name>");

        // The XMP is well formed XML.
        var xml = new System.Xml.XmlDocument { XmlResolver = null };
        xml.LoadXml(xmp.Substring(xmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal), xmp.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12 - xmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal)));

        PdfAttachment saved = back.Attachments.Single();
        Assert.AreEqual("text/xml", saved.MimeType);
        Assert.AreEqual("Alternative", saved.Dictionary.GetName("AFRelationship"));
        CollectionAssert.AreEqual(Xml, saved.GetData());
        var af = (PdfArray)back.Resolve(back.Catalog["AF"])!;
        Assert.AreSame(saved.Dictionary, back.Resolve(af[0]));
    }

    [TestMethod]
    public void MinimumAndBasicWlAreData()
    {
        foreach ((PdfInvoiceProfile profile, string level) in new[] { (PdfInvoiceProfile.Minimum, "MINIMUM"), (PdfInvoiceProfile.BasicWL, "BASIC WL") })
        {
            PdfDocument doc = Document();
            PdfInvoice.Attach(doc, Xml, profile);
            PdfDocument back = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3U }));
            Assert.AreEqual("Data", back.Attachments.Single().Dictionary.GetName("AFRelationship"));
            StringAssert.Contains(Xmp(back), "<fx:ConformanceLevel>" + level + "</fx:ConformanceLevel>");
        }
    }

    [TestMethod]
    public void NamesAreChecked()
    {
        PdfDocument doc = Document();
        Assert.ThrowsExactly<ArgumentException>(() => PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.EN16931, new PdfInvoiceOptions { FileName = "invoice.xml" }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.Extended, new PdfInvoiceOptions { FileName = "xrechnung.xml" }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.Basic, new PdfInvoiceOptions { Version = "one" }));
        Assert.IsEmpty(doc.Attachments);

        PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.XRechnung, new PdfInvoiceOptions { FileName = "xrechnung.xml", Version = "3.0", Relationship = PdfFileRelationship.Source });
        PdfDocument back = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));
        string xmp = Xmp(back);
        StringAssert.Contains(xmp, "<fx:ConformanceLevel>XRECHNUNG</fx:ConformanceLevel>");
        StringAssert.Contains(xmp, "<fx:DocumentFileName>xrechnung.xml</fx:DocumentFileName>");
        StringAssert.Contains(xmp, "<fx:Version>3.0</fx:Version>");
        Assert.AreEqual("Source", back.Attachments.Single().Dictionary.GetName("AFRelationship"));
    }

    [TestMethod]
    public void OnlyOneInvoiceAttachmentRemains()
    {
        PdfDocument doc = Document();
        doc.Attachments.Add("terms.txt", "terms"u8.ToArray(), "text/plain");
        PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.XRechnung, new PdfInvoiceOptions { FileName = "xrechnung.xml" });
        PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.XRechnung);
        CollectionAssert.AreEquivalent(new[] { "factur-x.xml", "terms.txt" }, doc.Attachments.Select(a => a.Name).ToArray());
    }

    [TestMethod]
    public void BadXmlIsRefused()
    {
        PdfDocument doc = Document();
        Assert.ThrowsExactly<ArgumentException>(() => PdfInvoice.Attach(doc, "<invoice><open></invoice>"u8.ToArray(), PdfInvoiceProfile.Basic));
        Assert.ThrowsExactly<ArgumentException>(() => PdfInvoice.Attach(doc, [], PdfInvoiceProfile.Basic));

        // Entities are never expanded: a document type declaration is refused outright.
        byte[] entities = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x \"xx\"><!ENTITY y \"&x;&x;&x;\">]><a>&y;</a>");
        Assert.ThrowsExactly<ArgumentException>(() => PdfInvoice.Attach(doc, entities, PdfInvoiceProfile.Basic));
    }

    [TestMethod]
    public void InvoicesMustBePdfA3()
    {
        PdfDocument doc = Document();
        PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.Basic);
        StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => doc.ToArray()).Message, "PDF/A-3");
        StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA2B })).Message, "PDF/A-3");
    }

    [TestMethod]
    public void SaveChecksTheAttachment()
    {
        PdfDocument doc = Document();
        PdfAttachment attachment = PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.Basic);
        attachment.Dictionary.Set("AFRelationship", new PdfName("Supplement"));
        StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B })).Message, "Supplement");

        doc.Attachments.Remove("factur-x.xml");
        StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B })).Message, "missing");
    }

    [TestMethod]
    public void ReopenedInvoicesKeepTheirMetadata()
    {
        PdfDocument doc = Document();
        PdfInvoice.Attach(doc, Xml, PdfInvoiceProfile.Extended);
        PdfDocument reopened = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));
        reopened.Info.Subject = "Edited";
        PdfDocument back = PdfDocument.Open(reopened.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));
        string xmp = Xmp(back);
        StringAssert.Contains(xmp, "<fx:ConformanceLevel>EXTENDED</fx:ConformanceLevel>");
        StringAssert.Contains(xmp, "Edited");
        Assert.AreEqual(1, CountOf(xmp, "<fx:DocumentType>"));
    }

    [TestMethod]
    public void HostileMetadataIsReadInLinearTime()
    {
        // An unclosed property followed by a long run of spaces made a backtracking pattern quadratic.
        string spaces = new(' ', 300_000);
        string xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:fx=\"urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#\">"
            + "<fx:ConformanceLevel>" + spaces + "<fx:DocumentFileName>" + spaces;
        PdfDocument doc = Document();
        doc.Catalog.Set("Metadata", doc.AddObject(PdfStream.FromData(Encoding.UTF8.GetBytes(xmp))));
        PdfDocument reopened = PdfDocument.Open(doc.ToArray());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        PdfDocument back = PdfDocument.Open(reopened.ToArray(new PdfSaveOptions { Conformance = PdfConformance.PdfA3B }));
        Assert.IsLessThan(5000L, watch.ElapsedMilliseconds);
        Assert.DoesNotContain("fx:DocumentType", Xmp(back));
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
