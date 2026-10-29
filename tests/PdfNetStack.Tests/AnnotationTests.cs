using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class AnnotationTests
{
    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(new PdfSaveOptions { Optimize = false });
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    private static double[] Numbers(PdfObject? o) => [.. ((PdfArray)o!).Select(v => v is PdfNumber n ? n.ToDouble() : double.NaN)];

    private static string AppearanceText(PdfDocument doc, PdfAnnotation a)
    {
        var ap = (PdfDictionary)doc.Resolve(a.Dictionary["AP"])!;
        return Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(ap["N"])!));
    }

    [TestMethod]
    public void WebLinks()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        page.Annotations.AddLink(50, 100, 200, 20, new Uri("https://github.com/jams4code/pdf.netstack?a=b c"));

        PdfDocument back = Reopen(doc);
        PdfAnnotation link = back.Pages[0].Annotations.Single();
        Assert.AreEqual("Link", link.Subtype);
        Assert.AreEqual(new PdfRectangle(50, 680, 250, 700), link.Rectangle);
        Assert.AreEqual("https://github.com/jams4code/pdf.netstack?a=b%20c", link.Uri);
        CollectionAssert.AreEqual(new double[] { 0, 0, 0 }, Numbers(link.Dictionary["Border"]));
        Assert.IsNull(link.Destination);
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddLink(0, 0, 10, 10, new Uri("/relative", UriKind.Relative)));
    }

    [TestMethod]
    public void PageLinks()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage first = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        PdfPage second = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        first.Annotations.AddLink(10, 10, 100, 20, second, top: 300);
        first.Annotations.AddLink(10, 40, 100, 20, second);

        PdfDocument back = Reopen(doc);
        PdfAnnotationCollection annots = back.Pages[0].Annotations;
        Assert.HasCount(2, annots);
        Assert.AreSame(back.Pages[1], annots[0].Destination);
        double[] dest = Numbers(annots[0].Dictionary["Dest"]);
        Assert.AreEqual(500, dest[3]);
        Assert.AreSame(back.Pages[1], annots[1].Destination);
        Assert.ThrowsExactly<ArgumentException>(() => first.Annotations.AddLink(0, 0, 10, 10, PdfDocument.Create().Pages.Add(PdfRectangle.A4)));
    }

    [TestMethod]
    public void NamedDestinationsResolve()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfPage target = doc.Pages.Add(PdfRectangle.A4);
        var names = new PdfDictionary
        {
            ["Dests"] = new PdfDictionary
            {
                ["Names"] = PdfArray.Of(PdfString.FromText("chapter-2"), PdfArray.Of(new PdfReference(target.Number, 0), new PdfName("Fit"))),
            },
        };
        doc.Catalog.Set("Names", names);
        PdfAnnotation link = page.Annotations.AddLink(0, 0, 10, 10, target);
        link.Dictionary.Set("Dest", PdfString.FromText("chapter-2"));

        PdfDocument back = Reopen(doc);
        Assert.AreSame(back.Pages[1], back.Pages[0].Annotations[0].Destination);
    }

    [TestMethod]
    public void HostileNameTreesResolveQuickly()
    {
        // Each of 25 levels lists the next 30 times: 30^25 paths without the visited set.
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfDictionary node = new() { ["Names"] = new PdfArray() };
        for (int level = 0; level < 25; level++)
        {
            PdfReference child = doc.AddObject(node);
            var kids = new PdfArray();
            for (int k = 0; k < 30; k++) kids.Add(child);
            node = new PdfDictionary { ["Kids"] = kids };
        }

        doc.Catalog.Set("Names", new PdfDictionary { ["Dests"] = node });
        PdfAnnotation link = page.Annotations.AddLink(0, 0, 10, 10, page);
        link.Dictionary.Set("Dest", PdfString.FromText("missing"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsNull(link.Destination);
        Assert.IsLessThan(5000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    public void NotesAndMarkupHaveAppearances()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Annotations.AddNote(500, 40, "Check the totals", "Reviewer");
        page.Annotations.AddTextMarkup(PdfTextMarkup.Highlight, 72, 100, 200, 14, comment: "Key sentence");
        page.Annotations.AddTextMarkup(PdfTextMarkup.Underline, 72, 120, 200, 14);
        page.Annotations.AddTextMarkup(PdfTextMarkup.StrikeOut, 72, 140, 200, 14);
        page.Annotations.AddTextMarkup(PdfTextMarkup.Squiggly, 72, 160, 200, 14);
        page.Annotations.AddFreeText(72, 200, 200, 40, "Approved by finance", new PdfTextStyle(PdfStandardFont.Helvetica, 11), PdfColor.Black, PdfColor.White);

        PdfDocument back = Reopen(doc);
        PdfAnnotation[] all = [.. back.Pages[0].Annotations];
        CollectionAssert.AreEqual(new[] { "Text", "Highlight", "Underline", "StrikeOut", "Squiggly", "FreeText" }, all.Select(a => a.Subtype).ToArray());
        Assert.AreEqual("Check the totals", all[0].Contents);
        Assert.AreEqual("Reviewer", ((PdfString)all[0].Dictionary["T"]!).ToText());
        Assert.AreEqual("Key sentence", all[1].Contents);

        // Quad points: upper left, upper right, lower left, lower right, in PDF space.
        CollectionAssert.AreEqual(new double[] { 72, 742, 272, 742, 72, 728, 272, 728 }, Numbers(all[1].Dictionary["QuadPoints"]));
        foreach (PdfAnnotation a in all) Assert.IsNotNull(a.Dictionary["AP"], a.Subtype);

        // The highlight multiplies, so the text under it stays readable.
        var ap = (PdfStream)back.Resolve(((PdfDictionary)back.Resolve(all[1].Dictionary["AP"])!)["N"])!;
        var gs = (PdfDictionary)back.Resolve(((PdfDictionary)back.Resolve(ap.Dictionary["Resources"])!)["ExtGState"])!;
        Assert.AreEqual("Multiply", ((PdfName)((PdfDictionary)back.Resolve(gs.First().Value)!)["BM"]!).Value);
        StringAssert.Contains(AppearanceText(back, all[5]), "Approved");
    }

    [TestMethod]
    public void AnnotationsCanBeRemoved()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfAnnotation a = page.Annotations.AddNote(10, 10, "one");
        page.Annotations.AddNote(40, 10, "two");
        Assert.IsTrue(page.Annotations.Remove(a));
        Assert.IsFalse(page.Annotations.Remove(a));
        Assert.AreEqual("two", Reopen(doc).Pages[0].Annotations.Single().Contents);
    }

    [TestMethod]
    public void SharedAnnotationArraysAreNotChanged()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage first = doc.Pages.Add(PdfRectangle.A4);
        PdfPage second = doc.Pages.Add(PdfRectangle.A4);
        first.Annotations.AddNote(10, 10, "shared");
        PdfReference shared = doc.AddObject(first.Dictionary["Annots"]!);
        first.Dictionary.Set("Annots", shared);
        second.Dictionary.Set("Annots", shared);

        second.Annotations.AddNote(20, 20, "second only");
        Assert.HasCount(1, first.Annotations);
        Assert.HasCount(2, second.Annotations);
    }

    [TestMethod]
    public void CanvasLinksFollowTheTransform()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.TranslateTransform(100, 200);
            g.ScaleTransform(2, 2);
            g.AddLink(10, 10, 50, 10, new Uri("https://example.com/"));
        }

        Assert.AreEqual(new PdfRectangle(120, 560, 220, 580), page.Annotations[0].Rectangle);
        using PdfGraphics f = PdfForm.Create(doc, 10, 10).CreateGraphics();
        Assert.ThrowsExactly<InvalidOperationException>(() => f.AddLink(0, 0, 1, 1, new Uri("https://example.com/")));
    }

    [TestMethod]
    public void BlendModesAreStateLikeTheRest()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            using (g.Save())
            {
                g.BlendMode = PdfBlendMode.Multiply;
                g.BlendMode = PdfBlendMode.Multiply;
                Assert.AreEqual(PdfBlendMode.Multiply, g.BlendMode);
            }

            Assert.AreEqual(PdfBlendMode.Normal, g.BlendMode);
        }

        PdfObject? contents = doc.Resolve(doc.Pages[0].Dictionary["Contents"]);
        string content = Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(contents)!));
        Assert.AreEqual(1, content.Split(new[] { " gs\n" }, StringSplitOptions.None).Length - 1);
    }
}
