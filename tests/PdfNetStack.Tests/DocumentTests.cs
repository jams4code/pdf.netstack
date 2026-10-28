using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class DocumentTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static string Text(PdfPage page)
    {
        PdfObject? contents = page.Document.Resolve(page.Dictionary["Contents"]);
        return contents is PdfStream s ? Encoding.ASCII.GetString(s.Data.Span) : "";
    }

    private static PdfDocument Reopen(PdfDocument doc, PdfSaveOptions? options = null)
    {
        byte[] bytes = doc.ToArray(options ?? Plain);
        PdfFile file = PdfFile.Open(bytes, 64 << 20);
        Assert.IsFalse(file.WasReconstructed, "saved file must have a valid xref");
        Assert.AreEqual(0, file.LengthsRepaired);
        return PdfDocument.Open(bytes);
    }

    private static int CountObjects(byte[] pdf, Func<PdfDictionary, bool> match)
    {
        PdfFile file = PdfFile.Open(pdf, 64 << 20);
        int count = 0;
        foreach (int n in file.Xref.Keys)
        {
            if (file.ResolveDictionary(file.GetObject(n)) is { } d && match(d)) count++;
        }

        return count;
    }

    [TestMethod]
    public void CreateBlankPagesAndSave()
    {
        PdfDocument doc = PdfDocument.Create();
        doc.Pages.Add(PdfRectangle.A4);
        doc.Pages.Add(PdfRectangle.Letter).Rotation = 90;
        doc.Info.Title = "Blank";

        PdfDocument back = Reopen(doc);
        Assert.HasCount(2, back.Pages);
        Assert.AreEqual(595, back.Pages[0].Width);
        Assert.AreEqual(842, back.Pages[0].Height);
        Assert.AreEqual(90, back.Pages[1].Rotation);
        Assert.AreEqual("Blank", back.Info.Title);
    }

    [TestMethod]
    public void SavingWithoutPagesFails() =>
        Assert.ThrowsExactly<InvalidOperationException>(() => PdfDocument.Create().ToArray());

    [TestMethod]
    public void RemovedPagesAndTheirContentAreGone()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("Alpha", "Bravo", "Charlie").Build());
        doc.Pages.RemoveAt(1);
        byte[] bytes = doc.ToArray(Plain);

        PdfDocument back = PdfDocument.Open(bytes);
        Assert.HasCount(2, back.Pages);
        StringAssert.Contains(Text(back.Pages[0]), "(Alpha)");
        StringAssert.Contains(Text(back.Pages[1]), "(Charlie)");
        Assert.AreEqual(-1, Encoding.Latin1.GetString(bytes).IndexOf("Bravo", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MoveAndRotate()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("A", "B", "C").Build());
        doc.Pages.Move(2, 0);
        doc.Pages[1].Rotate(270);
        doc.Pages[1].Rotate(180);

        PdfDocument back = Reopen(doc);
        StringAssert.Contains(Text(back.Pages[0]), "(C)");
        StringAssert.Contains(Text(back.Pages[1]), "(A)");
        Assert.AreEqual(90, back.Pages[1].Rotation);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => back.Pages[0].Rotation = 45);
    }

    [TestMethod]
    public void InheritedAttributesAreMaterializedOnPages()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 2/MediaBox[0 0 300 400]/Rotate 90/Resources<</Font<</F1 6 0 R>>>>>>");
        b.Set(3, "<</Type/Pages/Parent 2 0 R/Kids[4 0 R 5 0 R]/Count 2>>");
        b.Set(4, "<</Type/Page/Parent 3 0 R/Contents 7 0 R>>");
        b.Set(5, "<</Type/Page/Parent 3 0 R/MediaBox[0 0 100 100]/Contents 7 0 R>>");
        b.Set(6, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.SetStream(7, "", "BT /F1 9 Tf (x) Tj ET"u8.ToArray());

        PdfDocument doc = PdfDocument.Open(b.Build());
        Assert.HasCount(2, doc.Pages);
        Assert.AreEqual(300, doc.Pages[0].Width);
        Assert.AreEqual(90, doc.Pages[0].Rotation);
        Assert.AreEqual(100, doc.Pages[1].Width);

        PdfDocument moved = PdfDocument.Create();
        moved.Pages.Import([doc.Pages[0]]);
        PdfDocument back = Reopen(moved);
        Assert.AreEqual(300, back.Pages[0].Width);
        Assert.AreEqual(90, back.Pages[0].Rotation);
        Assert.IsNotNull(back.ResolveDictionary(back.ResolveDictionary(back.Pages[0].Dictionary["Resources"])!["Font"]));
    }

    [TestMethod]
    public void MergeCopiesSharedResourcesOnce()
    {
        PdfDocument a = PdfDocument.Open(PdfBuilder.Simple("A1", "A2", "A3").Build());
        PdfDocument b = PdfDocument.Open(PdfBuilder.Simple("B1").Build());

        PdfDocument merged = PdfDocument.Merge([a, b]);
        byte[] bytes = merged.ToArray(Plain);

        PdfDocument back = PdfDocument.Open(bytes);
        Assert.HasCount(4, back.Pages);
        CollectionAssert.AreEqual(new[] { "(A1)", "(A2)", "(A3)", "(B1)" },
            back.Pages.Select(p => Text(p)[(Text(p).IndexOf('(', StringComparison.Ordinal))..(Text(p).IndexOf(')', StringComparison.Ordinal) + 1)]).ToArray());

        // One Helvetica from a (shared by three pages) and one from b.
        Assert.AreEqual(2, CountObjects(bytes, d => d.GetName("Type") == "Font"));
        Assert.AreEqual(1, CountObjects(bytes, d => d.GetName("Type") == "Pages"));
    }

    [TestMethod]
    public void ImportingTheSamePagesTwiceReusesResources()
    {
        PdfDocument source = PdfDocument.Open(PdfBuilder.Simple("X", "Y").Build());
        PdfDocument target = PdfDocument.Create();
        target.Pages.Import([source.Pages[0]]);
        target.Pages.Import([source.Pages[1], source.Pages[0]]);

        byte[] bytes = target.ToArray(Plain);
        Assert.HasCount(3, PdfDocument.Open(bytes).Pages);
        Assert.AreEqual(1, CountObjects(bytes, d => d.GetName("Type") == "Font"));
    }

    [TestMethod]
    public void LinksFollowImportedPagesAndDropOthers()
    {
        var b = PdfBuilder.Simple("One", "Two", "Three");
        // Page 1 (object 5) links to page 2 (7) and page 3 (9).
        b.Set(20, "<</Type/Annot/Subtype/Link/Rect[0 0 10 10]/P 5 0 R/Dest[7 0 R/Fit]>>");
        b.Set(21, "<</Type/Annot/Subtype/Link/Rect[0 0 10 10]/P 5 0 R/Dest[9 0 R/Fit]>>");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/Annots[20 0 R 21 0 R]>>");
        PdfDocument source = PdfDocument.Open(b.Build());

        PdfDocument target = PdfDocument.Create();
        target.Pages.Import([source.Pages[0], source.Pages[1]]);
        PdfDocument back = Reopen(target);

        var annots = (PdfArray)back.Resolve(back.Pages[0].Dictionary["Annots"])!;
        var toTwo = back.ResolveDictionary(annots[0])!;
        var toThree = back.ResolveDictionary(annots[1])!;
        Assert.AreEqual(back.Pages[1].Number, ((PdfReference)((PdfArray)toTwo["Dest"]!)[0]).Number);
        Assert.AreEqual(back.Pages[0].Number, ((PdfReference)toTwo["P"]!).Number);
        Assert.IsInstanceOfType<PdfNull>(((PdfArray)toThree["Dest"]!)[0]);
    }

    [TestMethod]
    public void DuplicatingAPageGivesItsOwnAnnotations()
    {
        var b = PdfBuilder.Simple("Only");
        b.Set(20, "<</Type/Annot/Subtype/Text/Rect[0 0 10 10]/Contents(note)/P 5 0 R>>");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/Annots[20 0 R]>>");
        PdfDocument doc = PdfDocument.Open(b.Build());

        doc.Pages.Import([doc.Pages[0]]);
        PdfDocument back = Reopen(doc);

        Assert.HasCount(2, back.Pages);
        var first = (PdfReference)((PdfArray)back.Resolve(back.Pages[0].Dictionary["Annots"])!)[0];
        var second = (PdfReference)((PdfArray)back.Resolve(back.Pages[1].Dictionary["Annots"])!)[0];
        Assert.AreNotEqual(first.Number, second.Number);
        Assert.AreEqual(back.Pages[1].Number, ((PdfReference)back.ResolveDictionary(second)!["P"]!).Number);
        Assert.AreEqual(((PdfReference)back.Pages[0].Dictionary["Contents"]!).Number, ((PdfReference)back.Pages[1].Dictionary["Contents"]!).Number);
    }

    [TestMethod]
    public void SplitProducesOneDocumentPerPage()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("P1", "P2", "P3").Build());
        var parts = doc.Split().Select(d => PdfDocument.Open(d.ToArray(Plain))).ToList();
        Assert.HasCount(3, parts);
        StringAssert.Contains(Text(parts[2].Pages[0]), "(P3)");
    }

    [TestMethod]
    public void InfoRoundTripsUnicodeAndDates()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("x").Build());
        var created = new DateTimeOffset(2026, 10, 28, 14, 30, 5, TimeSpan.FromHours(1));
        doc.Info.Title = "Rapport annuel: été 2026";
        doc.Info.Author = "مكتب الرباط";
        doc.Info.CreationDate = created;
        doc.Info["Department"] = "Engineering";

        PdfDocument back = Reopen(doc);
        Assert.AreEqual("Rapport annuel: été 2026", back.Info.Title);
        Assert.AreEqual("مكتب الرباط", back.Info.Author);
        Assert.AreEqual(created, back.Info.CreationDate);
        Assert.AreEqual("Engineering", back.Info["Department"]);

        back.Info.Author = null;
        Assert.IsNull(Reopen(back).Info.Author);
    }

    [TestMethod]
    [DataRow("D:20260102030405+05'30'", 2026, 1, 2, 3, 4, 5, 330)]
    [DataRow("D:2026", 2026, 1, 1, 0, 0, 0, 0)]
    [DataRow("D:20261231235959Z", 2026, 12, 31, 23, 59, 59, 0)]
    [DataRow("20260615-08'00", 2026, 6, 15, 0, 0, 0, -480)]
    public void ParsesPdfDates(string text, int y, int mo, int d, int h, int mi, int s, int offsetMinutes) =>
        Assert.AreEqual(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.FromMinutes(offsetMinutes)), Documents.PdfText.ParseDate(text));

    [TestMethod]
    public void ExistingOutlinesAreReadAndRewrittenWithChanges()
    {
        var b = PdfBuilder.Simple("A", "B", "C");
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/Outlines 30 0 R>>");
        b.Set(30, "<</Type/Outlines/First 31 0 R/Last 32 0 R/Count 3>>");
        b.Set(31, "<</Title(Intro)/Parent 30 0 R/Next 32 0 R/Dest[5 0 R/Fit]/C[1 0 0]>>");
        b.Set(32, "<</Title(Body)/Parent 30 0 R/Prev 31 0 R/A<</S/GoTo/D[7 0 R/XYZ 0 0 0]>>/First 33 0 R/Last 33 0 R/Count 1>>");
        b.Set(33, "<</Title(Web)/Parent 32 0 R/A<</S/URI/URI(https://example.com)>>>>");
        PdfDocument doc = PdfDocument.Open(b.Build());

        Assert.HasCount(2, doc.Outlines);
        Assert.AreEqual("Intro", doc.Outlines[0].Title);
        Assert.AreSame(doc.Pages[0], doc.Outlines[0].Destination);
        Assert.AreSame(doc.Pages[1], doc.Outlines[1].Destination);
        Assert.IsTrue(doc.Outlines[1].IsOpen);
        Assert.IsNull(doc.Outlines[1].Children[0].Destination);

        doc.Outlines.Add("Appendix", doc.Pages[2]).Add("Details", doc.Pages[2]);
        doc.Outlines[0].Title = "Introduction";

        PdfDocument back = Reopen(doc);
        Assert.HasCount(3, back.Outlines);
        Assert.AreEqual("Introduction", back.Outlines[0].Title);
        Assert.AreSame(back.Pages[1], back.Outlines[1].Destination);
        Assert.AreSame(back.Pages[2], back.Outlines[2].Destination);
        Assert.AreEqual("Details", back.Outlines[2].Children[0].Title);

        // Untouched items keep their styling and non-page actions.
        Assert.IsNotNull(back.Outlines[0].Original!["C"]);
        Assert.AreEqual("URI", back.ResolveDictionary(back.Outlines[1].Children[0].Original!["A"])!.GetName("S"));
    }

    [TestMethod]
    public void OutlinePointingAtRemovedPageLosesItsDestination()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("A", "B").Build());
        doc.Outlines.Add("Second", doc.Pages[1]);
        doc.Pages.RemoveAt(1);
        PdfDocument back = Reopen(doc);
        Assert.HasCount(1, back.Outlines);
        Assert.IsNull(back.Outlines[0].Destination);
    }

    [TestMethod]
    public void SaveWithOptimizationProducesAVerifiedSmallerFile()
    {
        PdfBuilder b = PdfBuilder.Simple("x", "y");
        b.SetStream(4, "", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0 0 m 100 100 l S\n", 500))));
        PdfDocument doc = PdfDocument.Open(b.Build());

        byte[] plain = doc.ToArray(Plain);
        byte[] optimized = doc.ToArray();
        Assert.IsLessThan(plain.Length, optimized.Length);
        Assert.IsNull(PdfEquivalence.FindDifference(plain, optimized));
    }

    [TestMethod]
    public void SavingTwiceGivesTheSameDocument()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("A", "B").Build());
        byte[] first = doc.ToArray(Plain);
        byte[] second = doc.ToArray(Plain);
        Assert.IsNull(PdfEquivalence.FindDifference(first, second));
    }

    [TestMethod]
    public void EncryptedDocumentsAreRejectedClearly()
    {
        PdfBuilder b = PdfBuilder.Simple("secret");
        b.Add("<</Filter/Standard/V 2/R 3/O(x)/U(y)/P -4>>");
        byte[] input = b.Build(trailerExtra: "/Encrypt 6 0 R/ID[<01><01>]");
        Assert.ThrowsExactly<NotSupportedException>(() => PdfDocument.Open(input));
    }
}
