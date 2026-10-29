using System.Text;
using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Linearized output (ISO 32000-1, Annex F), checked by reading its structure back.</summary>
[TestClass]
public sealed class LinearizationTests
{
    private static readonly PdfOptimizerOptions Linear = new() { Linearize = true, MinSavingPercent = 0 };

    private static (byte[] Output, PdfOptimizationResult Result) Run(byte[] input, PdfOptimizerOptions? options = null) =>
        PdfOptimizer.Optimize(input, options ?? Linear);

    private static byte[] Repeat(string text, int times) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, times)));

    /// <summary>
    /// Pages sharing one font, each with its own content and image, a resource dictionary and media
    /// box inherited from the tree, bookmarks, a form field and an open action.
    /// </summary>
    private static byte[] Document(int pages, bool outlinesFirst = false)
    {
        var b = new PdfBuilder();
        var kids = new StringBuilder();
        int next = 10;
        var outlineItems = new List<int>();
        var pageNumbers = new List<int>();
        for (int i = 0; i < pages; i++)
        {
            int page = next++, content = next++, image = next++;
            pageNumbers.Add(page);
            kids.Append(page).Append(" 0 R ");
            string annots = i == 1 ? "/Annots[7 0 R]" : "";
            b.Set(page, $"<</Type/Page/Parent 2 0 R/Contents {content} 0 R/Resources<</Font<</F1 4 0 R>>/XObject<</Im 5 0 R/Own {image} 0 R>>>>{annots}>>");
            b.SetStream(content, "", Encoding.ASCII.GetBytes($"q 50 0 0 50 0 0 cm /Im Do Q q 20 0 0 20 100 0 cm /Own Do Q BT /F1 12 Tf 20 150 Td (Page {i + 1}) Tj ET"));
            b.SetStream(image, "/Type/XObject/Subtype/Image/Width 8/Height 8/ColorSpace/DeviceGray/BitsPerComponent 8", Enumerable.Range(0, 64).Select(v => (byte)(v * (i + 3))).ToArray());
            int item = next++;
            outlineItems.Add(item);
        }

        // Bookmarks, one per page, as a linked list under the outline dictionary 3.
        for (int i = 0; i < pages; i++)
        {
            string links = (i > 0 ? $"/Prev {outlineItems[i - 1]} 0 R" : "") + (i < pages - 1 ? $"/Next {outlineItems[i + 1]} 0 R" : "");
            b.Set(outlineItems[i], $"<</Title(Page {i + 1})/Parent 3 0 R/Dest[{pageNumbers[i]} 0 R/Fit]{links}>>");
        }

        string pageMode = outlinesFirst ? "/PageMode/UseOutlines" : "";
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/Outlines 3 0 R/AcroForm<</Fields[7 0 R]>>/OpenAction[10 0 R/Fit]{pageMode}>>");
        b.Set(2, $"<</Type/Pages/Kids[{kids}]/Count {pages}/MediaBox[0 0 200 200]/Rotate 0>>");
        b.Set(3, $"<</Type/Outlines/First {outlineItems[0]} 0 R/Last {outlineItems[^1]} 0 R/Count {pages}>>");
        b.Set(4, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.SetStream(5, "/Type/XObject/Subtype/Image/Width 16/Height 16/ColorSpace/DeviceGray/BitsPerComponent 8", Enumerable.Range(0, 256).Select(v => (byte)v).ToArray());
        b.Set(6, "<</Title(Synthetic)/Producer(PdfNetStack tests)>>");
        b.Set(7, $"<</Type/Annot/Subtype/Widget/FT/Tx/T(name)/Rect[10 10 90 30]/P {pageNumbers[Math.Min(1, pages - 1)]} 0 R>>");
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 80));
        return b.Build("/Info 6 0 R");
    }

    [TestMethod]
    public void LinearizedOutputIsVerifiedAndStructurallySound()
    {
        foreach ((int pages, bool outlinesFirst) in new[] { (1, false), (2, false), (5, false), (4, true) })
        {
            byte[] input = Document(pages, outlinesFirst);
            (byte[] output, PdfOptimizationResult result) = Run(input);

            Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
            Assert.IsTrue(result.Statistics.Linearized, result.Reason);
            StringAssert.Contains(result.Reason, "linearized");
            Assert.IsNull(PdfEquivalence.FindDifference(input, output));
            CheckLinearization(output);

            PdfDocument doc = PdfDocument.Open(output);
            Assert.AreEqual(pages, doc.Pages.Count);
            for (int i = 0; i < pages; i++) StringAssert.Contains(doc.Pages[i].ExtractText(), $"Page {i + 1}");
        }
    }

    [TestMethod]
    public void DocumentsMadeByTheLibraryLinearize()
    {
        PdfDocument created = PdfDocument.Create();
        for (int i = 0; i < 3; i++)
        {
            using Drawing.PdfGraphics g = created.Pages.Add(PdfRectangle.A4).CreateGraphics();
            g.DrawString("Linear page " + (i + 1), Fonts.PdfStandardFont.Helvetica, 14, Drawing.PdfColor.Rgb(0, 0, 0), 40, 60);
        }

        created.Outlines.Add("First", created.Pages[0]);
        byte[] input = created.ToArray();
        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsTrue(result.Statistics.Linearized);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
        CheckLinearization(output);
    }

    [TestMethod]
    public void LinearizationIsKeptEvenWhenItCostsBytes()
    {
        // Object streams make this input compact; a linearized file writes every object on its own.
        PdfDocument created = PdfDocument.Create();
        for (int i = 0; i < 40; i++) created.Pages.Add(PdfRectangle.A4);
        byte[] input = created.ToArray();

        (byte[] output, PdfOptimizationResult result) = Run(input, Linear with { MinSavingPercent = 1 });
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsTrue(result.Statistics.Linearized);
        Assert.IsGreaterThan(input.Length, output.Length);

        // Already linearized: a second pass keeps it only when it saves enough.
        (byte[] again, PdfOptimizationResult second) = Run(output, Linear with { MinSavingPercent = 1 });
        Assert.AreEqual(OptimizationDisposition.OriginalRetained, second.Disposition, second.Reason);
        CollectionAssert.AreEqual(output, again);
    }

    [TestMethod]
    public void PageTreesThatCannotBeLaidOutAreWrittenAsUsual()
    {
        var b = PdfBuilder.Simple("x");
        b.Set(2, "<</Type/Pages/Kids[5 0 R 5 0 R]/Count 2>>");
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 80));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsFalse(result.Statistics.Linearized);
        StringAssert.Contains(result.Reason, "Not linearized: a page appears twice in the page tree.");
        Assert.IsFalse(PdfFile.Open(output, 64 << 20).IsLinearized);
    }

    [TestMethod]
    public void VerifierAcceptsInheritedAttributesOnlyWhereEveryPageSeesTheSame()
    {
        byte[] Build(string node, string page1, string page2)
        {
            var b = new PdfBuilder();
            b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
            b.Set(2, $"<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2{node}>>");
            b.Set(3, $"<</Type/Page/Parent 2 0 R/Contents 5 0 R{page1}>>");
            b.Set(4, $"<</Type/Page/Parent 2 0 R/Contents 5 0 R{page2}>>");
            b.SetStream(5, "", "0 0 m 10 10 l S"u8.ToArray());
            return b.Build();
        }

        byte[] inherited = Build("/MediaBox[0 0 100 100]/Rotate 90", "", "/Rotate 0");
        Assert.IsNull(PdfEquivalence.FindDifference(inherited, Build("", "/MediaBox[0 0 100 100]/Rotate 90", "/MediaBox[0 0 100 100]/Rotate 0")));
        Assert.IsNotNull(PdfEquivalence.FindDifference(inherited, Build("", "/MediaBox[0 0 100 100]/Rotate 90", "/MediaBox[0 0 100 100]/Rotate 90")));
        Assert.IsNotNull(PdfEquivalence.FindDifference(inherited, Build("/MediaBox[0 0 100 100]", "", "/Rotate 0")));
    }

    [TestMethod]
    public void MutatedInputsNeverBreakLinearization()
    {
        byte[] seed = Document(3);
        var random = new Random(23);
        for (int round = 0; round < 80; round++)
        {
            byte[] mutated = (byte[])seed.Clone();
            for (int k = 0; k < 3; k++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            (byte[] output, PdfOptimizationResult result) = Run(mutated, Linear with { AllowRepairedFiles = true });
            if (result.Disposition != OptimizationDisposition.Optimized)
            {
                CollectionAssert.AreEqual(mutated, output);
                continue;
            }

            Assert.IsNull(PdfEquivalence.FindDifference(mutated, output), result.Reason);
            if (result.Statistics.Linearized) CheckLinearization(output);
        }
    }

    // --- Structural check ------------------------------------------------------------------

    /// <summary>
    /// Reads a linearized file back and checks what Annex F requires and qpdf's
    /// --check-linearization verifies: the parameter dictionary against the file, the two
    /// cross-reference sections, and every entry of the page offset, shared object and outline
    /// hint tables against the objects' real offsets and lengths.
    /// </summary>
    internal static void CheckLinearization(byte[] pdf)
    {
        PdfFile file = PdfFile.Open(pdf, 64 << 20);
        Assert.IsTrue(file.IsLinearized, "The first object is the linearization dictionary.");
        Assert.AreEqual(1, file.RevisionCount);
        Assert.IsFalse(file.WasReconstructed);
        Assert.AreEqual(0, file.LengthsRepaired);
        Assert.IsFalse(file.UsesObjectStreams, "Every object has its own offset.");

        string text = Compat.Latin1.GetString(pdf);
        int lindictAt = text.IndexOf(" obj", StringComparison.Ordinal);
        Assert.IsLessThan(1024, lindictAt);
        var parser = new PdfParser(pdf, text.LastIndexOf('\n', lindictAt) + 1);
        Assert.IsTrue(parser.TryReadInteger(out long lindictNumber));
        var lindict = (PdfDictionary)file.GetObject((int)lindictNumber);
        long Int(PdfObject? o) => ((PdfNumber)o!).IntegerValue;
        Assert.AreEqual(pdf.Length, Int(lindict["L"]));

        // Pages in order, none inheriting anything.
        PdfDictionary catalog = file.ResolveDictionary(file.Trailer["Root"])!;
        var pages = new List<int>();
        void Walk(PdfObject node)
        {
            var r = (PdfReference)node;
            var d = (PdfDictionary)file.GetObject(r.Number);
            if (d.GetName("Type") == "Page")
            {
                pages.Add(r.Number);
                return;
            }

            foreach (string key in InheritedAttributes.Keys) Assert.IsNull(d[key], "Page tree nodes carry no inheritable attributes.");
            foreach (PdfObject kid in (PdfArray)d["Kids"]!) Walk(kid);
        }

        Walk(catalog["Pages"]!);
        Assert.AreEqual(pages.Count, Int(lindict["N"]));
        Assert.AreEqual(pages[0], Int(lindict["O"]));

        // Object extents: from "n 0 obj" to after endobj and the white space that follows.
        long Offset(int n) => file.Xref[n].Field1;
        long Length(int n)
        {
            long start = Offset(n);
            int end = text.IndexOf("endobj", (int)start, StringComparison.Ordinal) + 6;
            while (end < pdf.Length && PdfSyntax.IsWhitespace(pdf[end])) end++;
            return end - start;
        }

        // /T: the white space before the entry for object 0 of the main cross-reference table.
        long t = Int(lindict["T"]);
        Assert.IsTrue(PdfSyntax.IsWhitespace(pdf[t]));
        Assert.AreEqual("0000000000 65535 f", text.Substring((int)t + 1, 18));

        // /H: the hint stream, ending exactly where the array says.
        var h = (PdfArray)lindict["H"]!;
        long hintOffset = Int(h[0]), hintLength = Int(h[1]);
        int hintNumber = file.Xref.Single(e => e.Value.Type == XrefEntryType.InFile && e.Value.Field1 == hintOffset).Key;
        Assert.AreEqual(hintLength, Length(hintNumber));
        var hint = (PdfStream)file.GetObject(hintNumber);
        byte[] data;
        using (DecodedStream decoded = StreamCodec.Decode(hint.Data, StreamCodec.GetFilters(hint.Dictionary, file.Resolve), DecodeDepth.Full, 64 << 20, file.Resolve))
        {
            data = decoded.Data.ToArray();
        }

        long Adjusted(long o) => o >= hintOffset ? o + hintLength : o;

        // Page offset hint table.
        var bits = new BitReader(data, 0);
        long minObjects = bits.Read(32), firstPageOffset = bits.Read(32);
        int objectBits = (int)bits.Read(16);
        long minLength = bits.Read(32);
        int lengthBits = (int)bits.Read(16);
        bits.Read(32);
        int contentOffsetBits = (int)bits.Read(16);
        bits.Read(32);
        int contentLengthBits = (int)bits.Read(16);
        int countBits = (int)bits.Read(16), idBits = (int)bits.Read(16), numeratorBits = (int)bits.Read(16);
        bits.Read(16);
        long[] objectsPerPage = bits.Row(pages.Count, objectBits).Select(v => v + minObjects).ToArray();
        long[] lengthPerPage = bits.Row(pages.Count, lengthBits).Select(v => v + minLength).ToArray();
        long[] sharedCounts = bits.Row(pages.Count, countBits);
        long[] ids = bits.Row((int)sharedCounts.Sum(), idBits);
        bits.Row((int)sharedCounts.Sum(), numeratorBits);
        bits.Row(pages.Count, contentOffsetBits);
        bits.Row(pages.Count, contentLengthBits);
        Assert.AreEqual(Offset(pages[0]), Adjusted(firstPageOffset));
        Assert.AreEqual(0, sharedCounts[0], "The first page lists no shared objects.");

        long expectedOffset = Offset(pages[0]);
        for (int i = 0; i < pages.Count; i++)
        {
            Assert.AreEqual(expectedOffset, Offset(pages[i]), $"page {i} starts where the previous one ends");
            long length = 0;
            for (int k = 0; k < objectsPerPage[i]; k++) length += Length(pages[i] + k);
            Assert.AreEqual(lengthPerPage[i], length, $"page {i} length");
            expectedOffset += length;
        }

        // /E: the end of the first page's section.
        Assert.AreEqual(Offset(pages[0]) + lengthPerPage[0], Int(lindict["E"]));

        // Shared object hint table: part 6 from the first page object, then part 8.
        bits = new BitReader(data, (int)Int(hint.Dictionary["S"]));
        long firstShared = bits.Read(32), firstSharedOffset = bits.Read(32), firstPageShared = bits.Read(32), totalShared = bits.Read(32);
        int groupObjectBits = (int)bits.Read(16);
        long minGroup = bits.Read(32);
        int groupBits = (int)bits.Read(16);
        long[] groupLengths = bits.Row((int)totalShared, groupBits).Select(v => v + minGroup).ToArray();
        Assert.IsTrue(bits.Row((int)totalShared, 1).All(v => v == 0), "No MD5 signatures.");
        long[] groupObjects = bits.Row((int)totalShared, groupObjectBits);
        Assert.AreEqual(objectsPerPage[0], firstPageShared);
        var sharedObjects = new List<int>();
        int current = pages[0];
        for (int i = 0; i < totalShared; i++)
        {
            if (i == firstPageShared)
            {
                current = (int)firstShared;
                Assert.AreEqual(Offset(current), Adjusted(firstSharedOffset));
            }

            Assert.AreEqual(0, groupObjects[i]);
            Assert.AreEqual(groupLengths[i], Length(current), $"shared object {i} length");
            sharedObjects.Add(current++);
        }

        // Every shared reference of a later page is an object that page uses.
        int at = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            HashSet<int> used = Reachable(file, pages[i]);
            for (int k = 0; k < sharedCounts[i]; k++)
            {
                int shared = sharedObjects[(int)ids[at++]];
                Assert.Contains(shared, used, $"page {i} uses shared object {shared}");
            }
        }

        // Outline hint table, when bookmarks exist.
        if (catalog["Outlines"] is PdfReference outlines)
        {
            Assert.IsNotNull(hint.Dictionary["O"]);
            bits = new BitReader(data, (int)Int(hint.Dictionary["O"]));
            long firstOutline = bits.Read(32), outlineOffset = bits.Read(32), outlineObjects = bits.Read(32), outlineLength = bits.Read(32);
            Assert.AreEqual(outlines.Number, firstOutline);
            Assert.AreEqual(Offset(outlines.Number), Adjusted(outlineOffset));
            long length = 0;
            for (int k = 0; k < outlineObjects; k++) length += Length(outlines.Number + k);
            Assert.AreEqual(outlineLength, length);
        }
    }

    /// <summary>Objects a page reaches without entering another page or going back up the tree.</summary>
    private static HashSet<int> Reachable(PdfFile file, int page)
    {
        var found = new HashSet<int> { page };
        var pending = new Stack<PdfObject>();
        pending.Push(file.GetObject(page));
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case PdfReference r when !found.Contains(r.Number):
                    PdfObject target = file.GetObject(r.Number);
                    if (target is PdfDictionary { } other && other.GetName("Type") == "Page") break;
                    found.Add(r.Number);
                    pending.Push(target);
                    break;
                case PdfArray a:
                    foreach (PdfObject item in a.Items) pending.Push(item);
                    break;
                case PdfStream s:
                    pending.Push(s.Dictionary);
                    break;
                case PdfDictionary d:
                    foreach (string key in d.Keys)
                    {
                        if (key != "Parent") pending.Push(d[key]!);
                    }

                    break;
            }
        }

        return found;
    }

    /// <summary>Reads hint table fields most significant bit first; each row ends on a byte boundary.</summary>
    private sealed class BitReader(byte[] data, int start)
    {
        private long _bit = start * 8L;

        public long Read(int bits)
        {
            long value = 0;
            for (int i = 0; i < bits; i++, _bit++) value = (value << 1) | (uint)((data[_bit >> 3] >> (7 - (int)(_bit & 7))) & 1);
            return value;
        }

        public long[] Row(int count, int bits)
        {
            long[] values = new long[count];
            for (int i = 0; i < count; i++) values[i] = Read(bits);
            _bit = (_bit + 7) & ~7L;
            return values;
        }
    }
}
