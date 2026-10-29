using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>PDF/A-1b (PDF 1.4) and PDF/A-4, 4e and 4f (PDF 2.0) output.</summary>
[TestClass]
public sealed class PdfAPartsTests
{
    private static PdfOpenTypeFont Font() => PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf"));

    private static PdfDocument Document(Action<PdfGraphics>? draw = null)
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Info.Title = "Archive & <Record>";
        doc.Info.Author = "PdfNetStack";
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawString("AV To", Font(), 18, PdfColor.Black, 72, 72);
        draw?.Invoke(g);
        return doc;
    }

    private static byte[] Save(PdfDocument doc, PdfConformance conformance, bool optimize = true) =>
        doc.ToArray(new PdfSaveOptions { Conformance = conformance, Optimize = optimize });

    private static string Xmp(PdfDocument back) => Encoding.UTF8.GetString(back.GetStreamData((PdfStream)back.Resolve(back.Catalog["Metadata"])!));

    private static string Refusal(PdfDocument doc, PdfConformance conformance) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => Save(doc, conformance)).Message;

    private static PdfDictionary Descendant(PdfDocument back)
    {
        var fonts = (PdfDictionary)back.Resolve(((PdfDictionary)back.Resolve(back.Pages[0].Dictionary["Resources"])!)["Font"])!;
        var font = (PdfDictionary)back.Resolve(fonts.First().Value)!;
        return (PdfDictionary)back.Resolve(((PdfArray)back.Resolve(font["DescendantFonts"])!)[0])!;
    }

    private static void AddAnnotation(PdfDocument doc, PdfDictionary annotation)
    {
        PdfPage page = doc.Pages[0];
        if (doc.Resolve(page.Dictionary["Annots"]) is not PdfArray annots)
        {
            annots = new PdfArray();
            page.Dictionary.Set("Annots", annots);
        }

        annots.Add(doc.AddObject(annotation));
    }

    private static PdfDictionary Annotation(PdfDocument doc, string subtype)
    {
        PdfForm look = PdfForm.Create(doc, 40, 40);
        using (PdfGraphics g = look.CreateGraphics()) g.DrawRectangle(null, PdfColor.Gray(0.5), 0, 0, 40, 40);
        return new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName(subtype),
            ["Rect"] = PdfArray.Of(100, 100, 140, 140),
            ["AP"] = new PdfDictionary { ["N"] = look.Reference },
        };
    }

    [TestMethod]
    public void PdfA1bIsPdf14WithAClassicCrossReferenceTable()
    {
        foreach (bool optimize in new[] { true, false })
        {
            byte[] file = Save(Document(), PdfConformance.PdfA1B, optimize);
            string text = Encoding.ASCII.GetString(file);
            Assert.AreEqual("%PDF-1.4", text.Substring(0, 8));
            StringAssert.Contains(text, "\nxref\n0 ");
            StringAssert.Contains(text, "\ntrailer\n");
            Assert.DoesNotContain("/ObjStm", text);
            Assert.DoesNotContain("/XRef", text);
            Assert.IsTrue(text.EndsWith("%%EOF\n", StringComparison.Ordinal));

            PdfInspection inspection = PdfInspector.Inspect(file);
            Assert.IsFalse(inspection.UsesObjectStreams);
            Assert.IsFalse(inspection.UsesXrefStreams);
            Assert.IsTrue(inspection.IsPdfA1);

            PdfDocument back = PdfDocument.Open(file);
            Assert.AreEqual("1.4", back.Version);
            string xmp = Xmp(back);
            StringAssert.Contains(xmp, "<pdfaid:part>1</pdfaid:part>");
            StringAssert.Contains(xmp, "<pdfaid:conformance>B</pdfaid:conformance>");
            Assert.DoesNotContain("pdfaid:rev", xmp);
            Assert.AreEqual("Archive & <Record>", back.Info.Title);
            var intent = (PdfDictionary)back.Resolve(((PdfArray)back.Resolve(back.Catalog["OutputIntents"])!)[0])!;
            Assert.AreEqual("GTS_PDFA1", intent.GetName("S"));
        }
    }

    [TestMethod]
    public void PdfA1bSubsetFontsListTheirGlyphs()
    {
        PdfDocument back = PdfDocument.Open(Save(Document(), PdfConformance.PdfA1B));
        PdfDictionary cid = Descendant(back);
        Assert.AreEqual("Identity", cid.GetName("CIDToGIDMap"));
        var descriptor = (PdfDictionary)back.Resolve(cid["FontDescriptor"])!;
        byte[] program = back.GetStreamData((PdfStream)back.Resolve(descriptor["FontFile2"])!);
        byte[] set = back.GetStreamData((PdfStream)back.Resolve(descriptor["CIDSet"])!);

        // Glyph ids are kept in the subset, so the program holds every glyph up to the highest one used.
        int glyphs = MaxpGlyphs(program);
        Assert.IsGreaterThan(1, glyphs);
        for (int cidValue = 0; cidValue < set.Length * 8; cidValue++)
        {
            bool listed = (set[cidValue >> 3] & (0x80 >> (cidValue & 7))) != 0;
            Assert.AreEqual(cidValue < glyphs, listed, "CID " + cidValue);
        }

        // PDF/A-2 needs no CIDSet and gets none.
        PdfDocument two = PdfDocument.Open(Save(Document(), PdfConformance.PdfA2B));
        Assert.IsNull(((PdfDictionary)two.Resolve(Descendant(two)["FontDescriptor"])!)["CIDSet"]);
    }

    private static int MaxpGlyphs(byte[] sfnt)
    {
        int tables = sfnt[4] << 8 | sfnt[5];
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + 16 * i;
            if (Encoding.ASCII.GetString(sfnt, record, 4) != "maxp") continue;
            int offset = sfnt[record + 8] << 24 | sfnt[record + 9] << 16 | sfnt[record + 10] << 8 | sfnt[record + 11];
            return sfnt[offset + 4] << 8 | sfnt[offset + 5];
        }

        throw new AssertFailedException("No maxp table.");
    }

    [TestMethod]
    public void PdfA1bRefusesTransparency()
    {
        PdfDocument faded = Document(g => g.DrawRectangle(null, PdfColor.Rgb(1, 0, 0, 0.5), 10, 10, 50, 50));
        string message = Refusal(faded, PdfConformance.PdfA1B);
        StringAssert.Contains(message, "PDF/A-1b");
        StringAssert.Contains(message, "Transparency");

        // The same drawing is fine from PDF/A-2 on.
        Assert.IsNotNull(PdfDocument.Open(Save(faded, PdfConformance.PdfA2B)).Catalog["Metadata"]);

        PdfDocument stamped = Document();
        stamped.Pages[0].Annotations.AddStamp(72, 200, 150, 45, g => g.DrawString("AV", Font(), 20, PdfColor.Black, 10, 30), "Checked", new PdfAnnotationOptions { Opacity = 0.8 });
        StringAssert.Contains(Refusal(stamped, PdfConformance.PdfA1B), "Transparency");
    }

    [TestMethod]
    public void PdfA1bRefusesAttachmentsLayersAndNewerFeatures()
    {
        PdfDocument doc = Document();
        doc.Attachments.Add("data.csv", "a,b\n"u8.ToArray(), "text/csv");
        doc.Pages[0].Annotations.AddFileAttachment(500, 72, "more.csv", "c\n"u8.ToArray(), "text/csv");
        doc.Catalog.Set("OCProperties", new PdfDictionary { ["OCGs"] = new PdfArray() });
        string message = Refusal(doc, PdfConformance.PdfA1B);
        StringAssert.Contains(message, "PDF/A-1 cannot embed files");
        StringAssert.Contains(message, "FileAttachment annotations are not allowed in PDF/A-1");
        StringAssert.Contains(message, "Optional content");

        // Polygons arrived with PDF 1.5.
        PdfDocument polygon = Document();
        polygon.Pages[0].Annotations.AddPolygon([new PdfPoint(10, 10), new PdfPoint(60, 10), new PdfPoint(35, 50)]);
        StringAssert.Contains(Refusal(polygon, PdfConformance.PdfA1B), "Polygon annotations are not allowed in PDF/A-1");
    }

    [TestMethod]
    public void ScriptsAndUnknownNamedActionsAreRefused()
    {
        PdfDocument doc = Document();
        PdfAnnotation link = doc.Pages[0].Annotations.AddLink(0, 0, 50, 10, new Uri("https://example.com/"));
        link.Dictionary.Set("A", new PdfDictionary
        {
            ["S"] = new PdfName("URI"),
            ["URI"] = PdfString.FromText("https://example.com/"),
            ["Next"] = new PdfDictionary { ["S"] = new PdfName("JavaScript"), ["JS"] = PdfString.FromText("app.alert(1)") },
        });
        doc.Catalog.Set("OpenAction", new PdfDictionary { ["S"] = new PdfName("Named"), ["N"] = new PdfName("Print") });
        doc.Outlines.Add("Start", doc.Pages[0]);

        foreach (PdfConformance conformance in new[] { PdfConformance.PdfA1B, PdfConformance.PdfA2B, PdfConformance.PdfA4 })
        {
            string message = Refusal(doc, conformance);
            StringAssert.Contains(message, "JavaScript actions are not allowed");
            StringAssert.Contains(message, "The named action Print is not allowed");
        }

        doc.Catalog.Set("OpenAction", new PdfDictionary { ["S"] = new PdfName("Named"), ["N"] = new PdfName("NextPage") });
        link.Dictionary.Set("A", new PdfDictionary { ["S"] = new PdfName("URI"), ["URI"] = PdfString.FromText("https://example.com/") });
        Assert.IsNotNull(PdfDocument.Open(Save(doc, PdfConformance.PdfA1B)).Catalog["Metadata"]);
    }

    [TestMethod]
    public void WidgetAdditionalActionsAreRefused()
    {
        PdfDocument doc = Document();
        PdfAnnotation note = doc.Pages[0].Annotations.AddStamp(72, 200, 150, 45, g => g.DrawString("AV", Font(), 20, PdfColor.Black, 10, 30));
        note.Dictionary.Set("Subtype", new PdfName("Widget"));
        note.Dictionary.Set("AA", new PdfDictionary { ["E"] = new PdfDictionary { ["S"] = new PdfName("JavaScript"), ["JS"] = PdfString.FromText("1") } });
        StringAssert.Contains(Refusal(doc, PdfConformance.PdfA2B), "additional actions");
    }

    [TestMethod]
    public void LzwStreamsAreReencodedWithFlate()
    {
        PdfDocument doc = Document();
        PdfPage page = doc.Pages[0];
        byte[] content = "0 0 1 rg 10 10 20 20 re f\n"u8.ToArray();
        var lzw = new PdfStream(new PdfDictionary { ["Filter"] = new PdfName("LZWDecode") }, FilterTests.LzwEncode(content, 1));
        PdfObject existing = page.Dictionary["Contents"]!;
        page.Dictionary.Set("Contents", new PdfArray([existing, doc.AddObject(lzw)]));

        byte[] file = Save(doc, PdfConformance.PdfA2B, optimize: false);
        Assert.DoesNotContain("LZWDecode", Encoding.ASCII.GetString(file));
        PdfDocument back = PdfDocument.Open(file);
        var contents = (PdfArray)back.Resolve(back.Pages[0].Dictionary["Contents"])!;
        var reencoded = (PdfStream)back.Resolve(contents[1])!;
        Assert.AreEqual("FlateDecode", reencoded.Dictionary.GetName("Filter"));
        CollectionAssert.AreEqual(content, back.GetStreamData(reencoded));
    }

    [TestMethod]
    public void ImageInterpolationIsSwitchedOff()
    {
        PdfDocument doc = Document();
        var image = PdfStream.FromData(new byte[] { 0, 128, 255, 64 }, new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = PdfNumber.FromInteger(2),
            ["Height"] = PdfNumber.FromInteger(2),
            ["ColorSpace"] = new PdfName("DeviceGray"),
            ["BitsPerComponent"] = PdfNumber.FromInteger(8),
            ["Interpolate"] = PdfBoolean.True,
        });
        PdfReference reference = doc.AddObject(image);
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        resources.Set("XObject", new PdfDictionary { ["Im9"] = reference });
        Save(doc, PdfConformance.PdfA1B);
        Assert.AreSame(PdfBoolean.False, image.Dictionary["Interpolate"]);
    }

    [TestMethod]
    public void PdfA4IsPdf20WithTheRevisionAndOnlyAModificationDate()
    {
        PdfDocument doc = Document();
        doc.Info.Subject = "Records";
        byte[] file = Save(doc, PdfConformance.PdfA4);
        Assert.AreEqual("%PDF-2.0", Encoding.ASCII.GetString(file, 0, 8));

        PdfDocument back = PdfDocument.Open(file);
        Assert.AreEqual("2.0", back.Version);
        string xmp = Xmp(back);
        StringAssert.Contains(xmp, "<pdfaid:part>4</pdfaid:part>");
        StringAssert.Contains(xmp, "<pdfaid:rev>2020</pdfaid:rev>");
        Assert.DoesNotContain("pdfaid:conformance", xmp);
        StringAssert.Contains(xmp, "Archive &amp; &lt;Record&gt;");
        StringAssert.Contains(xmp, "<rdf:li>PdfNetStack</rdf:li>");

        // The information dictionary in the file holds the modification date and nothing else; the document keeps its values.
        CollectionAssert.AreEqual(new[] { "ModDate" }, back.Info.Keys.ToArray());
        DateTimeOffset modified = back.Info.ModificationDate!.Value;
        StringAssert.Contains(xmp, "<xmp:ModifyDate>" + modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z</xmp:ModifyDate>");
        Assert.AreEqual("Records", doc.Info.Subject);
        Assert.AreEqual("Archive & <Record>", doc.Info.Title);

        // The same document saves as plain PDF afterwards with its full information.
        Assert.AreEqual("Records", PdfDocument.Open(doc.ToArray()).Info.Subject);
    }

    [TestMethod]
    public void PdfA4fNeedsAnEmbeddedFileAndDeclaresIt()
    {
        PdfDocument empty = Document();
        StringAssert.Contains(Refusal(empty, PdfConformance.PdfA4F), "at least one embedded file");

        PdfDocument doc = Document();
        doc.Attachments.Add("data.csv", "a,b\n1,2\n"u8.ToArray(), "text/csv");
        StringAssert.Contains(Refusal(doc, PdfConformance.PdfA4), "PDF/A-4f");

        PdfDocument back = PdfDocument.Open(Save(doc, PdfConformance.PdfA4F));
        StringAssert.Contains(Xmp(back), "<pdfaid:conformance>F</pdfaid:conformance>");
        var spec = (PdfDictionary)back.Resolve(((PdfArray)back.Resolve(back.Catalog["AF"])!)[0])!;
        Assert.AreEqual("Unspecified", spec.GetName("AFRelationship"));
        Assert.AreEqual("text/csv", back.Attachments.Single().MimeType);
    }

    [TestMethod]
    public void PdfA4EmbedsArchivalPdfFilesOnly()
    {
        byte[] archival = Save(Document(), PdfConformance.PdfA2B);
        PdfDocument doc = Document();
        doc.Attachments.Add("earlier.pdf", archival, "application/pdf");
        PdfDocument back = PdfDocument.Open(Save(doc, PdfConformance.PdfA4));
        Assert.IsNotNull(back.Resolve(back.Catalog["AF"]));

        PdfDocument plain = Document();
        plain.Attachments.Add("draft.pdf", Document().ToArray(), "application/pdf");
        StringAssert.Contains(Refusal(plain, PdfConformance.PdfA4), "draft.pdf is not one");

        // A PDF/A-3 file may carry anything, so it does not count.
        PdfDocument nested = Document();
        nested.Attachments.Add("three.pdf", Save(Document(), PdfConformance.PdfA3B), "application/pdf");
        StringAssert.Contains(Refusal(nested, PdfConformance.PdfA4E), "three.pdf is not one");
    }

    [TestMethod]
    public void EngineeringContentNeedsPdfA4e()
    {
        PdfDocument doc = Document();
        PdfDictionary model = Annotation(doc, "3D");
        model.Set("3DD", doc.AddObject(PdfStream.FromData("U3D"u8, new PdfDictionary { ["Type"] = new PdfName("3D"), ["Subtype"] = new PdfName("U3D") })));
        AddAnnotation(doc, model);
        PdfAnnotation link = doc.Pages[0].Annotations.AddLink(0, 0, 50, 10, new Uri("https://example.com/"));
        link.Dictionary.Set("A", new PdfDictionary { ["S"] = new PdfName("GoTo3DView"), ["TA"] = doc.Pages[0].Dictionary["Annots"] is PdfArray a ? a[0] : PdfNull.Instance, ["V"] = PdfNumber.FromInteger(0) });

        string message = Refusal(doc, PdfConformance.PdfA4);
        StringAssert.Contains(message, "3D annotations need PDF/A-4e");
        StringAssert.Contains(message, "GoTo3DView actions need PDF/A-4e");
        StringAssert.Contains(Refusal(doc, PdfConformance.PdfA2B), "3D annotations are not allowed");

        PdfDocument back = PdfDocument.Open(Save(doc, PdfConformance.PdfA4E));
        StringAssert.Contains(Xmp(back), "<pdfaid:conformance>E</pdfaid:conformance>");

        // Sound and movie annotations stay out even of PDF/A-4e.
        AddAnnotation(doc, Annotation(doc, "Sound"));
        StringAssert.Contains(Refusal(doc, PdfConformance.PdfA4E), "Sound annotations are not allowed");
    }

    [TestMethod]
    public void InvoicesStayPdfA3()
    {
        PdfDocument doc = Document();
        PdfInvoice.Attach(doc, "<?xml version=\"1.0\"?><rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"/>"u8.ToArray(), PdfInvoiceProfile.EN16931);
        StringAssert.Contains(Refusal(doc, PdfConformance.PdfA4F), "must be saved as PDF/A-3");
    }

    [TestMethod]
    public void FormsLoseNeedAppearancesAndRefuseXfa()
    {
        PdfDocument doc = Document();
        var form = new PdfDictionary { ["Fields"] = new PdfArray(), ["NeedAppearances"] = PdfBoolean.True };
        doc.Catalog.Set("AcroForm", form);
        Save(doc, PdfConformance.PdfA1B);
        Assert.IsNull(form["NeedAppearances"]);

        form.Set("XFA", PdfString.FromText("<xdp/>"));
        StringAssert.Contains(Refusal(doc, PdfConformance.PdfA2B), "XFA forms are not allowed");
    }

    [TestMethod]
    public void PdfA1bRefusesJpeg2000AndNewerIccProfiles()
    {
        PdfDocument doc = Document();
        byte[] profile = new byte[128];
        profile[8] = 4;
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        resources.Set("ColorSpace", new PdfDictionary { ["CS0"] = PdfArray.Of(new PdfName("ICCBased"), doc.AddObject(PdfStream.FromData(profile, new PdfDictionary { ["N"] = PdfNumber.FromInteger(3) }))) });
        var jpx = new PdfStream(new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = PdfNumber.FromInteger(1),
            ["Height"] = PdfNumber.FromInteger(1),
            ["Filter"] = new PdfName("JPXDecode"),
        }, new byte[] { 0, 0, 0, 12 });
        resources.Set("XObject", new PdfDictionary { ["Im0"] = doc.AddObject(jpx) });
        string message = Refusal(doc, PdfConformance.PdfA1B);
        StringAssert.Contains(message, "JPEG 2000 images are not allowed in PDF/A-1");
        StringAssert.Contains(message, "ICC profile of version 4");
    }

    [TestMethod]
    public void UnreadableFontProgramsCannotGetACidSet()
    {
        PdfDocument doc = PdfDocument.Open(Save(Document(), PdfConformance.PdfA2B));
        var descriptor = (PdfDictionary)doc.Resolve(Descendant(doc)["FontDescriptor"])!;
        var program = (PdfStream)doc.Resolve(descriptor["FontFile2"])!;
        program.Dictionary.Remove("Filter");
        program.Data = new byte[] { 0, 1, 0, 0, 0, 9, 0, 0 };
        StringAssert.Contains(Refusal(doc, PdfConformance.PdfA1B), "cannot be read to list its glyphs");
    }

    [TestMethod]
    public void CyclesAndDeepNestingAreBounded()
    {
        PdfDocument doc = Document();

        // An action whose Next is itself, a bookmark that is its own sibling, and a deeply nested array.
        var loop = new PdfDictionary { ["S"] = new PdfName("URI"), ["URI"] = PdfString.FromText("https://example.com/") };
        PdfReference self = doc.AddObject(loop);
        loop.Set("Next", self);
        doc.Pages[0].Annotations.AddLink(0, 0, 50, 10, new Uri("https://example.com/")).Dictionary.Set("A", self);
        var item = new PdfDictionary { ["Title"] = PdfString.FromText("Loop") };
        PdfReference itemReference = doc.AddObject(item);
        item.Set("Next", itemReference);
        item.Set("A", self);
        doc.Catalog.Set("Outlines", new PdfDictionary { ["First"] = itemReference, ["Last"] = itemReference });
        var deep = new PdfArray();
        PdfArray innermost = deep;
        for (int i = 0; i < 2000; i++)
        {
            var next = new PdfArray();
            innermost.Add(next);
            innermost = next;
        }

        doc.Catalog.Set("Deep", doc.AddObject(deep));
        try
        {
            Save(doc, PdfConformance.PdfA2B, optimize: false);
        }
        catch (InvalidOperationException)
        {
            // Refusing is fine; hanging or any other exception is not.
        }
    }

    [TestMethod]
    public void DeclaredPartIsReadFromElementsAndAttributes()
    {
        foreach ((string xmp, int? part) in new (string, int?)[]
        {
            ("<pdfaid:part>2</pdfaid:part>", 2),
            ("<rdf:Description pdfaid:part=\"4\" pdfaid:rev=\"2020\"/>", 4),
            ("<pdfaid:part> 1 </pdfaid:part>", 1),
            ("<pdfaid:part>12</pdfaid:part>", null),
            ("<dc:title>none</dc:title>", null),
        })
        {
            PdfDocument doc = PdfDocument.Create();
            doc.Pages.Add(PdfRectangle.A4);
            var stream = PdfStream.FromData(Encoding.UTF8.GetBytes(xmp), compress: false);
            doc.Catalog.Set("Metadata", doc.AddObject(stream));
            var file = Parsing.PdfFile.Open(doc.ToArray(new PdfSaveOptions { Optimize = false }), 1 << 20);
            Assert.AreEqual(part, PdfArchive.DeclaredPart(file, 1 << 20), xmp);
        }
    }

    [TestMethod]
    public void DamagedEmbeddedFilesAreNotArchival()
    {
        PdfDocument doc = Document();
        doc.Attachments.Add("broken.pdf", "%PDF-1.7\n1 0 obj <<"u8.ToArray(), "application/pdf");
        StringAssert.Contains(Refusal(doc, PdfConformance.PdfA4), "broken.pdf is not one");
    }
}
