using System.Runtime.CompilerServices;
using System.Text;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>What a document read on demand keeps after its pages have been read, and releasing it.</summary>
[TestClass]
public sealed class OnDemandMemoryTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A long document whose pages share nothing but the page tree: each has its own resources,
    /// content, font with widths and descriptor, and a link annotation, all packed in object streams.
    /// </summary>
    private static byte[] LongDocument(int pages)
    {
        var b = new PdfBuilder();
        var kids = new StringBuilder();
        var packed = new HashSet<int> { 1, 2 };
        int next = 3;
        string widths = string.Join(" ", Enumerable.Range(0, 224).Select(i => 400 + (i * 7 % 300)));
        for (int i = 0; i < pages; i++)
        {
            int page = next++, content = next++, resources = next++, font = next++, descriptor = next++, link = next++;
            kids.Append(page).Append(" 0 R ");
            foreach (int n in new[] { page, resources, font, descriptor, link }) packed.Add(n);
            b.Set(page, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents {content} 0 R/Resources {resources} 0 R/Annots[{link} 0 R]>>");
            var text = new StringBuilder();
            for (int line = 0; line < 40; line++) text.Append(Compat.Format("BT /F1 10 Tf 40 {0} Td (Page {1} line {2} of the long synthetic document) Tj ET\n", 760 - (line * 18), i + 1, line + 1));
            b.SetStream(content, "", Encoding.ASCII.GetBytes(text.ToString()));
            b.Set(resources, $"<</Font<</F1 {font} 0 R>>/ProcSet[/PDF/Text]>>");
            b.Set(font, $"<</Type/Font/Subtype/Type1/BaseFont/Synthetic{i}/FirstChar 32/LastChar 255/Widths[{widths}]/FontDescriptor {descriptor} 0 R/Encoding/WinAnsiEncoding>>");
            b.Set(descriptor, $"<</Type/FontDescriptor/FontName/Synthetic{i}/Flags 32/FontBBox[-100 -200 1000 900]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80>>");
            b.Set(link, $"<</Type/Annot/Subtype/Link/Rect[40 40 200 60]/Border[0 0 0]/A<</S/URI/URI(https://example.com/page/{i + 1})>>>>");
        }

        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, $"<</Type/Pages/Kids[{kids}]/Count {pages}>>");
        return b.BuildWithXrefStream(packed);
    }

    private static long Retained()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static PdfDictionary FontOf(PdfDocument doc, int page)
    {
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[page].Dictionary["Resources"])!;
        return (PdfDictionary)doc.Resolve(((PdfDictionary)resources["Font"]!)["F1"])!;
    }

    /// <summary>Reads every page, releasing every <paramref name="every"/> pages (0 never); returns what the document keeps.</summary>
    /// <summary>
    /// What a document read on demand keeps after reading every page: the heap with it, measured
    /// before it is dropped, minus the heap right after. Garbage other tests left behind is
    /// collected by the first measurement, not between the two.
    /// </summary>
    private static (long Bytes, int Objects, int Released) ReadAll(byte[] pdf, int every)
    {
        (long with, int objects, int released, WeakReference weak) = Read(pdf, every);
        long kept = with - Retained();
        Assert.IsFalse(weak.IsAlive, "The document is collected once nothing holds it.");
        return (kept, objects, released);
    }

    /// <summary>
    /// Reads the document in a frame of its own, so nothing on the measuring frame can hold it
    /// (the JIT may keep temporaries inside a try region alive until the method returns).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long With, int Objects, int Released, WeakReference Weak) Read(byte[] pdf, int every)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        PdfDocument doc = PdfDocument.OpenOnDemand(stream);
        int released = 0;
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            _ = doc.Pages[i].ExtractText();
            if (every > 0 && i % every == every - 1) released += doc.ReleaseUnusedObjects();
        }

        long with = Retained();
        int objects = doc.CachedObjectCount;
        return (with, objects, released, new WeakReference(doc));
    }

    [TestMethod]
    [DoNotParallelize] // memory: other tests' objects would land in the measurement
    public void ReleasingKeepsMemoryFlatWhileReadingPageByPage()
    {
        const int pages = 300;
        byte[] pdf = LongDocument(pages);
        _ = ReadAll(LongDocument(5), 0);

        (long grown, int grownObjects, _) = ReadAll(pdf, 0);
        (long flat, int flatObjects, int released) = ReadAll(pdf, 50);
        TestContext.WriteLine($"{pages} pages, {pdf.Length:N0} bytes: without releasing {grown:N0} bytes and {grownObjects} objects kept; releasing every 50 pages {flat:N0} bytes and {flatObjects} objects kept, {released} released");

        // Each page parses its resources, font, descriptor and link: they all stay.
        Assert.IsGreaterThanOrEqualTo(pages * 5, grownObjects);

        // Released, what stays is the page tree (the pages, held by the document) and what nothing frees.
        Assert.IsLessThan(pages + 10, flatObjects);
        Assert.IsGreaterThanOrEqualTo(pages * 4, released);
        Assert.IsLessThan(grown / 2, flat, $"kept {flat:N0} bytes releasing, {grown:N0} without");
    }

    [TestMethod]
    public void ReleasingNeverLosesAnEdit()
    {
        byte[] pdf = LongDocument(6);
        using var stream = new MemoryStream(pdf, writable: false);
        using PdfDocument doc = PdfDocument.OpenOnDemand(stream);
        foreach (PdfPage page in doc.Pages) _ = page.ExtractText();

        // Changed and no longer referenced: kept, because it differs from the file.
        FontOf(doc, 0).Set("Edited", PdfBoolean.True);

        // Referenced across the release and changed afterwards: kept, because it is referenced.
        PdfDictionary held = FontOf(doc, 1);
        int parsed = doc.CachedObjectCount;
        int released = doc.ReleaseUnusedObjects();
        Assert.IsGreaterThan(0, released);
        Assert.AreEqual(parsed - released, doc.CachedObjectCount);
        held.Set("ChangedLater", PdfBoolean.True);
        Assert.AreSame(held, FontOf(doc, 1), "A referenced object stays the instance the document returns.");
        held = null!;
        GC.Collect();

        // A page change and an information entry survive too, and a second release finds nothing new.
        doc.Pages[2].Rotation = 90;
        doc.Info.Title = "Released";
        doc.ReleaseUnusedObjects();
        Assert.AreEqual(0, doc.ReleaseUnusedObjects());

        using var output = new MemoryStream();
        doc.SaveIncremental(output);
        PdfDocument reopened = PdfDocument.Open(output.ToArray());
        Assert.IsTrue(FontOf(reopened, 0)["Edited"] is PdfBoolean { Value: true });
        Assert.IsTrue(FontOf(reopened, 1)["ChangedLater"] is PdfBoolean { Value: true });
        Assert.IsNull(FontOf(reopened, 3)["Edited"]);
        Assert.AreEqual(90, reopened.Pages[2].Rotation);
        Assert.AreEqual("Released", reopened.Info.Title);
    }

    [TestMethod]
    public void ReleasedObjectsReadAgainAsTheyWere()
    {
        // Written once by the library, so the file has the /ID both full saves below keep.
        var plain = new PdfSaveOptions { Optimize = false };
        byte[] pdf = PdfDocument.Open(LongDocument(8)).ToArray(plain);
        PdfDocument inMemory = PdfDocument.Open(pdf);
        string[] expected = [.. inMemory.Pages.Select(p => p.ExtractText())];

        using var stream = new MemoryStream(pdf, writable: false);
        using PdfDocument doc = PdfDocument.OpenOnDemand(stream);
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                Assert.AreEqual(expected[i], doc.Pages[i].ExtractText());
                doc.ReleaseUnusedObjects();
            }
        }

        // An incremental save compares with the file, which a release forgets too.
        doc.SaveIncremental(new MemoryStream());
        doc.ReleaseUnusedObjects();

        string? difference = PdfEquivalence.FindDifference(inMemory.ToArray(plain), doc.ToArray(plain));
        Assert.IsNull(difference, difference);
    }
}
