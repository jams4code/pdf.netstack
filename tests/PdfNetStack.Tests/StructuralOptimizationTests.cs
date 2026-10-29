using System.Text;
using PdfNetStack.Content;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Resource pruning, content stream compaction and font program merging, with their proofs.</summary>
[TestClass]
public sealed class StructuralOptimizationTests
{
    private static readonly PdfOptimizerOptions NoThreshold = new() { MinSavingPercent = 0 };

    private static (byte[] Output, PdfOptimizationResult Result) Run(byte[] input, PdfOptimizerOptions? options = null) =>
        PdfOptimizer.Optimize(input, options ?? NoThreshold);

    private static PdfFile Open(byte[] pdf) => PdfFile.Open(pdf, 64 << 20);

    private static byte[] Repeat(string text, int times) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, times)));

    private static PdfDictionary Page(PdfFile file, int index = 0)
    {
        var kids = (PdfArray)file.ResolveDictionary(file.ResolveDictionary(file.Trailer["Root"])!["Pages"])!["Kids"]!;
        return file.ResolveDictionary(kids[index])!;
    }

    private static PdfDictionary Category(PdfFile file, PdfDictionary holder, string category) =>
        (PdfDictionary)file.Resolve(file.ResolveDictionary(holder["Resources"])![category])!;

    private static void AssertVerified(byte[] input, byte[] output, PdfOptimizationResult result)
    {
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
    }

    private static string Decoded(PdfFile file, PdfStream stream)
    {
        using Filters.DecodedStream d = Filters.StreamCodec.Decode(stream.Data, Filters.StreamCodec.GetFilters(stream.Dictionary, file.Resolve),
            Filters.DecodeDepth.Full, 64 << 20, file.Resolve);
        return Compat.Latin1.GetString(d.Data.Span);
    }

    // --- Resource pruning -------------------------------------------------------------------

    [TestMethod]
    public void UnusedFontsImagesAndStatesArePruned()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R/F2 6 0 R>>"
            + "/XObject<</Im1 7 0 R/Im2 8 0 R>>/ExtGState<</GS1<</CA 0.5>>/GS2<</ca 0.5>>>>/ColorSpace<</DefaultRGB/DeviceRGB/CS9[/ICCBased 9 0 R]>>>>>>");
        b.SetStream(4, "", "q /GS1 gs 10 0 0 10 0 0 cm /Im1 Do Q BT /F1 12 Tf (Hi) Tj ET"u8.ToArray());
        b.Set(5, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.Set(6, "<</Type/Font/Subtype/Type1/BaseFont/Courier>>");
        b.SetStream(7, "/Type/XObject/Subtype/Image/Width 2/Height 2/ColorSpace/DeviceGray/BitsPerComponent 8", [1, 2, 3, 4]);
        b.SetStream(8, "/Type/XObject/Subtype/Image/Width 40/Height 40/ColorSpace/DeviceGray/BitsPerComponent 8", new byte[1600]);
        b.SetStream(9, "/N 1", Repeat("icc profile bytes ", 100));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(4, result.Statistics.ResourcesPruned);
        PdfFile file = Open(output);
        PdfDictionary page = Page(file);
        CollectionAssert.AreEquivalent(new[] { "F1" }, Category(file, page, "Font").Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "Im1" }, Category(file, page, "XObject").Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "GS1" }, Category(file, page, "ExtGState").Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "DefaultRGB" }, Category(file, page, "ColorSpace").Keys.ToArray(), "Default color spaces are used implicitly.");
        Assert.AreEqual(-1, Compat.Latin1.GetString(output).IndexOf("Courier", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PruningFollowsFormsPatternsGlyphProceduresAndAppearances()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");

        // Inherited resources: the form has none of its own, so its names count against the page tree's.
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1/Resources<</XObject<</Fm1 5 0 R/Unused 6 0 R>>/Font<</F1 7 0 R/T3 8 0 R>>/Pattern<</P1 10 0 R>>/Shading<</Sh1 12 0 R/Sh2 12 0 R>>>>>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Annots[13 0 R]>>");
        b.SetStream(4, "", "/Fm1 Do /Pattern cs /P1 scn 0 0 10 10 re f BT /T3 1 Tf (a) Tj ET"u8.ToArray());
        b.SetStream(5, "/Type/XObject/Subtype/Form/BBox[0 0 10 10]", "BT /F1 9 Tf (x) Tj ET /Sh1 sh"u8.ToArray());
        b.SetStream(6, "/Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", [0]);
        b.Set(7, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.Set(8, "<</Type/Font/Subtype/Type3/FontBBox[0 0 1 1]/FontMatrix[1 0 0 1 0 0]/CharProcs<</a 9 0 R>>/Encoding<</Differences[97/a]>>/FirstChar 97/LastChar 97/Widths[1]"
            + "/Resources<</XObject<</Glyph 6 0 R/Spare 6 0 R>>>>>>");
        b.SetStream(9, "", "1 0 d0 /Glyph Do"u8.ToArray());
        b.SetStream(10, "/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 4 4]/XStep 4/YStep 4/Resources<</ExtGState<</G1<</LW 2>>/G2<</LW 3>>>>>>", "/G1 gs 0 0 2 2 re f"u8.ToArray());
        b.Set(12, "<</ShadingType 2/ColorSpace/DeviceGray/Coords[0 0 1 0]/Function<</FunctionType 2/Domain[0 1]/N 1>>>>");
        b.Set(13, "<</Type/Annot/Subtype/Square/Rect[0 0 10 10]/AP<</N 14 0 R>>>>");
        b.SetStream(14, "/Type/XObject/Subtype/Form/BBox[0 0 10 10]/Resources<</ExtGState<</A<</CA 1>>/B<</CA 0>>>>>>", "/A gs 0 0 10 10 re S"u8.ToArray());
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 50));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        PdfFile file = Open(output);
        PdfDictionary pages = file.ResolveDictionary(file.ResolveDictionary(file.Trailer["Root"])!["Pages"])!;
        CollectionAssert.AreEquivalent(new[] { "Fm1" }, Category(file, pages, "XObject").Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "F1", "T3" }, Category(file, pages, "Font").Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "Sh1" }, Category(file, pages, "Shading").Keys.ToArray());
        PdfDictionary type3 = file.ResolveDictionary(Category(file, pages, "Font")["T3"])!;
        CollectionAssert.AreEquivalent(new[] { "Glyph" }, Category(file, type3, "XObject").Keys.ToArray());
        PdfStream pattern = (PdfStream)file.Resolve(Category(file, pages, "Pattern")["P1"])!;
        CollectionAssert.AreEquivalent(new[] { "G1" }, Category(file, pattern.Dictionary, "ExtGState").Keys.ToArray());
        PdfStream appearance = (PdfStream)file.Resolve(file.ResolveDictionary(file.ResolveDictionary(((PdfArray)Page(file)["Annots"]!)[0])!["AP"])!["N"])!;
        CollectionAssert.AreEquivalent(new[] { "A" }, Category(file, appearance.Dictionary, "ExtGState").Keys.ToArray());
    }

    [TestMethod]
    public void ResourcesSharedWithFormDefaultsOrUnreadableContentAreKept()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[]/DR 6 0 R>>>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 7 0 R/Resources 6 0 R>>");
        b.Set(4, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 8 0 R/Resources<</Font<</F1 5 0 R/F2 5 0 R>>>>>>");
        b.Set(5, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.Set(6, "<</Font<</Helv 5 0 R/Unused 5 0 R>>>>");
        b.SetStream(7, "", "BT /Helv 12 Tf (a) Tj ET"u8.ToArray());
        b.SetStream(8, "/Filter/FlateDecode", [1, 2, 3, 4, 5]);
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 50));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(0, result.Statistics.ResourcesPruned);
        PdfFile file = Open(output);
        Assert.HasCount(2, Category(file, Page(file, 0), "Font"));
        Assert.HasCount(2, Category(file, Page(file, 1), "Font"));
    }

    [TestMethod]
    public void NamesMentionedAnywhereInContentAreKept()
    {
        var b = PdfBuilder.Simple("x");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 3 0 R/F9 3 0 R/F8 3 0 R>>>>>>");
        b.SetStream(4, "", "BT /F1 24 Tf 72 700 Td (see /F9 here) Tj ET % /F8 in a comment\n"u8.ToArray());
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 50));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        PdfFile file = Open(output);
        CollectionAssert.AreEquivalent(new[] { "F1", "F9", "F8" }, Category(file, Page(file), "Font").Keys.ToArray());
    }

    [TestMethod]
    public void NamesAFormLacksCountAgainstTheResourcesThatInvokedIt()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R"
            + "/Resources<</XObject<</Fm1 5 0 R>>/Font<</F1 6 0 R/F2 6 0 R/F3 6 0 R>>>>>>");
        b.SetStream(4, "", "/Fm1 Do"u8.ToArray());

        // The form defines F1 itself but names F2 too, which only the page has.
        b.SetStream(5, "/Type/XObject/Subtype/Form/BBox[0 0 10 10]/Resources<</Font<</F1 6 0 R>>>>", "BT /F1 9 Tf (x) Tj /F2 9 Tf (y) Tj ET"u8.ToArray());
        b.Set(6, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 50));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        PdfFile file = Open(output);
        CollectionAssert.AreEquivalent(new[] { "F2" }, Category(file, Page(file), "Font").Keys.ToArray());
    }

    [TestMethod]
    public void NamesSplitAcrossContentPartsAreKept()
    {
        var b = PdfBuilder.Simple("x");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R/F12 3 0 R/F9 3 0 R>>>>/Contents[4 0 R 6 0 R]>>");
        b.SetStream(4, "", "BT /F1"u8.ToArray());
        b.SetStream(6, "", "2 24 Tf (x) Tj ET"u8.ToArray());
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 50));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        PdfFile file = Open(output);
        CollectionAssert.AreEquivalent(new[] { "F1", "F12" }, Category(file, Page(file), "Font").Keys.ToArray());
    }

    [TestMethod]
    public void VerifierRejectsRemovalOfAUsedResource()
    {
        PdfBuilder Build(string fonts)
        {
            var b = PdfBuilder.Simple("x");
            b.Set(5, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<<{fonts}>>>>>>");
            return b;
        }

        byte[] original = Build("/F1 3 0 R/F2 3 0 R").Build();
        Assert.IsNull(PdfEquivalence.FindDifference(original, Build("/F1 3 0 R").Build()), "F2 is never named.");
        Assert.IsNotNull(PdfEquivalence.FindDifference(original, Build("/F2 3 0 R").Build()), "F1 is shown with Tf.");
    }

    [TestMethod]
    public void PruningCanBeTurnedOff()
    {
        var b = PdfBuilder.Simple("x");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 3 0 R/F2 3 0 R>>>>>>");
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 50));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { PruneUnusedResources = false });

        AssertVerified(input, output, result);
        Assert.AreEqual(0, result.Statistics.ResourcesPruned);
        Assert.HasCount(2, Category(Open(output), Page(Open(output)), "Font"));
    }

    [TestMethod]
    public void StructuralPassesWorkAlongsideTheLossyImageProfile()
    {
        // A 600 dpi photograph drawn small, an unused copy of it, an unused font and verbose content.
        Imaging.Raster photo = SyntheticImages.Photo(1200, 900, 3);
        string image = $"/Type/XObject/Subtype/Image/Width 1200/Height 900/ColorSpace{SyntheticImages.ColorSpace(3)}/BitsPerComponent 8/Filter/FlateDecode";
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</XObject<</Im0 10 0 R/Spare 11 0 R>>/Font<</F1 5 0 R/F2 6 0 R>>>>>>");
        b.SetStream(4, "", Repeat("q   144.000  0  0  108.000  50.00  50.00  cm   /Im0  Do   Q   % the photograph\n   BT  /F1  12.000  Tf  72  700  Td  (Both)  Tj  ET\n", 20));
        b.Set(5, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.Set(6, "<</Type/Font/Subtype/Type1/BaseFont/Courier>>");
        b.SetStream(10, image, PdfBuilder.Deflate(photo.Samples));
        b.SetStream(11, image, PdfBuilder.Deflate(photo.Samples));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { MaxImageDpi = 150 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesDownsampled);
        Assert.AreEqual(2, result.Statistics.ResourcesPruned);
        Assert.AreEqual(1, result.Statistics.ContentStreamsCompacted);

        // The public check applies both rule sets: the resampled image passes only with its gate.
        Assert.IsNull(PdfEquivalence.FindDifference(input, output, 64 << 20, false, 0.99));
        Assert.IsNotNull(PdfEquivalence.FindDifference(input, output));
    }

    // --- Content stream compaction ----------------------------------------------------------

    [TestMethod]
    public void ContentStreamsAreCompactedTokenForToken()
    {
        var b = PdfBuilder.Simple("x");
        byte[] content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
            "q   1.000 0.0000 0 +1.50 -0.0 00012.250 cm % a comment\n  BT /F1   24.0 Tf [ (A) -120.00 <42> ] TJ ET Q\n", 40)));
        b.SetStream(4, "", content);
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { RecompressStreams = false });

        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.ContentStreamsCompacted);
        PdfFile file = Open(output);
        string compacted = Decoded(file, (PdfStream)file.Resolve(Page(file)["Contents"])!);
        StringAssert.StartsWith(compacted, "q 1 0 0 1.5 0 12.25 cm BT/F1 24 Tf[(A)-120(B)]TJ ET Q q ");
        Assert.AreEqual(-1, compacted.IndexOf('%'));
        StringAssert.EndsWith(compacted, "Q\n", "A trailing separator is kept for readers that join content streams.");
    }

    [TestMethod]
    public void CompactionKeepsStreamEdgesWhereReadersJoinParts()
    {
        var b = PdfBuilder.Simple("x");
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents[4 0 R 6 0 R]>>");
        b.SetStream(4, "", Encoding.ASCII.GetBytes("q " + string.Concat(Enumerable.Repeat("  1.50   0  0  1  0  0  cm  ", 30)) + "1.0"));
        b.SetStream(6, "", Encoding.ASCII.GetBytes("0 0 1 0 0 cm" + string.Concat(Enumerable.Repeat("   0   0   m   10   10   l   S  ", 30)) + "Q"));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        PdfFile file = Open(output);
        var parts = (PdfArray)file.Resolve(Page(file)["Contents"])!;
        string first = Decoded(file, (PdfStream)file.Resolve(parts[0])!);
        string second = Decoded(file, (PdfStream)file.Resolve(parts[1])!);
        StringAssert.EndsWith(first, " 1.0", "The last token touches the next part and keeps its exact bytes.");
        StringAssert.StartsWith(second, "0 0 1 0 0 cm 0 0 m");
    }

    [TestMethod]
    public void InlineImagesAreCopiedExactly()
    {
        // Four samples each. The second image's read "EI " and a byte, which a reader searching
        // for EI would take as the end of the data.
        byte[] clear = [.. "q BI /W 4 /H 1 /BPC 8 /CS /G ID "u8, 0x45, 0x58, 0x20, 0x01, .. "\nEI   Q   "u8];
        byte[] ambiguous = [.. "q BI /W 4 /H 1 /BPC 8 /CS /G ID "u8, 0x45, 0x49, 0x20, 0x01, .. "\nEI   Q   "u8];
        byte[] Content(byte[] image) => [.. Repeat("  0   0   m   10   10   l   S  ", 20), .. image, .. Repeat("  0   0   m   10   10   l   S  ", 20)];

        Assert.IsNull(ContentCompactor.FindDifference(Content(clear), ContentCompactor.Compact(Content(clear))!));
        string compacted = Compat.Latin1.GetString(ContentCompactor.Compact(Content(clear))!);
        StringAssert.Contains(compacted, "q BI/W 4/H 1/BPC 8/CS/G ID EX \u0001\nEI Q");

        byte[] withAmbiguous = Content(ambiguous);
        byte[] result = ContentCompactor.Compact(withAmbiguous)!;
        Assert.IsNull(ContentCompactor.FindDifference(withAmbiguous, result));
        int bi = Compat.Latin1.GetString(withAmbiguous).IndexOf("BI", StringComparison.Ordinal);
        StringAssert.EndsWith(Compat.Latin1.GetString(result), Compat.Latin1.GetString(withAmbiguous).Substring(bi), "Everything from an ambiguous inline image on stays as it was.");
    }

    [TestMethod]
    public void TokenComparisonCatchesEveryRealChange()
    {
        byte[] original = "q 1 0 0 1 10 20 cm BT /F1 12 Tf (Hello) Tj ET Q\n"u8.ToArray();
        Assert.IsNull(ContentCompactor.FindDifference(original, "q 1 0 0 1 10 20 cm BT/F1 12 Tf(Hello)Tj ET Q\n"u8.ToArray()));
        Assert.IsNull(ContentCompactor.FindDifference(original, "q 1.0 0 0 +1 10 20.000 cm BT /F#31 12 Tf <48656C6C6F> Tj ET Q\n"u8.ToArray()));
        Assert.IsNotNull(ContentCompactor.FindDifference(original, "q 1 0 0 1 10 21 cm BT /F1 12 Tf (Hello) Tj ET Q\n"u8.ToArray()));
        Assert.IsNotNull(ContentCompactor.FindDifference(original, "q 1 0 0 1 10 20 cm BT /F2 12 Tf (Hello) Tj ET Q\n"u8.ToArray()));
        Assert.IsNotNull(ContentCompactor.FindDifference(original, "q 1 0 0 1 10 20 cm BT /F1 12 Tf (Hellp) Tj ET Q\n"u8.ToArray()));
        Assert.IsNotNull(ContentCompactor.FindDifference(original, "q 1 0 0 1 10 20 cm BT /F1 12 Tf (Hello) TJ ET Q\n"u8.ToArray()));
        Assert.IsNotNull(ContentCompactor.FindDifference(original, "q 1 0 0 1 10 20 cm BT /F1 12 Tf (Hello) Tj ET\n"u8.ToArray()));
        Assert.IsNotNull(ContentCompactor.FindDifference(original, "q 1 0 0 1 10 20 cm BT /F1 12 Tf (Hello) Tj ET Q"u8.ToArray()), "The trailing separator matters.");
    }

    [TestMethod]
    public void CompactionRoundTripsRandomContent()
    {
        var random = new Random(17);
        string[] pieces = ["q", "Q", "1", "-2.50", "+.5", "0.000", "/F1", "/A#20B", "(x\\) y)", "<414>", "[", "]", "<<", ">>", "Tj", "TJ", "cm", "% note\n", "\n", "\t", "  ", "true", "null"];
        for (int round = 0; round < 300; round++)
        {
            var sb = new StringBuilder();
            int count = random.Next(1, 60);
            for (int i = 0; i < count; i++)
            {
                sb.Append(pieces[random.Next(pieces.Length)]);
                if (random.Next(3) > 0) sb.Append(' ');
            }

            byte[] content = Encoding.ASCII.GetBytes(sb.ToString());
            byte[]? compacted = ContentCompactor.Compact(content);
            if (compacted is null) continue;
            Assert.IsLessThan(content.Length, compacted.Length);
            Assert.IsNull(ContentCompactor.FindDifference(content, compacted), sb.ToString());
        }
    }

    [TestMethod]
    public void StreamsThatAreNotOnlyContentAreNotCompacted()
    {
        var b = PdfBuilder.Simple("x");
        byte[] content = Repeat("  0   0   m   10   10   l   S  ", 40);
        b.SetStream(4, "", content);

        // The same stream is also an embedded file, whose bytes must not change.
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles<</Names[(a.txt)<</Type/Filespec/F(a.txt)/EF<</F 4 0 R>>>>]>>>>>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(0, result.Statistics.ContentStreamsCompacted);
        PdfFile file = Open(output);
        CollectionAssert.AreEqual(content, Compat.Latin1.GetBytes(Decoded(file, (PdfStream)file.Resolve(Page(file)["Contents"])!)));
    }

    // --- Decoded content deduplication and font program merging ---------------------------

    [TestMethod]
    public void StreamsEqualAfterDecodingAreMerged()
    {
        var b = PdfBuilder.Simple("x");
        byte[] program = Repeat("font program bytes ", 200);
        b.SetStream(6, $"/Length1 {program.Length}", program);
        b.SetStream(7, $"/Length1 {program.Length}/Filter/FlateDecode", PdfBuilder.Deflate(program));
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/X[6 0 R 7 0 R]>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { RecompressStreams = false, CompactContentStreams = false });
        Assert.AreEqual(0, result.Statistics.ObjectsDeduplicated, "Without decoding, the two encodings differ.");

        (output, result) = Run(input);
        AssertVerified(input, output, result);
        Assert.AreEqual(1, result.Statistics.ObjectsDeduplicated);
    }

    private static byte[] FontFixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Regular.ttf"));

    private static byte[] TextDocument(PdfOpenTypeFont font, string text)
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 300, 100));
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString(text, font, 24, PdfColor.Rgb(0, 0, 0), 10, 50);
        }

        return doc.ToArray(new PdfSaveOptions { Optimize = false });
    }

    private static byte[] Merged(params byte[][] documents)
    {
        PdfDocument merged = PdfDocument.Merge(documents.Select(d => PdfDocument.Open(d)));
        return merged.ToArray(new PdfSaveOptions { Optimize = false });
    }

    private static List<PdfStream> FontPrograms(byte[] pdf)
    {
        PdfFile file = Open(pdf);
        var programs = new List<PdfStream>();
        foreach ((int number, XrefEntry e) in file.Xref)
        {
            if (e.Type != XrefEntryType.Free && file.GetObject(number) is PdfDictionary d && d["FontFile2"] is { } ff && file.Resolve(ff) is PdfStream s) programs.Add(s);
        }

        return programs;
    }

    [TestMethod]
    public void SubsetsOfOneTrueTypeFontAreMerged()
    {
        PdfOpenTypeFont font = PdfOpenTypeFont.Load(FontFixture());
        byte[] input = Merged(TextDocument(font, "AVo"), TextDocument(font, "THx Á"), TextDocument(font, "AHA"));
        Assert.HasCount(3, FontPrograms(input));

        (byte[] separate, PdfOptimizationResult kept) = Run(input, NoThreshold with { MergeFontSubsets = false });
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(0, kept.Statistics.FontProgramsMerged);
        Assert.AreEqual(3, result.Statistics.FontProgramsMerged);
        Assert.HasCount(1, FontPrograms(output).Distinct().ToList(), "The descriptors share one merged program.");
        Assert.IsLessThan(separate.Length, output.Length);
    }

    [TestMethod]
    public void SubsetShowingAGlyphItDoesNotHoldIsNotMerged()
    {
        PdfOpenTypeFont font = PdfOpenTypeFont.Load(FontFixture());
        byte[] first = TextDocument(font, "AVo");

        // Show glyph 6 (H), which this subset leaves empty but the other subset holds: merging
        // would make it appear, so the programs must stay apart.
        PdfDocument doc = PdfDocument.Open(first);
        PdfPage page = doc.Pages[0];
        string fontName = ((PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(page.Dictionary["Resources"])!)["Font"])!).Keys[0];
        var stream = (PdfStream)doc.Resolve(page.Dictionary["Contents"])!;
        byte[] content = [.. doc.GetStreamData(stream), .. Encoding.ASCII.GetBytes($"\nBT /{fontName} 24 Tf 10 10 Td <0006> Tj ET\n")];
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData(content)));
        byte[] patched = doc.ToArray(new PdfSaveOptions { Optimize = false });

        byte[] input = Merged(patched, TextDocument(font, "THx"));
        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertVerified(input, output, result);
        Assert.AreEqual(0, result.Statistics.FontProgramsMerged);
    }

    /// <summary>
    /// Two embeddings of the test font, each with one glyph's outline blanked, shown through
    /// identity mapped composite fonts with the given hex codes.
    /// </summary>
    private static byte[] TwoSubsets(int blankInFirst, string codesFirst, int blankInSecond, string codesSecond)
    {
        byte[] font = FontFixture();
        TrueTypeGlyphs glyphs = TrueTypeGlyphs.Parse(font)!;
        byte[] Blank(int gid)
        {
            byte[] copy = (byte[])font.Clone();
            ReadOnlySpan<byte> data = glyphs.Slot(gid).Data.Span;
            copy.AsSpan(font.AsSpan().IndexOf(data), data.Length).Clear();
            return copy;
        }

        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 100]/Contents 4 0 R/Resources<</Font<</T1 5 0 R/T2 9 0 R>>>>>>");
        b.SetStream(4, "", Encoding.ASCII.GetBytes($"BT /T1 24 Tf 10 50 Td <{codesFirst}> Tj /T2 24 Tf <{codesSecond}> Tj ET"));
        int number = 5;
        foreach (byte[] program in new[] { Blank(blankInFirst), Blank(blankInSecond) })
        {
            int type0 = number, cid = number + 1, descriptor = number + 2, file = number + 3;
            b.Set(type0, $"<</Type/Font/Subtype/Type0/BaseFont/Test/Encoding/Identity-H/DescendantFonts[{cid} 0 R]>>");
            b.Set(cid, $"<</Type/Font/Subtype/CIDFontType2/BaseFont/Test/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor {descriptor} 0 R/DW 600/CIDToGIDMap/Identity>>");
            b.Set(descriptor, $"<</Type/FontDescriptor/FontName/Test/Flags 32/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile2 {file} 0 R>>");
            b.SetStream(file, $"/Length1 {program.Length}/Filter/FlateDecode", PdfBuilder.Deflate(program));
            number += 4;
        }

        return b.Build();
    }

    private static int[] OutlinedGlyphs()
    {
        TrueTypeGlyphs glyphs = TrueTypeGlyphs.Parse(FontFixture())!;
        return Enumerable.Range(1, glyphs.GlyphCount - 1).Where(g => glyphs.Slot(g).Data.Length > 0).Take(3).ToArray();
    }

    [TestMethod]
    public void OnlyTheSubsetsShowingAGlyphDecideIt()
    {
        int[] g = OutlinedGlyphs();
        string Hex(params int[] ids) => string.Concat(ids.Select(id => id.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));

        // Each font blanks a glyph only the other shows: the union keeps both outlines.
        byte[] input = TwoSubsets(g[2], Hex(g[0], g[1]), g[0], Hex(g[2]));
        (byte[] output, PdfOptimizationResult result) = Run(input);
        AssertVerified(input, output, result);
        Assert.AreEqual(2, result.Statistics.FontProgramsMerged);
        Assert.HasCount(1, FontPrograms(output).Distinct().ToList());

        // Both show the glyph one of them blanked: they disagree, so they stay apart.
        input = TwoSubsets(g[2], Hex(g[0], g[2]), g[0], Hex(g[2]));
        (output, result) = Run(input);
        AssertVerified(input, output, result);
        Assert.AreEqual(0, result.Statistics.FontProgramsMerged);
    }

    [TestMethod]
    public void MergedProgramCheckFindsChangedGlyphs()
    {
        byte[] font = FontFixture();
        TrueTypeGlyphs original = TrueTypeGlyphs.Parse(font)!;
        Assert.IsNotNull(original);
        Assert.IsNull(TrueTypeGlyphs.FindDifference(font, font, [0, 2, 3]));

        // Flip one byte inside glyph 2's outline.
        GlyphSlot slot = original.Slot(2);
        int at = font.AsSpan().IndexOf(slot.Data.Span);
        byte[] changed = (byte[])font.Clone();
        changed[at + slot.Data.Length - 1] ^= 0x01;
        Assert.IsNull(TrueTypeGlyphs.FindDifference(font, changed, [0, 3]), "Glyph 2 is not shown.");
        Assert.IsNotNull(TrueTypeGlyphs.FindDifference(font, changed, [0, 2, 3]));
        Assert.IsNotNull(TrueTypeGlyphs.FindDifference(font, "not a font"u8.ToArray(), [0]));
    }

    [TestMethod]
    public void AnalysisNeverThrowsOnMutatedInput()
    {
        PdfOpenTypeFont font = PdfOpenTypeFont.Load(FontFixture());
        byte[] seed = Merged(TextDocument(font, "AVo"), TextDocument(font, "THx"));
        var random = new Random(5);
        for (int i = 0; i < 60; i++)
        {
            byte[] mutated = (byte[])seed.Clone();
            for (int k = 0; k < 4; k++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            (byte[] output, PdfOptimizationResult result) = Run(mutated, NoThreshold with { AllowRepairedFiles = true });
            if (result.Disposition == OptimizationDisposition.Optimized) Assert.IsNull(PdfEquivalence.FindDifference(mutated, output), result.Reason);
            else CollectionAssert.AreEqual(mutated, output);
            // A damaged file may still fail to rewrite, but only ever with a format error.
            if (result.Reason.StartsWith("Rewrite failed", StringComparison.Ordinal)) StringAssert.Contains(result.Reason, "(PdfFormatException)");
        }
    }
}
