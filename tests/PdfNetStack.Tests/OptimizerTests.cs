using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class OptimizerTests
{
    private static readonly PdfOptimizerOptions NoThreshold = new() { MinSavingPercent = 0 };

    private static (byte[] Output, PdfOptimizationResult Result) Run(byte[] input, PdfOptimizerOptions? options = null) =>
        PdfOptimizer.Optimize(input, options ?? NoThreshold);

    private static PdfFile Open(byte[] pdf) => PdfFile.Open(pdf, 64 << 20);

    private static void AssertOptimizedAndEquivalent(byte[] input, byte[] output, PdfOptimizationResult result)
    {
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.IsLessThan(input.Length, output.Length);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
        PdfFile file = Open(output);
        Assert.IsFalse(file.WasReconstructed);
        Assert.AreEqual(0, file.LengthsRepaired);
    }

    private static byte[] Repeat(string text, int times) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, times)));

    [TestMethod]
    public void SimpleDocumentIsRewrittenWithObjectStreams()
    {
        PdfBuilder b = PdfBuilder.Simple("one", "two", "three");
        b.SetStream(4, "", Repeat("0 0 m 100 100 l S\n", 200));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.AreEqual(3, result.PageCount);
        string text = Compat.Latin1.GetString(output);
        StringAssert.StartsWith(text, "%PDF-1.5");
        StringAssert.Contains(text, "/Type/ObjStm");
        StringAssert.Contains(text, "/Type/XRef");
        Assert.AreEqual(result.OutputSha256, Compat.ToHex(Compat.Sha256(output)).ToLowerInvariant());
    }

    [TestMethod]
    public void UnreachableObjectsAreRemoved()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        b.AddStream("", Repeat("orphan data that nothing references ", 300));
        b.Add("<</Orphan true>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.AreEqual(2, result.Statistics.UnreachableRemoved);
        Assert.AreEqual(-1, Compat.Latin1.GetString(output).IndexOf("Orphan", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IdenticalImagesAndFontsAreMerged()
    {
        var b = new PdfBuilder();
        byte[] pixels = new byte[64 * 64 * 3];
        new Random(3).NextBytes(pixels);
        string imageDict = "/Type/XObject/Subtype/Image/Width 64/Height 64/ColorSpace/DeviceRGB/BitsPerComponent 8";
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Resources<</XObject<</Im0 5 0 R>>/Font<</F1 9 0 R>>>>/Contents 7 0 R>>");
        b.Set(4, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Resources<</XObject<</Im0 6 0 R>>/Font<</F1 10 0 R>>>>/Contents 8 0 R>>");
        b.SetStream(5, imageDict, pixels);
        b.SetStream(6, imageDict, pixels);
        b.SetStream(7, "", "q 64 0 0 64 0 0 cm /Im0 Do Q"u8.ToArray());
        b.SetStream(8, "", "q 32 0 0 32 0 0 cm /Im0 Do Q"u8.ToArray());
        b.Set(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        b.Set(10, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.AreEqual(2, result.Statistics.ObjectsDeduplicated);
        Assert.IsLessThan((int)(pixels.Length * 1.2), output.Length);
    }

    [TestMethod]
    public void PagesAndAnnotationsWithEqualContentAreNeverMerged()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Annots[5 0 R 6 0 R]>>");
        b.Set(4, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]>>");
        b.Set(5, "<</Type/Annot/Subtype/Link/Rect[0 0 10 10]/Border[0 0 0]>>");
        b.Set(6, "<</Type/Annot/Subtype/Link/Rect[0 0 10 10]/Border[0 0 0]>>");
        b.AddStream("", Repeat("padding so the rewrite is smaller ", 100));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.AreEqual(0, result.Statistics.ObjectsDeduplicated);
        PdfFile file = Open(output);
        var pages = (PdfArray)file.ResolveDictionary(file.ResolveDictionary(file.Trailer["Root"])!["Pages"])!["Kids"]!;
        Assert.AreNotEqual(((PdfReference)pages[0]).Number, ((PdfReference)pages[1]).Number);
        var annots = (PdfArray)file.ResolveDictionary(pages[0])!["Annots"]!;
        Assert.AreNotEqual(((PdfReference)annots[0]).Number, ((PdfReference)annots[1]).Number);
    }

    [TestMethod]
    public void TextEncodingsAndLzwBecomeFlate()
    {
        byte[] content = Repeat("BT /F1 12 Tf 10 10 Td (repeated text) Tj ET\n", 100);
        string hex = Compat.ToHex(content) + ">";
        PdfBuilder b = PdfBuilder.Simple("x");
        b.SetStream(4, "/Filter/ASCIIHexDecode", Encoding.ASCII.GetBytes(hex));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        PdfFile file = Open(output);
        var stream = (PdfStream)file.Resolve(file.ResolveDictionary(file.GetObject(FindPage(file)))!["Contents"])!;
        Assert.AreEqual("FlateDecode", stream.Dictionary.GetName("Filter"));
    }

    [TestMethod]
    public void RawImageGetsPngPredictorWhenSmaller()
    {
        var b = PdfBuilder.Simple("x");
        var pixels = new byte[200 * 100 * 3];
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                int o = (y * 200 + x) * 3;
                pixels[o] = (byte)(x + y);
                pixels[o + 1] = (byte)(x * 2);
                pixels[o + 2] = (byte)(y * 3);
            }
        }

        int image = b.AddStream("/Type/XObject/Subtype/Image/Width 200/Height 100/ColorSpace/DeviceRGB/BitsPerComponent 8", pixels);
        b.Set(5, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</I {image} 0 R>>>>/Contents 4 0 R>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        StringAssert.Contains(Compat.Latin1.GetString(output), "/Predictor 15");
    }

    [TestMethod]
    public void JpegBytesAreNeverTouched()
    {
        var b = PdfBuilder.Simple("x");
        var jpeg = new byte[5000];
        new Random(9).NextBytes(jpeg);
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        int image = b.AddStream("/Type/XObject/Subtype/Image/Width 10/Height 10/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/DCTDecode", jpeg);
        b.Set(5, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</I {image} 0 R>>>>/Contents 4 0 R>>");
        b.AddStream("", Repeat("padding ", 500));
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/PieceInfo<</X<</Private 11 0 R>>>>>>");
        b.SetStream(11, "", Repeat("padding ", 500));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.IsGreaterThanOrEqualTo(0, output.AsSpan().IndexOf(jpeg));
    }

    [TestMethod]
    public void EncryptedFileIsReturnedUnchanged()
    {
        PdfBuilder b = PdfBuilder.Simple("secret");
        b.Add("<</Filter/Standard/V 2/R 3/O(x)/U(y)/P -4>>");
        byte[] input = b.Build(trailerExtra: "/Encrypt 6 0 R/ID[<01><01>]");

        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
        Assert.AreEqual(result.SourceSha256, result.OutputSha256);
    }

    [TestMethod]
    public void SignedFileIsReturnedUnchanged()
    {
        PdfBuilder b = PdfBuilder.Simple("signed");
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[7 0 R]/SigFlags 3>>>>");
        b.Set(7, "<</FT/Sig/T(Signature1)/V 8 0 R/Rect[0 0 0 0]>>");
        b.Set(8, "<</Type/Sig/Filter/Adobe.PPKLite/Contents<00>/ByteRange[0 1 2 3]>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void SignatureInsideObjectStreamIsStillDetected()
    {
        PdfBuilder b = PdfBuilder.Simple("signed");
        b.Set(1, "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[7 0 R]>>>>");
        b.Set(7, "<</FT/Sig/T(S)/V 8 0 R>>");
        b.Set(8, "<</Type/Sig/Contents<00>>>");
        byte[] input = b.BuildWithXrefStream(new HashSet<int> { 1, 2, 7, 8 });

        (byte[] output, PdfOptimizationResult result) = Run(input);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition, result.Reason);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void IncrementalUpdatesAreRetainedByDefaultAndFlattenedOnRequest()
    {
        PdfBuilder b = PdfBuilder.Simple("Old");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        byte[] v1 = b.Build();
        byte[] v2 = PdfBuilder.AppendUpdate(v1, new Dictionary<int, string>
        {
            [6] = "<</Length 34>>\nstream\nBT /F1 24 Tf 72 700 Td (New) Tj ET\nendstream",
            [5] = "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 6 0 R>>",
        }, size: 7);

        (byte[] retained, PdfOptimizationResult r1) = Run(v2);
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, r1.Disposition);
        CollectionAssert.AreEqual(v2, retained);

        (byte[] flattened, PdfOptimizationResult r2) = Run(v2, NoThreshold with { AllowIncrementalUpdates = true });
        AssertOptimizedAndEquivalent(v2, flattened, r2);
        PdfFile file = Open(flattened);
        Assert.AreEqual(1, file.RevisionCount);
        var page = file.ResolveDictionary(file.GetObject(FindPage(file)))!;
        StringAssert.Contains(Encoding.ASCII.GetString(((PdfStream)file.Resolve(page["Contents"])!).Data.Span), "(New)");
    }

    [TestMethod]
    public void PdfA1KeepsClassicXrefAndVersion()
    {
        PdfBuilder b = PdfBuilder.Simple("archival");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        const string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF><rdf:Description xmlns:pdfaid='http://www.aiim.org/pdfa/ns/id/' pdfaid:part='1' pdfaid:conformance='B'/></rdf:RDF></x:xmpmeta>";
        int meta = b.AddStream("/Type/Metadata/Subtype/XML", Encoding.ASCII.GetBytes(xmp));
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/Metadata {meta} 0 R>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);

        AssertOptimizedAndEquivalent(input, output, result);
        string text = Compat.Latin1.GetString(output);
        StringAssert.StartsWith(text, "%PDF-1.4");
        Assert.AreEqual(-1, text.IndexOf("/ObjStm", StringComparison.Ordinal));
        StringAssert.Contains(text, "xref\n0 ");
        StringAssert.Contains(text, "pdfaid:part='1'");
    }

    [TestMethod]
    public void BelowThresholdKeepsOriginalBytes()
    {
        byte[] input = PdfBuilder.Simple("x").Build();
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinSavingPercent = 99 });
        Assert.AreEqual(OptimizationDisposition.OriginalRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void EmptyAndGarbageInputNeverThrow()
    {
        foreach (byte[] input in new[] { Array.Empty<byte>(), "not a pdf"u8.ToArray(), "%PDF-1.7\n"u8.ToArray() })
        {
            (byte[] output, PdfOptimizationResult result) = Run(input);
            Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
            CollectionAssert.AreEqual(input, output);
        }
    }

    [TestMethod]
    public void DecompressionBombStreamIsCarriedOverUntouched()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        byte[] bomb = PdfBuilder.Deflate(new byte[8 << 20]);
        int s = b.AddStream("/Filter/FlateDecode", bomb);
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/Bomb {s} 0 R>>");
        b.AddStream("", Repeat("padding ", 2000));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { MaxDecodedStreamBytes = 1 << 20 });

        AssertOptimizedAndEquivalent(input, output, result);
        Assert.IsGreaterThanOrEqualTo(0, output.AsSpan().IndexOf(bomb));
    }

    [TestMethod]
    public void DamagedFileIsRetainedUnlessRepairIsAllowed()
    {
        PdfBuilder b = PdfBuilder.Simple("Hello");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        string text = Compat.Latin1.GetString(b.Build());
        int firstObject = text.IndexOf("1 0 obj", StringComparison.Ordinal);
        byte[] input = Compat.Latin1.GetBytes(text[..firstObject] + "% shifted\n" + text[firstObject..]);

        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, Run(input).Result.Disposition);
        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { AllowRepairedFiles = true });
        AssertOptimizedAndEquivalent(input, output, result);
    }

    [TestMethod]
    public void StrippingOptionsRemoveOnlyTheRequestedData()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        int thumb = b.AddStream("/Width 10/Height 10/ColorSpace/DeviceGray/BitsPerComponent 8", new byte[100]);
        int piece = b.AddStream("", Repeat("illustrator private data ", 200));
        b.Set(5, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents 4 0 R/Thumb {thumb} 0 R/PieceInfo<</Illustrator<</Private {piece} 0 R>>>>>>");
        byte[] input = b.Build();

        (byte[] kept, PdfOptimizationResult r1) = Run(input);
        Assert.AreEqual(OptimizationDisposition.Optimized, r1.Disposition);
        PdfFile keptFile = Open(kept);
        Assert.IsNotNull(keptFile.ResolveDictionary(keptFile.GetObject(FindPage(keptFile)))!["Thumb"]);

        (byte[] stripped, PdfOptimizationResult r2) = Run(input, NoThreshold with { RemovePageThumbnails = true, RemovePieceInfo = true });
        Assert.AreEqual(OptimizationDisposition.Optimized, r2.Disposition, r2.Reason);
        Assert.AreEqual(2, r2.Statistics.EntriesStripped);
        PdfFile strippedFile = Open(stripped);
        PdfDictionary strippedPage = strippedFile.ResolveDictionary(strippedFile.GetObject(FindPage(strippedFile)))!;
        Assert.IsNull(strippedPage["Thumb"]);
        Assert.IsNull(strippedPage["PieceInfo"]);
        Assert.IsNotNull(strippedPage["Contents"]);
        Assert.IsNotNull(PdfEquivalence.FindDifference(input, stripped), "Stripping is not lossless and must be visible to the checker.");
        Assert.IsLessThan(kept.Length, stripped.Length);
    }

    [TestMethod]
    public void EmptyNameFollowedByNumberSurvives()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        b.Set(3, "<</Type/Font/Subtype/Type1/BaseFont/Courier/Encoding<</Differences[32/ 39/quotesingle]>>>>");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input);
        AssertOptimizedAndEquivalent(input, output, result);
    }

    [TestMethod]
    public void StreamsWithParametersWeCannotRebuildAreLeftAlone()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        byte[] odd = PdfBuilder.Deflate(Repeat("odd parameters ", 400));
        int s1 = b.AddStream("/Filter/FlateDecode/DecodeParms<</Predictor 12/Columns 3.5>>", odd);
        int s2 = b.AddStream("/Filter/FlateDecode/DecodeParms<</Predictor 9/Columns 4>>", odd);
        int s3 = b.AddStream("/Filter/FlateDecode/DecodeParms<</Predictor 12/Columns 4/Vendor 1>>", odd);
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/X[{s1} 0 R {s2} 0 R {s3} 0 R]>>");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = Run(input, NoThreshold with { DeduplicateObjects = false });

        AssertOptimizedAndEquivalent(input, output, result);
        string text = Compat.Latin1.GetString(output);
        StringAssert.Contains(text, "/Columns 3.5");
        StringAssert.Contains(text, "/Predictor 9");
        StringAssert.Contains(text, "/Vendor 1");
    }

    [TestMethod]
    public void JunkBeforeHeaderWithAbsoluteOffsetsIsNotDamage()
    {
        PdfBuilder b = PdfBuilder.Simple("x");
        b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
        byte[] clean = b.Build();

        // Prepend three bytes and shift every offset by three, the way a correct producer would.
        string text = Compat.Latin1.GetString(clean);
        string shifted = System.Text.RegularExpressions.Regex.Replace(text, @"(\d{10}) 00000 n",
            m => (long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 3).ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n");
        int sx = shifted.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10;
        int end = shifted.IndexOf('\n', sx);
        long xref = long.Parse(shifted[sx..end], System.Globalization.CultureInfo.InvariantCulture);
        shifted = shifted[..sx] + (xref + 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + shifted[end..];
        byte[] input = Compat.Latin1.GetBytes("ï»¿" + shifted);

        PdfFile file = Open(input);
        Assert.IsFalse(file.WasReconstructed);
        (byte[] output, PdfOptimizationResult result) = Run(input);
        AssertOptimizedAndEquivalent(input, output, result);
    }

    [TestMethod]
    public void CancellationIsHonored()
    {
        byte[] input = PdfBuilder.Simple("x").Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PdfOptimizer.Optimize(input, Stream.Null, NoThreshold, cts.Token));
    }

    [TestMethod]
    public async Task FileApiWritesAtomicallyAndRejectsSamePath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfnetstack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PdfBuilder b = PdfBuilder.Simple("x");
            b.SetStream(4, "", Repeat("0 0 m 1 1 l S\n", 300));
            string source = Path.Combine(dir, "in.pdf");
            string target = Path.Combine(dir, "sub", "out.pdf");
            File.WriteAllBytes(source, b.Build());

            PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync(source, target);

            Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition);
            Assert.AreEqual(result.OutputBytes, new FileInfo(target).Length);
            Assert.HasCount(1, Directory.GetFiles(Path.Combine(dir, "sub")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => PdfOptimizer.OptimizeFileAsync(source, source));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void InspectorReportsStructure()
    {
        PdfBuilder b = PdfBuilder.Simple("a", "b");
        PdfInspection i = PdfInspector.Inspect(b.BuildWithXrefStream(new HashSet<int> { 1, 2 }));
        Assert.AreEqual(2, i.PageCount);
        Assert.IsTrue(i.UsesXrefStreams);
        Assert.IsTrue(i.UsesObjectStreams);
        Assert.IsFalse(i.IsEncrypted);
        Assert.IsFalse(i.NeededRepair);
    }

    private static int FindPage(PdfFile file)
    {
        var kids = (PdfArray)file.ResolveDictionary(file.ResolveDictionary(file.Trailer["Root"])!["Pages"])!["Kids"]!;
        return ((PdfReference)kids[0]).Number;
    }
}
