using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// Scanned pages end to end: bilevel images coded again as Group 4 with the same pixels, bilevel
/// CCITT and JBIG2 images reduced to a target resolution, JPEG 2000 images converted to JPEG, and
/// the proof of each.
/// </summary>
[TestClass]
public sealed class BilevelOptimizationTests
{
    private const long Limit = 64 << 20;
    private static readonly PdfOptimizerOptions NoThreshold = new() { MinSavingPercent = 0 };

    private static string Fixture(string folder, string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name);

    /// <summary>
    /// A scanned text page as 1 bit rows, 0 for black: lines of glyphs made of upright, level,
    /// slanted and round strokes on white, with ragged edges and speckle as a scanner leaves them.
    /// </summary>
    internal static byte[] ScanRows(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        var black = new bool[width * height];
        void Dot(double cx, double cy, double radius)
        {
            for (int y = (int)(cy - radius); y <= (int)(cy + radius); y++)
            {
                for (int x = (int)(cx - radius); x <= (int)(cx + radius); x++)
                {
                    if (x >= 0 && y >= 0 && x < width && y < height && ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius) black[(y * width) + x] = true;
                }
            }
        }

        void Line(double x0, double y0, double x1, double y1, double radius)
        {
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0))) + 1;
            for (int i = 0; i <= steps; i++) Dot(x0 + ((x1 - x0) * i / steps), y0 + ((y1 - y0) * i / steps), radius);
        }

        int lineHeight = Math.Max(12, height / 30), glyph = lineHeight * 3 / 5;
        double pen = Math.Max(1.0, glyph / 14.0);
        for (int top = lineHeight; top + lineHeight < height; top += lineHeight)
        {
            int x = width / 12;
            while (x + glyph < width * 11 / 12)
            {
                int w = (glyph / 2) + random.Next(glyph / 3);
                for (int stroke = 0, count = 2 + random.Next(2); stroke < count; stroke++)
                {
                    switch (random.Next(5))
                    {
                        case 0: Line(x, top, x, top + glyph, pen); break;
                        case 1: Line(x + w, top, x + w, top + glyph, pen); break;
                        case 2: Line(x, top + (glyph / 2), x + w, top + (glyph / 2), pen); break;
                        case 3: Line(x, top + glyph, x + w, top, pen); break;
                        default:
                            for (int a = 0; a < 64; a++) Dot(x + (w / 2.0) + (w / 2.0 * Math.Cos(a * Math.PI / 32)), top + (glyph / 2.0) + (glyph / 2.0 * Math.Sin(a * Math.PI / 32)), pen);
                            break;
                    }
                }

                x += w + (int)(pen * 4) + random.Next(3) + (random.Next(6) == 0 ? glyph : 0);
            }
        }

        // Ragged edges: pixels next to an edge flip now and then. Speckle anywhere.
        var edges = (bool[])black.Clone();
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = (y * width) + x;
                bool edge = black[i] != black[i - 1] || black[i] != black[i + 1] || black[i] != black[i - width] || black[i] != black[i + width];
                if (edge && random.Next(6) == 0) edges[i] = !black[i];
            }
        }

        for (int i = 0; i < width * height / 3000; i++) edges[random.Next(width * height)] = true;

        int rowBytes = (width + 7) / 8;
        var rows = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!edges[(y * width) + x]) rows[(y * rowBytes) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return rows;
    }

    /// <summary>One page drawing the image at the given size in points, as /Im0; the image is object 10.</summary>
    private static byte[] Page(string dictionary, byte[] data, double widthPoints, double heightPoints, Action<PdfBuilder>? extra = null)
    {
        var b = new PdfBuilder();
        extra?.Invoke(b);
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, dictionary, data);
        b.SetStream(4, "", System.Text.Encoding.ASCII.GetBytes(Compat.Format("q {0} 0 0 {1} 0 0 cm /Im0 Do Q", widthPoints, heightPoints)));
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 10 0 R>>>>/Contents 4 0 R>>");
        return b.Build();
    }

    private static string Bilevel(int width, int height, string extra = "") =>
        $"/Type/XObject/Subtype/Image/Width {width}/Height {height}/ColorSpace/DeviceGray/BitsPerComponent 1{extra}";

    private static PdfStream Image(byte[] pdf)
    {
        PdfFile file = PdfFile.Open(pdf, Limit);
        PdfDictionary catalog = file.ResolveDictionary(file.Trailer["Root"])!;
        PdfDictionary pages = file.ResolveDictionary(catalog["Pages"])!;
        PdfDictionary page = file.ResolveDictionary(((PdfArray)file.Resolve(pages["Kids"])!)[0])!;
        PdfDictionary xobjects = file.ResolveDictionary(file.ResolveDictionary(page["Resources"])!["XObject"])!;
        return (PdfStream)file.Resolve(xobjects["Im0"])!;
    }

    /// <summary>The page's image as the library reads it: gray pixels after the color space and /Decode.</summary>
    private static byte[] Pixels(byte[] pdf) => PdfDocument.Open(pdf).Pages[0].GetImages().Single().Decode()!.Pixels.ToArray();

    private static int Int(PdfStream s, string key) => (int)((PdfNumber)s.Dictionary[key]!).IntegerValue;

    private static PdfDictionary Parms(PdfStream s) => (PdfDictionary)s.Dictionary["DecodeParms"]!;

    [TestMethod]
    public void RawScanBecomesGroup4WithTheSamePixels()
    {
        byte[] rows = ScanRows(1700, 2200);
        byte[] input = Page(Bilevel(1700, 2200, "/Filter/FlateDecode"), PdfBuilder.Deflate(rows), 612, 792);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesRecodedAsGroup4);
        Assert.AreEqual("Rewritten and verified equivalent to the source.", result.Reason);
        PdfStream image = Image(output);
        Assert.AreEqual("CCITTFaxDecode", image.Dictionary.GetName("Filter"));
        Assert.AreEqual(-1L, ((PdfNumber)Parms(image)["K"]!).IntegerValue);
        Assert.IsNull(Parms(image)["BlackIs1"], "white, the 1 bits, is the more common value");
        Assert.IsLessThan(input.Length * 7 / 10, output.Length);
        CollectionAssert.AreEqual(Pixels(input), Pixels(output));
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));

        // Turned off, the image keeps Flate.
        (byte[] plain, PdfOptimizationResult off) = PdfOptimizer.Optimize(input, NoThreshold with { OptimizeBilevelImages = false });
        Assert.AreEqual(0, off.Statistics.ImagesRecodedAsGroup4);
        if (off.Disposition == OptimizationDisposition.Optimized) Assert.AreEqual("FlateDecode", Image(plain).Dictionary.GetName("Filter"));
    }

    [TestMethod]
    public void MasksPalettesPredictorsAndInvertedImagesKeepTheirPixels()
    {
        byte[] rows = ScanRows(900, 700, seed: 2);
        byte[] inverted = [.. rows.Select(b => (byte)~b)];
        var layout = new PredictorParams(15, 1, 1, 900);
        using BoundedBuffer predicted = Predictors.EncodePng(rows, layout);
        (string Dictionary, byte[] Data, bool BlackIs1)[] cases =
        [
            ("/Type/XObject/Subtype/Image/Width 900/Height 700/ImageMask true/Decode[1 0]/Filter/FlateDecode", PdfBuilder.Deflate(inverted), true),
            (Bilevel(900, 700, "/Decode[1 0]/Filter/FlateDecode"), PdfBuilder.Deflate(rows), false),
            ("/Type/XObject/Subtype/Image/Width 900/Height 700/ColorSpace[/Indexed/DeviceRGB 1<FF000000FF00>]/BitsPerComponent 1/Filter/FlateDecode", PdfBuilder.Deflate(rows), false),
            (Bilevel(900, 700, "/Filter/FlateDecode/DecodeParms<</Predictor 15/Columns 900/BitsPerComponent 1>>"), PdfBuilder.Deflate(predicted.Span.ToArray()), false),
            (Bilevel(900, 700, "/Filter/FlateDecode"), PdfBuilder.Deflate(inverted), true),
            (Bilevel(900, 700), rows, false),
        ];
        foreach ((string dictionary, byte[] data, bool blackIs1) in cases)
        {
            byte[] input = Page(dictionary, data, 300, 233);
            (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold);
            Assert.AreEqual(1, result.Statistics.ImagesRecodedAsGroup4, dictionary + ": " + result.Reason);
            PdfStream image = Image(output);
            Assert.AreEqual("CCITTFaxDecode", image.Dictionary.GetName("Filter"), dictionary);
            Assert.AreEqual(blackIs1, Parms(image)["BlackIs1"] is PdfBoolean { Value: true }, dictionary);
            CollectionAssert.AreEqual(Pixels(input), Pixels(output), dictionary);
            Assert.IsNull(PdfEquivalence.FindDifference(input, output), dictionary);
        }
    }

    [TestMethod]
    [DataRow("g3-1d", 0, true, false)]
    [DataRow("g3-2d", 4, true, false)]
    [DataRow("g3-1d-aligned", 0, true, true)]
    [DataRow("mh", 0, false, true)]
    public void Group3BecomesGroup4(string name, int k, bool endOfLine, bool byteAlign)
    {
        byte[] data = File.ReadAllBytes(Fixture("Ccitt", name + ".bin"));
        string parms = $"/DecodeParms<</K {k}/Columns 301/Rows 97{(endOfLine ? "/EndOfLine true" : "")}{(byteAlign ? "/EncodedByteAlign true" : "")}>>";
        byte[] input = Page(Bilevel(301, 97, "/Filter/CCITTFaxDecode" + parms), data, 301, 97);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold);

        Assert.AreEqual(1, result.Statistics.ImagesRecodedAsGroup4, result.Reason);
        PdfStream image = Image(output);
        Assert.AreEqual(-1, (int)((PdfNumber)Parms(image)["K"]!).IntegerValue);
        CollectionAssert.AreEqual(File.ReadAllBytes(Fixture("Ccitt", "g4.bin")), image.Data.ToArray(), "the same bytes libtiff writes");
        CollectionAssert.AreEqual(Pixels(input), Pixels(output));
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
    }

    [TestMethod]
    public void IndirectParametersAreReadAlikeByThePassAndTheProof()
    {
        byte[] data = File.ReadAllBytes(Fixture("Ccitt", "g3-2d.bin"));
        byte[] input = Page(Bilevel(301, 97, "/Filter/CCITTFaxDecode/DecodeParms 20 0 R"), data, 301, 97, b =>
        {
            b.Set(20, "<</K 21 0 R/Columns 22 0 R/Rows 97/EndOfLine 23 0 R>>");
            b.Set(21, "4");
            b.Set(22, "301");
            b.Set(23, "true");
        });

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesRecodedAsGroup4, result.Reason);
        CollectionAssert.AreEqual(Pixels(input), Pixels(output));
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
    }

    [TestMethod]
    public void AmbiguousOrDamagedFaxDataIsLeftAlone()
    {
        byte[] g3 = File.ReadAllBytes(Fixture("Ccitt", "g3-2d.bin"));
        string[] variants =
        [
            "/K 4/Rows 97/EndOfLine true",                     // no /Columns: 1728 to some readers, the width to others
            "/K 4/Columns 301/Rows 90/EndOfLine true",         // rows that are not the height
            "/K 4.5/Columns 301/Rows 97/EndOfLine true",       // a real where an integer belongs
            "/K 4/Columns 301/Rows 97/EndOfLine 1",            // a number where a boolean belongs
        ];
        foreach (string parms in variants)
        {
            byte[] input = Page(Bilevel(301, 97, $"/Filter/CCITTFaxDecode/DecodeParms<<{parms}>>"), g3, 301, 97);
            (_, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold);
            Assert.AreEqual(0, result.Statistics.ImagesRecodedAsGroup4, parms);
        }

        // Cut short: viewers fill in what is missing in their own ways.
        byte[] cut = Page(Bilevel(301, 97, "/Filter/CCITTFaxDecode/DecodeParms<</K 4/Columns 301/Rows 97/EndOfLine true>>"), g3.AsSpan(0, g3.Length / 2).ToArray(), 301, 97);
        Assert.AreEqual(0, PdfOptimizer.Optimize(cut, NoThreshold).Result.Statistics.ImagesRecodedAsGroup4);

        // Short samples, read as zeros by some readers and not drawn by others.
        byte[] rows = ScanRows(400, 300);
        byte[] shortRaw = Page(Bilevel(400, 300, "/Filter/FlateDecode"), PdfBuilder.Deflate(rows.AsSpan(0, rows.Length - 50).ToArray()), 200, 150);
        Assert.AreEqual(0, PdfOptimizer.Optimize(shortRaw, NoThreshold).Result.Statistics.ImagesRecodedAsGroup4);
    }

    /// <summary>A source with a raw bilevel image and an output where it is the given CCITT data, otherwise identical.</summary>
    private static (byte[] Source, byte[] Output) Pair(byte[] rows, int width, int height, byte[] ccitt, string parms)
    {
        byte[] source = Page(Bilevel(width, height, "/Filter/FlateDecode"), PdfBuilder.Deflate(rows), 300, 300);
        byte[] output = Page(Bilevel(width, height, "/Filter/CCITTFaxDecode/DecodeParms<<" + parms + ">>"), ccitt, 300, 300);
        return (source, output);
    }

    [TestMethod]
    public void ProofComparesEveryPixel()
    {
        byte[] rows = ScanRows(301, 211);
        string parms = "/K -1/Columns 301/Rows 211";
        byte[] good = CcittEncoder.EncodeGroup4(rows, 301, 211, false, 1 << 20)!;
        (byte[] source, byte[] output) = Pair(rows, 301, 211, good, parms);
        Assert.IsNull(PdfEquivalence.FindDifference(source, output));

        // Padding bits are not pixels.
        byte[] padded = (byte[])rows.Clone();
        for (int y = 0; y < 211; y++) padded[(y * 38) + 37] ^= 0x07;
        Assert.IsNull(PdfEquivalence.FindDifference(Pair(padded, 301, 211, good, parms).Source, output));

        // One pixel off.
        byte[] changed = (byte[])rows.Clone();
        changed[(100 * 38) + 20] ^= 0x10;
        StringAssert.Contains(PdfEquivalence.FindDifference(Pair(changed, 301, 211, good, parms).Source, output), "bilevel image pixels differ");

        // Flawed output data, even if a lenient reader would fill it in the same.
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Pair(rows, 301, 211, good.AsSpan(0, good.Length - 30).ToArray(), parms).Output), "does not decode exactly");

        // Parameters a reader could take differently are compared as written.
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Pair(rows, 301, 211, good, "/K -1/Rows 211").Output), "encoded filter chain differs");

        // An inverted reading of the same data.
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Pair(rows, 301, 211, good, parms + "/BlackIs1 true").Output), "bilevel image pixels differ");
    }

    [TestMethod]
    public void OversampledFaxPageIsReducedLegibly()
    {
        // 2400 by 3000 pixels on 4 by 5 inches: 600 dpi, reduced to 150.
        byte[] rows = ScanRows(2400, 3000, seed: 3);
        byte[] g4 = CcittEncoder.EncodeGroup4(rows, 2400, 3000, false, 1 << 24)!;
        byte[] input = Page(Bilevel(2400, 3000, "/Filter/CCITTFaxDecode/DecodeParms<</K -1/Columns 2400/Rows 3000>>"), g4, 288, 360);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 150 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesDownsampled);
        PdfStream image = Image(output);
        Assert.AreEqual(600, Int(image, "Width"));
        Assert.AreEqual(750, Int(image, "Height"));
        Assert.AreEqual(1, Int(image, "BitsPerComponent"));
        Assert.AreEqual("CCITTFaxDecode", image.Dictionary.GetName("Filter"));
        Assert.IsLessThan(g4.Length / 2, image.Data.Length);

        // Exactly the reduction the proof computes, with every glyph stroke still there.
        double[] coverage = BilevelResampler.Coverage(rows, 2400, 3000, 600, 750);
        byte[] expected = BilevelResampler.Threshold(coverage, 600, 750, onesAreForeground: false);
        CollectionAssert.AreEqual(expected, CcittDecoder.DecodeExact(image.Data.Span, new CcittParameters { K = -1, Columns = 600, Rows = 750, BlackIs1 = Parms(image)["BlackIs1"] is PdfBoolean { Value: true } }, 750));
        double inkBefore = rows.Sum(b => 8 - Bits(b)) / (2400.0 * 3000), inkAfter = expected.Sum(b => 8 - Bits(b)) / (600.0 * 750);
        Assert.AreEqual(inkBefore, inkAfter, inkBefore * 0.25, "ink coverage is kept");

        StringAssert.Contains(PdfEquivalence.FindDifference(input, output), "Width");
        Assert.IsNull(PdfEquivalence.FindDifference(input, output, Limit, false, 0.95));
    }

    private static int Bits(byte b)
    {
        int n = 0;
        for (int v = b; v != 0; v &= v - 1) n++;
        return n;
    }

    [TestMethod]
    public void RawAndJbig2BilevelImagesAreReducedToo()
    {
        // The T.88 Annex H page with its JBIG2Globals, drawn small enough to be 1200 dpi.
        byte[] page = File.ReadAllBytes(Fixture("Jbig2", "annexh-p1.jb2"));
        byte[] globals = File.ReadAllBytes(Fixture("Jbig2", "annexh-p1.globals.jb2"));
        byte[] jbig2 = Page("/Type/XObject/Subtype/Image/Width 64/Height 56/ColorSpace/DeviceGray/BitsPerComponent 1/Filter/JBIG2Decode/DecodeParms<</JBIG2Globals 20 0 R>>",
            page, 3.84, 3.36, b => b.SetStream(20, "", globals));
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(jbig2, NoThreshold with { MaxImageDpi = 300 });
        Assert.AreEqual(1, result.Statistics.ImagesDownsampled, result.Reason);
        PdfStream image = Image(output);
        Assert.AreEqual(16, Int(image, "Width"));
        Assert.AreEqual(14, Int(image, "Height"));
        Assert.AreNotEqual("JBIG2Decode", image.Dictionary.GetName("Filter"));
        Assert.IsNull(PdfEquivalence.FindDifference(jbig2, output, Limit, false, 0.9));
        Assert.IsFalse(OutputHasPlainStream(output, globals), "the unused JBIG2Globals are dropped");

        // Raw samples of a 1 bit scan.
        byte[] rows = ScanRows(1800, 1200, seed: 4);
        byte[] raw = Page(Bilevel(1800, 1200, "/Filter/FlateDecode"), PdfBuilder.Deflate(rows), 216, 144);
        (byte[] reduced, PdfOptimizationResult rawResult) = PdfOptimizer.Optimize(raw, NoThreshold with { MaxImageDpi = 200 });
        Assert.AreEqual(1, rawResult.Statistics.ImagesDownsampled, rawResult.Reason);
        Assert.AreEqual(600, Int(Image(reduced), "Width"));
        Assert.IsNull(PdfEquivalence.FindDifference(raw, reduced, Limit, false, 0.95));
    }

    /// <summary>True when any stream of the file holds exactly these bytes, encoded or decoded.</summary>
    private static bool OutputHasPlainStream(byte[] pdf, byte[] data)
    {
        PdfDocument doc = PdfDocument.Open(pdf);
        PdfFile file = PdfFile.Open(pdf, Limit);
        foreach (KeyValuePair<int, XrefEntry> entry in file.Xref)
        {
            if (entry.Value.Type == XrefEntryType.Free || file.GetObject(entry.Key) is not PdfStream stream) continue;
            if (stream.Data.Span.SequenceEqual(data) || doc.GetStreamData(stream).AsSpan().SequenceEqual(data)) return true;
        }

        return false;
    }

    [TestMethod]
    public void ProofRejectsABadBilevelReduction()
    {
        byte[] rows = ScanRows(1200, 900, seed: 5);
        byte[] source = Page(Bilevel(1200, 900, "/Filter/FlateDecode"), PdfBuilder.Deflate(rows), 144, 108);
        double[] coverage = BilevelResampler.Coverage(rows, 1200, 900, 300, 225);
        byte[] reduced = BilevelResampler.Threshold(coverage, 300, 225, false);
        byte[] Output(byte[] samples) => Page(Bilevel(300, 225, "/Filter/FlateDecode"), PdfBuilder.Deflate(samples), 144, 108);

        Assert.IsNull(PdfEquivalence.FindDifference(source, Output(reduced), Limit, false, 0.95));

        // Another page altogether.
        byte[] other = BilevelResampler.Threshold(BilevelResampler.Coverage(ScanRows(1200, 900, seed: 50), 1200, 900, 300, 225), 300, 225, false);
        Assert.IsNotNull(PdfEquivalence.FindDifference(source, Output(other), Limit, false, 0.9));

        // Thresholded too dark: the ink is not what the source holds.
        byte[] dark = (byte[])reduced.Clone();
        for (int i = 0; i < dark.Length; i += 3) dark[i] = 0;
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Output(dark), Limit, false, 0.5), "colors");

        // A plain subsample keeps the ink but drops thin strokes: below a high gate.
        var subsampled = new byte[reduced.Length];
        for (int y = 0; y < 225; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                int sx = (x * 4) + 3, sy = (y * 4) + 3;
                if ((rows[(sy * 150) + (sx >> 3)] & (0x80 >> (sx & 7))) != 0) subsampled[(y * 38) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        Assert.IsNotNull(PdfEquivalence.FindDifference(source, Output(subsampled), Limit, false, 0.95));

        // A bilevel source may not become gray.
        byte[] gray = BilevelResampler.Gray(coverage, 300, 225).Samples;
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Page($"/Type/XObject/Subtype/Image/Width 300/Height 225/ColorSpace/DeviceGray/BitsPerComponent 8/Filter/FlateDecode", PdfBuilder.Deflate(gray), 144, 108), Limit, false, 0.5), "BitsPerComponent");
    }

    [TestMethod]
    public void JpegTwoThousandPhotographBecomesJpeg()
    {
        byte[] jpx = File.ReadAllBytes(Fixture("Jpx", "rgb-53.jp2"));
        foreach (string bits in new[] { "", "/BitsPerComponent 8" })
        {
            byte[] input = Page($"/Type/XObject/Subtype/Image/Width 61/Height 45/ColorSpace/DeviceRGB{bits}/Filter/JPXDecode", jpx, 61, 45);
            (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { JpegQuality = 75, ConvertImagesToJpeg = true });

            Assert.AreEqual(1, result.Statistics.ImagesConvertedToJpeg, result.Reason);
            PdfStream image = Image(output);
            Assert.AreEqual("DCTDecode", image.Dictionary.GetName("Filter"));
            Assert.AreEqual(8, Int(image, "BitsPerComponent"));
            Assert.AreEqual(61, Int(image, "Width"));
            Assert.IsNull(PdfEquivalence.FindDifference(input, output, Limit, false, 0.7), bits);
            Assert.IsNotNull(PdfEquivalence.FindDifference(input, output), bits);
        }
    }

    [TestMethod]
    public void JpegTwoThousandIsDownsampledAsJpegOrSamples()
    {
        // 61 by 45 pixels on 0.2 by 0.15 inches: 305 dpi, reduced to 100.
        byte[] jpx = File.ReadAllBytes(Fixture("Jpx", "rgb-53.jp2"));
        byte[] input = Page("/Type/XObject/Subtype/Image/Width 61/Height 45/ColorSpace/DeviceRGB/Filter/JPXDecode", jpx, 14.4, 10.8);

        (byte[] withQuality, PdfOptimizationResult jpeg) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 100, JpegQuality = 80 });
        Assert.AreEqual(1, jpeg.Statistics.ImagesDownsampled, jpeg.Reason);
        PdfStream image = Image(withQuality);
        Assert.IsLessThan(25, Int(image, "Width"));
        Assert.AreEqual(8, Int(image, "BitsPerComponent"));
        Assert.IsNull(PdfEquivalence.FindDifference(input, withQuality, Limit, false, 0.5));

        (byte[] samples, PdfOptimizationResult flate) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 100 });
        Assert.AreEqual(1, flate.Statistics.ImagesDownsampled, flate.Reason);
        Assert.AreEqual(0, flate.Statistics.ImagesConvertedToJpeg);
        Assert.AreEqual("FlateDecode", Image(samples).Dictionary.GetName("Filter"));
        Assert.IsNull(PdfEquivalence.FindDifference(input, samples, Limit, false, 0.5));
    }

    [TestMethod]
    public void JpegTwoThousandTheDictionaryCannotDescribeIsLeftAlone()
    {
        byte[] rgba = File.ReadAllBytes(Fixture("Jpx", "rgba-53.jp2"));
        byte[] rgb = File.ReadAllBytes(Fixture("Jpx", "rgb-53.jp2"));
        var options = NoThreshold with { JpegQuality = 75, ConvertImagesToJpeg = true, MaxImageDpi = 72 };
        (string Dictionary, byte[] Data)[] cases =
        [
            ("/ColorSpace/DeviceRGB/SMaskInData 1", rgba),          // opacity in the data
            ("/ColorSpace/DeviceRGB", rgba),                        // an opacity channel all the same
            ("", rgb),                                              // colors from the JP2 header
            ("/ColorSpace/DeviceRGB/Decode[1 0 1 0 1 0]", rgb),     // a /Decode a JPEG would apply
            ("/ColorSpace/DeviceRGB/BitsPerComponent 16", rgb),     // a depth a JPEG cannot keep
            ("/ColorSpace/DeviceCMYK", rgb),                        // components that do not match
        ];
        foreach ((string extra, byte[] data) in cases)
        {
            byte[] input = Page($"/Type/XObject/Subtype/Image/Width 61/Height 45{extra}/Filter/JPXDecode", data, 14.4, 10.8);
            (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, options);
            Assert.AreEqual(0, result.Statistics.ImagesConvertedToJpeg + result.Statistics.ImagesDownsampled, extra);
            if (result.Disposition == OptimizationDisposition.Optimized) Assert.AreEqual("JPXDecode", Image(output).Dictionary.GetName("Filter"), extra);
        }
    }

    [TestMethod]
    public void HostileBilevelDataNeverThrows()
    {
        byte[] rows = ScanRows(640, 480, seed: 6);
        byte[] g4 = CcittEncoder.EncodeGroup4(rows, 640, 480, false, 1 << 20)!;
        byte[] g3 = File.ReadAllBytes(Fixture("Ccitt", "g3-2d.bin"));
        byte[] jbig2 = File.ReadAllBytes(Fixture("Jbig2", "generic-mmr.jb2"));
        var random = new Random(9);
        var options = NoThreshold with { MaxImageDpi = 100 };
        for (int round = 0; round < 60; round++)
        {
            (string dictionary, byte[] data) = (round % 3) switch
            {
                0 => (Bilevel(640, 480, "/Filter/CCITTFaxDecode/DecodeParms<</K -1/Columns 640/Rows 480>>"), (byte[])g4.Clone()),
                1 => (Bilevel(301, 97, "/Filter/CCITTFaxDecode/DecodeParms<</K 4/Columns 301/Rows 97/EndOfLine true>>"), (byte[])g3.Clone()),
                _ => (Bilevel(160, 72, "/Filter/JBIG2Decode"), (byte[])jbig2.Clone()),
            };
            for (int i = 0; i < 4; i++) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
            if (round % 5 == 0) data = data.AsSpan(0, random.Next(data.Length)).ToArray();
            byte[] input = Page(dictionary, data, 20, 15);

            (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, round % 2 == 0 ? options : NoThreshold);
            if (result.Disposition == OptimizationDisposition.Optimized)
            {
                Assert.IsNull(PdfEquivalence.FindDifference(input, output, Limit, false, round % 2 == 0 ? 0.5 : null), result.Reason);
            }
        }
    }
}
