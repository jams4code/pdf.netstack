using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class AnnotationFlatteningTests
{
    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(new PdfSaveOptions { Optimize = false });
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    private static string Content(PdfDocument doc, PdfPage page)
    {
        PdfObject? contents = doc.Resolve(page.Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : contents is null ? [] : [contents];
        return string.Concat(parts.Select(p => Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!)) + "\n"));
    }

    private static int Count(string content, string op) => content.Split(new[] { op }, StringSplitOptions.None).Length - 1;

    /// <summary>An annotation with a hand made appearance: a form with the given bounding box and matrix.</summary>
    private static PdfAnnotation Custom(PdfPage page, string subtype, PdfRectangle rect, PdfRectangle bbox, PdfArray? matrix = null, int flags = 4)
    {
        PdfDocument doc = page.Document;
        var form = PdfStream.FromData("0 0 1 rg 0 0 10 10 re f"u8);
        form.Dictionary.Set("BBox", bbox.ToArray());
        if (matrix is not null) form.Dictionary.Set("Matrix", matrix);
        var annotation = new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName(subtype),
            ["Rect"] = rect.ToArray(),
            ["F"] = PdfNumber.FromInteger(flags),
            ["AP"] = new PdfDictionary { ["N"] = doc.AddObject(form) },
        };
        PdfReference reference = page.Annotations.AddAnnotation(annotation);
        return page.Annotations.Single(a => a.Entry is PdfReference r && r.Number == reference.Number);
    }

    [TestMethod]
    public void OneAnnotationBecomesPageContent()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfAnnotation stamp = page.Annotations.AddStamp(PdfStampName.Final, 300, 40, 150, 45);
        PdfAnnotation note = page.Annotations.AddNote(500, 40, "Keep me");

        Assert.IsTrue(page.Annotations.Flatten(stamp));
        Assert.IsFalse(page.Annotations.Flatten(stamp));
        Assert.AreEqual("Text", page.Annotations.Single().Subtype);
        Assert.IsFalse(doc.Pages.Add(PdfRectangle.A4).Annotations.Flatten(note));

        PdfDocument back = Reopen(doc);
        StringAssert.Contains(back.Pages[0].ExtractText(), "FINAL");
        Assert.AreEqual(1, Count(Content(back, back.Pages[0]), " Do"));
        Assert.HasCount(1, back.Pages[0].Annotations);
    }

    [TestMethod]
    public void PagesKeepLinksWidgetsAndHiddenAnnotationsByDefault()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Annotations.AddLink(10, 10, 100, 20, new Uri("https://example.com/"));
        doc.Form.AddTextField(page, "name", 72, 100, 200, 20, "Ada");
        page.Annotations.AddFreeText(72, 200, 200, 40, "Reviewed text", new PdfTextStyle(PdfStandardFont.Helvetica, 11));
        page.Annotations.AddSquare(72, 300, 50, 50);
        PdfAnnotation hidden = page.Annotations.AddCircle(72, 400, 50, 50);
        hidden.Dictionary.Set("F", PdfNumber.FromInteger(4 | 2));
        PdfAnnotation noView = page.Annotations.AddCircle(172, 400, 50, 50);
        noView.Dictionary.Set("F", PdfNumber.FromInteger(4 | 32));

        Assert.AreEqual(2, doc.FlattenAnnotations());
        CollectionAssert.AreEqual(new[] { "Link", "Widget", "Circle", "Circle" }, page.Annotations.Select(a => a.Subtype).ToArray());
        Assert.IsTrue(doc.Form.Exists);
        Assert.AreEqual(0, doc.FlattenAnnotations());

        PdfDocument back = Reopen(doc);
        StringAssert.Contains(back.Pages[0].ExtractText(), "Reviewed text");
        Assert.AreEqual("Ada", ((PdfTextField)back.Form["name"]!).Text);
    }

    [TestMethod]
    public void LinksAndWidgetsWhenAsked()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Annotations.AddLink(10, 10, 100, 20, new Uri("https://example.com/"));
        doc.Form.AddTextField(page, "name", 72, 100, 200, 20, "Ada Lovelace");
        doc.Form.AddRadioGroup("size", [(page, 72, 150, 14, 14, "S"), (page, 100, 150, 14, 14, "M")], "M");

        Assert.AreEqual(1, page.Annotations.Flatten(new PdfFlattenOptions { IncludeLinks = true }));
        Assert.HasCount(3, page.Annotations);

        // Flattening one radio button leaves the group with the other.
        PdfAnnotation small = page.Annotations.First(a => a.Subtype == "Widget" && a.Dictionary["Parent"] is not null);
        Assert.IsTrue(page.Annotations.Flatten(small));
        Assert.HasCount(1, doc.Form["size"]!.Widgets);

        // Once every widget is drawn, no field is left and the form goes.
        Assert.AreEqual(2, page.Annotations.Flatten(new PdfFlattenOptions { IncludeWidgets = true }));
        Assert.IsFalse(doc.Form.Exists);
        Assert.IsNull(page.Dictionary["Annots"]);

        PdfDocument back = Reopen(doc);
        Assert.IsFalse(back.Form.Exists);
        StringAssert.Contains(back.Pages[0].ExtractText(), "Ada Lovelace");
    }

    [TestMethod]
    public void PopupsGoWithTheirAnnotation()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfAnnotation square = page.Annotations.AddSquare(50, 50, 40, 40);
        var popup = new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName("Popup"),
            ["Rect"] = new PdfRectangle(100, 600, 300, 700).ToArray(),
            ["Parent"] = square.Entry,
        };
        PdfReference popupReference = page.Annotations.AddAnnotation(popup);
        square.Dictionary.Set("Popup", popupReference);
        var orphan = new PdfDictionary { ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Popup"), ["Rect"] = new PdfRectangle(0, 0, 10, 10).ToArray() };
        page.Annotations.AddAnnotation(orphan);

        Assert.AreEqual(1, page.Annotations.Flatten());
        Assert.AreEqual("Popup", page.Annotations.Single().Subtype);
        Assert.AreSame(orphan, page.Annotations.Single().Dictionary);
    }

    [TestMethod]
    public void TheAppearanceIsFittedToTheRectangle()
    {
        PdfDocument doc = PdfDocument.Create();
        var page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));

        // A 100 by 50 box at (10, 20), turned a quarter by its matrix: 50 wide and 100 high,
        // fitted onto a 100 by 200 rectangle, so scaled by 2 both ways.
        PdfArray quarter = PdfArray.Of(0, 1, -1, 0, 0, 0);
        PdfAnnotation turned = Custom(page, "Stamp", new PdfRectangle(200, 300, 300, 500), new PdfRectangle(10, 20, 110, 70), quarter);
        var form = (PdfStream)doc.Resolve(((PdfDictionary)turned.Dictionary["AP"]!)["N"])!;
        PdfMatrix m = PdfAnnotationCollection.PlaceAppearance(doc, form.Dictionary, new PdfRectangle(200, 300, 300, 500));
        Assert.AreEqual(new PdfMatrix(2, 0, 0, 2, 340, 280), m);

        // The box corner (10, 20) turns to (-20, 10) and lands on the rectangle's corner.
        PdfPoint corner = PdfMatrix.Multiply(new PdfMatrix(0, 1, -1, 0, 0, 0), m).Transform(new PdfPoint(10, 20));
        Assert.AreEqual(300, corner.X, 1e-9);
        Assert.AreEqual(300, corner.Y, 1e-9);
        PdfPoint far = PdfMatrix.Multiply(new PdfMatrix(0, 1, -1, 0, 0, 0), m).Transform(new PdfPoint(110, 70));
        Assert.AreEqual(200, far.X, 1e-9);
        Assert.AreEqual(500, far.Y, 1e-9);

        // Without a matrix or a usable box the form fills the rectangle as drawn.
        form.Dictionary.Remove("Matrix");
        form.Dictionary.Set("BBox", PdfArray.Of(new PdfName("x"), PdfNumber.FromInteger(1), PdfNumber.FromInteger(2), PdfNumber.FromInteger(3)));
        Assert.AreEqual(new PdfMatrix(1, 0, 0, 1, 200, 300), PdfAnnotationCollection.PlaceAppearance(doc, form.Dictionary, new PdfRectangle(200, 300, 300, 500)));
        form.Dictionary.Set("Matrix", PdfArray.Of(PdfNumber.FromInteger(1), PdfNumber.FromInteger(0), PdfNumber.FromInteger(0), new PdfName("bad"), PdfNumber.FromInteger(0), PdfNumber.FromInteger(0)));
        Assert.AreEqual(new PdfMatrix(1, 0, 0, 1, 200, 300), PdfAnnotationCollection.PlaceAppearance(doc, form.Dictionary, new PdfRectangle(200, 300, 300, 500)));

        Assert.IsTrue(page.Annotations.Flatten(turned));
        StringAssert.Contains(Content(doc, page), " Do");
    }

    [TestMethod]
    public void NoRotateAnnotationsStayUprightOnRotatedPages()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        page.Rotation = 90;
        PdfAnnotation note = Custom(page, "Text", new PdfRectangle(100, 600, 120, 620), new PdfRectangle(0, 0, 20, 20), flags: 4 | 16);
        Assert.IsTrue(page.Annotations.Flatten(note));

        // Placed at the rectangle, then turned 90 degrees counterclockwise about its top left corner
        // (100, 620): user space (0 1 -1 0 120 620), written after the page's y down flip.
        string content = Content(doc, page);
        StringAssert.Contains(content, "0 -1 -1 0 120 180 cm");
    }

    [TestMethod]
    public void OptionalContentKeepsItsVisibility()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfReference layer = doc.AddObject(new PdfDictionary { ["Type"] = new PdfName("OCG"), ["Name"] = PdfString.FromText("Review") });
        PdfAnnotation square = page.Annotations.AddSquare(50, 50, 40, 40);
        square.Dictionary.Set("OC", layer);
        page.Annotations.Flatten(square);

        string content = Content(doc, page);
        StringAssert.Contains(content, "/OC /Oc1 BDC");
        StringAssert.Contains(content, "EMC");
        var properties = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(page.Dictionary["Resources"])!)["Properties"])!;
        Assert.AreEqual(layer.Number, ((PdfReference)properties["Oc1"]!).Number);
    }

    [TestMethod]
    public void AppearanceStatesPickTheShownOne()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        doc.Form.AddCheckBox(page, "agree", 72, 72, 14, 14, isChecked: true);
        PdfAnnotation box = page.Annotations.Single();
        var states = (PdfDictionary)doc.Resolve(((PdfDictionary)box.Dictionary["AP"]!)["N"])!;
        int on = ((PdfReference)states["Yes"]!).Number;

        Assert.IsTrue(page.Annotations.Flatten(box));
        var xobjects = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(page.Dictionary["Resources"])!)["XObject"])!;
        Assert.AreEqual(on, ((PdfReference)xobjects.Single().Value).Number);
        Assert.IsFalse(doc.Form.Exists);
    }

    [TestMethod]
    public void HostileAppearancesAreLeftAlone()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfDictionary Annotation(PdfObject? rect, PdfObject? ap)
        {
            var d = new PdfDictionary { ["Subtype"] = new PdfName("Square") };
            if (rect is not null) d.Set("Rect", rect);
            if (ap is not null) d.Set("AP", ap);
            page.Annotations.AddAnnotation(d);
            return d;
        }

        PdfReference image = doc.AddObject(PdfStream.FromData([0], new PdfDictionary { ["Subtype"] = new PdfName("Image") }));
        PdfReference form = doc.AddObject(PdfStream.FromData("0 g"u8));
        Annotation(new PdfRectangle(0, 0, 10, 10).ToArray(), new PdfDictionary { ["N"] = doc.AddObject(new PdfDictionary()) });
        Annotation(new PdfRectangle(0, 0, 10, 10).ToArray(), new PdfDictionary { ["N"] = image });
        Annotation(null, new PdfDictionary { ["N"] = form });
        Annotation(PdfArray.Of(0, 0, 0, 10), new PdfDictionary { ["N"] = form });
        Annotation(PdfArray.Of(new PdfName("a"), PdfNumber.FromInteger(0), PdfNumber.FromInteger(1), PdfNumber.FromInteger(1)), new PdfDictionary { ["N"] = form });
        Annotation(new PdfRectangle(0, 0, 10, 10).ToArray(), new PdfName("AP"));
        Annotation(new PdfRectangle(0, 0, 10, 10).ToArray(), new PdfDictionary { ["N"] = new PdfDictionary { ["On"] = form } });
        page.Annotations.AddAnnotation(new PdfDictionary());
        page.Dictionary.Set("Annots", new PdfArray([.. (PdfArray)page.Dictionary["Annots"]!, PdfNull.Instance, new PdfReference(9999, 0)]));

        Assert.AreEqual(0, doc.FlattenAnnotations(new PdfFlattenOptions { IncludeLinks = true, IncludeWidgets = true }));
        Assert.HasCount(10, (PdfArray)page.Dictionary["Annots"]!);
        Reopen(doc);
    }

    [TestMethod]
    public void FormFlatteningSkipsAppearancesThatAreNotStreams()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        doc.Form.AddCheckBox(page, "agree", 72, 72, 14, 14, isChecked: true);
        doc.Form.AddTextField(page, "name", 72, 100, 100, 20, "Ada");
        var states = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Form["agree"]!.Widgets[0].Dictionary["AP"]!)["N"])!;
        states.Set("Yes", doc.AddObject(new PdfDictionary()));

        doc.Form.Flatten();
        Assert.IsFalse(doc.Form.Exists);
        StringAssert.Contains(Reopen(doc).Pages[0].ExtractText(), "Ada");
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void ManyAnnotationsFlattenQuickly()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfAnnotation first = page.Annotations.AddSquare(10, 10, 10, 10);
        var appearance = (PdfDictionary)first.Dictionary["AP"]!;
        var annots = (PdfArray)page.Dictionary["Annots"]!;
        for (int i = 0; i < 20_000; i++)
        {
            var copy = PdfDocument.Clone(first.Dictionary);
            copy.Set("AP", appearance);
            annots.Add(doc.AddObject(copy));
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual(20_001, page.Annotations.Flatten());
        Assert.IsLessThan(10_000, clock.ElapsedMilliseconds);

        // One shared appearance, one resource name.
        var xobjects = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(page.Dictionary["Resources"])!)["XObject"])!;
        Assert.HasCount(1, xobjects);
    }
}
