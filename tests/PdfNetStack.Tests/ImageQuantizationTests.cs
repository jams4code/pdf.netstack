using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Reducing raw RGB images to a palette: the quantizer, the optimizer pass and the proof.</summary>
[TestClass]
public sealed class ImageQuantizationTests
{
    private const long Limit = 64 << 20;
    private const string Draw = "144 0 0 108 50 50";
    private static readonly PdfOptimizerOptions Quantize = new() { MinSavingPercent = 0, QuantizeImages = true };

    private static byte[] Deflate(byte[] data) => StreamCodec.Deflate(data, CompressionLevel.Optimal);

    private static string Rgb(Raster r, string extra = "") =>
        $"/Type/XObject/Subtype/Image/Width {r.Width}/Height {r.Height}/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/FlateDecode{extra}";

    /// <summary>One page drawing one image, object 10. <paramref name="extra"/> adds other objects.</summary>
    private static byte[] Pdf(string dictionary, byte[] data, Action<PdfBuilder>? extra = null)
    {
        var b = new PdfBuilder();
        extra?.Invoke(b);
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, dictionary, data);
        b.SetStream(4, "", System.Text.Encoding.ASCII.GetBytes($"q {Draw} cm /Im0 Do Q"));
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 10 0 R>>>>/Contents 4 0 R>>");
        return b.Build();
    }

    private static byte[] Pdf(Raster raster, string extra = "") => Pdf(Rgb(raster, extra), Deflate(raster.Samples));

    private static (PdfFile File, PdfStream Image) Image(byte[] pdf)
    {
        PdfFile file = PdfFile.Open(pdf, Limit);
        PdfDictionary catalog = file.ResolveDictionary(file.Trailer["Root"])!;
        PdfDictionary pages = file.ResolveDictionary(catalog["Pages"])!;
        PdfDictionary page = file.ResolveDictionary(((PdfArray)file.Resolve(pages["Kids"])!)[0])!;
        PdfDictionary xobjects = file.ResolveDictionary(file.ResolveDictionary(page["Resources"])!["XObject"])!;
        return (file, (PdfStream)file.Resolve(xobjects["Im0"])!);
    }

    /// <summary>The image's samples through its palette, as a reader expands them.</summary>
    private static (byte[] Samples, int Bits, int Colors) Expanded(byte[] pdf)
    {
        (PdfFile file, PdfStream image) = Image(pdf);
        var space = (PdfArray)file.Resolve(image.Dictionary["ColorSpace"])!;
        Assert.AreEqual("Indexed", ((PdfName)space[0]).Value);
        byte[] lookup = ((PdfString)file.Resolve(space[3])!).Value;
        int bits = (int)((PdfNumber)image.Dictionary["BitsPerComponent"]!).IntegerValue;
        int width = (int)((PdfNumber)image.Dictionary["Width"]!).IntegerValue;
        int height = (int)((PdfNumber)image.Dictionary["Height"]!).IntegerValue;
        using DecodedStream decoded = StreamCodec.Decode(image.Data, StreamCodec.GetFilters(image.Dictionary, file.Resolve), DecodeDepth.Full, Limit, file.Resolve);
        byte[] data = decoded.Data.ToArray();
        int rowBytes = ((width * bits) + 7) / 8, mask = (1 << bits) - 1;
        var samples = new byte[width * height * 3];
        for (int y = 0, o = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++, o += 3)
            {
                int bit = x * bits;
                int index = (data[(y * rowBytes) + (bit >> 3)] >> (8 - bits - (bit & 7))) & mask;
                Array.Copy(lookup, index * 3, samples, o, 3);
            }
        }

        return (samples, bits, lookup.Length / 3);
    }

    /// <summary>A white drawing with antialiased colored strokes: thousands of colors, nearly all of them rare.</summary>
    private static Raster Drawing(int width, int height)
    {
        var samples = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double r = 255, g = 255, b = 255;
                for (int line = 0; line < 12; line++)
                {
                    double distance = Math.Abs(((x * (line + 1.3)) - (y * (12.7 - line))) / Math.Sqrt(((line + 1.3) * (line + 1.3)) + ((12.7 - line) * (12.7 - line))));
                    double cover = Math.Max(0, 1 - (distance / 1.7));
                    double hue = line / 12.0;
                    r += cover * ((255 * hue) - r);
                    g += cover * ((90 * (1 - hue)) - g);
                    b += cover * ((200 * (1 - hue)) - b);
                }

                int o = ((y * width) + x) * 3;
                samples[o] = (byte)Math.Round(r);
                samples[o + 1] = (byte)Math.Round(g);
                samples[o + 2] = (byte)Math.Round(b);
            }
        }

        return new Raster(width, height, 3, samples);
    }

    /// <summary>Rows of the given colors, each repeated through the image.</summary>
    private static Raster Flat(int width, int height, params int[] colors)
    {
        var samples = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int c = colors[((x / 3) + (y / 2)) % colors.Length], o = ((y * width) + x) * 3;
                samples[o] = (byte)(c >> 16);
                samples[o + 1] = (byte)(c >> 8);
                samples[o + 2] = (byte)c;
            }
        }

        return new Raster(width, height, 3, samples);
    }

    /// <summary>White noise: nothing a palette of 256 colors can hold.</summary>
    private static Raster Noise(int width, int height)
    {
        var samples = new byte[width * height * 3];
        new Random(7).NextBytes(samples);
        return new Raster(width, height, 3, samples);
    }

    private static int Distinct(Raster raster)
    {
        var colors = new HashSet<int>();
        for (int o = 0; o < raster.Samples.Length; o += 3) colors.Add((raster.Samples[o] << 16) | (raster.Samples[o + 1] << 8) | raster.Samples[o + 2]);
        return colors.Count;
    }

    [TestMethod]
    public void DrawingBecomesPaletteAndIsProven()
    {
        Raster drawing = Drawing(600, 400);
        Assert.IsGreaterThan(256, Distinct(drawing), "the test needs an image a palette cannot hold exactly");
        byte[] input = Pdf(drawing);

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, Quantize);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesQuantized);
        Assert.IsGreaterThan(0, result.Statistics.ObjectsVerified);
        StringAssert.Contains(result.Reason, "1 image(s) reduced to a palette");
        (byte[] samples, _, int colors) = Expanded(output);
        Assert.IsLessThanOrEqualTo(256, colors);
        Assert.IsGreaterThanOrEqualTo(0.97, Ssim.Compare(drawing, new Raster(600, 400, 3, samples)));
        Assert.IsNull(PdfEquivalence.FindDifference(input, output, Limit, false, 0.97));
    }

    [TestMethod]
    public void WhiteStaysExactlyWhite()
    {
        Raster drawing = Drawing(400, 300);
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(Pdf(drawing), Quantize);

        Assert.AreEqual(1, result.Statistics.ImagesQuantized, result.Reason);
        (byte[] samples, _, _) = Expanded(output);
        for (int o = 0; o < samples.Length; o += 3)
        {
            bool white = drawing.Samples[o] == 255 && drawing.Samples[o + 1] == 255 && drawing.Samples[o + 2] == 255;
            if (white) Assert.IsTrue(samples[o] == 255 && samples[o + 1] == 255 && samples[o + 2] == 255, $"pixel {o / 3} of the background moved");
        }
    }

    [TestMethod]
    [DataRow(2, 1)]
    [DataRow(4, 2)]
    [DataRow(16, 4)]
    [DataRow(200, 8)]
    public void ImagesWithFewColorsStayExactAtTheSmallestDepth(int count, int bits)
    {
        int[] colors = Enumerable.Range(0, count).Select(i => (i * 7919 * 1021) & 0xFFFFFF).ToArray();
        Raster flat = Flat(512, 300, colors);
        Assert.AreEqual(count, Distinct(flat));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(Pdf(flat), Quantize);

        Assert.AreEqual(1, result.Statistics.ImagesQuantized, result.Reason);
        (byte[] samples, int depth, int palette) = Expanded(output);
        Assert.AreEqual(bits, depth);
        Assert.AreEqual(count, palette);
        CollectionAssert.AreEqual(flat.Samples, samples, "an image that fits a palette keeps every pixel");
    }

    [TestMethod]
    public void NoiseIsKeptByTheGate()
    {
        Raster noise = Noise(300, 200);
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(Pdf(noise), Quantize);

        Assert.AreEqual(0, result.Statistics.ImagesQuantized);
        Assert.AreEqual(1, result.Statistics.ImagesKeptByQualityGate);
        Assert.AreEqual("DeviceRGB", Image(output).Image.Dictionary.GetName("ColorSpace"));
    }

    [TestMethod]
    public void AHigherGateKeepsMoreColors()
    {
        Raster drawing = Drawing(500, 300);
        byte[] input = Pdf(drawing);

        (byte[] loose, _) = PdfOptimizer.Optimize(input, Quantize with { MinImageSsim = 0.90 });
        (byte[] strict, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, Quantize with { MinImageSsim = 0.999 });

        double kept = Ssim.Compare(drawing, new Raster(500, 300, 3, Expanded(loose).Samples));
        Assert.IsGreaterThanOrEqualTo(0.90, kept);
        if (result.Statistics.ImagesQuantized == 1)
        {
            Assert.IsGreaterThanOrEqualTo(0.999, Ssim.Compare(drawing, new Raster(500, 300, 3, Expanded(strict).Samples)));
            Assert.IsGreaterThanOrEqualTo(loose.Length, strict.Length);
        }
    }

    [TestMethod]
    [DataRow("/Decode[1 0 1 0 1 0]")]
    [DataRow("/Interpolate true")]
    public void EntriesAPaletteWouldChangeLeaveTheImageAlone(string entry)
    {
        Raster drawing = Drawing(300, 200);
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(Pdf(drawing, entry), Quantize);

        Assert.AreEqual(0, result.Statistics.ImagesQuantized, entry);
        Assert.AreEqual("DeviceRGB", Image(output).Image.Dictionary.GetName("ColorSpace"), entry);
    }

    [TestMethod]
    public void MatteSoftMaskLeavesTheImageAlone()
    {
        Raster drawing = Drawing(300, 200);
        byte[] mask = Deflate(new byte[300 * 200]);
        byte[] input = Pdf(Rgb(drawing, "/SMask 20 0 R"), Deflate(drawing.Samples),
            b => b.SetStream(20, "/Type/XObject/Subtype/Image/Width 300/Height 200/ColorSpace/DeviceGray/BitsPerComponent 8/Filter/FlateDecode/Matte[0 0 0]", mask));

        (_, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, Quantize);

        Assert.AreEqual(0, result.Statistics.ImagesQuantized);
    }

    [TestMethod]
    public void PlainSoftMaskStaysWithTheImage()
    {
        Raster drawing = Drawing(300, 200);
        var opacity = new byte[300 * 200];
        for (int i = 0; i < opacity.Length; i++) opacity[i] = (byte)(i % 251);
        byte[] input = Pdf(Rgb(drawing, "/SMask 20 0 R"), Deflate(drawing.Samples),
            b => b.SetStream(20, "/Type/XObject/Subtype/Image/Width 300/Height 200/ColorSpace/DeviceGray/BitsPerComponent 8/Filter/FlateDecode", Deflate(opacity)));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, Quantize);

        Assert.AreEqual(1, result.Statistics.ImagesQuantized, result.Reason);
        (PdfFile file, PdfStream image) = Image(output);
        var soft = (PdfStream)file.Resolve(image.Dictionary["SMask"])!;
        using DecodedStream decoded = StreamCodec.Decode(soft.Data, StreamCodec.GetFilters(soft.Dictionary, file.Resolve), DecodeDepth.Full, Limit, file.Resolve);
        CollectionAssert.AreEqual(opacity, decoded.Data.ToArray(), "opacity is exact");
    }

    [TestMethod]
    public void IccBasedColorSpaceBecomesThePaletteBase()
    {
        Raster drawing = Drawing(300, 200);
        byte[] profile = new byte[256];
        new Random(3).NextBytes(profile);
        string dictionary = $"/Type/XObject/Subtype/Image/Width 300/Height 200/ColorSpace[/ICCBased 20 0 R]/BitsPerComponent 8/Filter/FlateDecode";
        byte[] input = Pdf(dictionary, Deflate(drawing.Samples), b => b.SetStream(20, "/N 3/Alternate/DeviceRGB", profile));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, Quantize);

        Assert.AreEqual(1, result.Statistics.ImagesQuantized, result.Reason);
        (PdfFile file, PdfStream image) = Image(output);
        var space = (PdfArray)file.Resolve(image.Dictionary["ColorSpace"])!;
        var @base = (PdfArray)file.Resolve(space[1])!;
        Assert.AreEqual("ICCBased", ((PdfName)@base[0]).Value);
        var icc = (PdfStream)file.Resolve(@base[1])!;
        using DecodedStream decoded = StreamCodec.Decode(icc.Data, StreamCodec.GetFilters(icc.Dictionary, file.Resolve), DecodeDepth.Full, Limit, file.Resolve);
        CollectionAssert.AreEqual(profile, decoded.Data.ToArray());
    }

    [TestMethod]
    public void DownsamplingAndQuantizingCountBoth()
    {
        // 1200 by 900 pixels on 2 by 1.5 inches: 600 dpi.
        Raster drawing = Drawing(1200, 900);
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(Pdf(drawing), Quantize with { MaxImageDpi = 150 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesDownsampled);
        Assert.AreEqual(1, result.Statistics.ImagesQuantized);
        PdfStream image = Image(output).Image;
        Assert.AreEqual(300L, ((PdfNumber)image.Dictionary["Width"]!).IntegerValue);
    }

    [TestMethod]
    public void JpegAndPaletteRaceImageByImage()
    {
        Raster drawing = Drawing(600, 400);
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(Pdf(drawing),
            Quantize with { ConvertImagesToJpeg = true, JpegQuality = 50, MinImageSsim = 0.97 });

        Assert.AreEqual(1, result.Statistics.ImagesQuantized + result.Statistics.ImagesConvertedToJpeg, result.Reason);
        Assert.AreEqual(0, result.Statistics.ImagesConvertedToJpeg, "a drawing is not an image JPEG should win");
        Assert.AreEqual("FlateDecode", Image(output).Image.Dictionary.GetName("Filter"));
    }

    [TestMethod]
    public async Task StreamingProvesThePaletteToo()
    {
        Raster drawing = Drawing(600, 400);
        string folder = Path.Combine(Path.GetTempPath(), "pdfnetstack-quantize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "in.pdf"), target = Path.Combine(folder, "out.pdf");
            File.WriteAllBytes(source, Pdf(drawing));

            PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync(source, target, Quantize with { Streaming = true });

            Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
            Assert.IsTrue(result.Statistics.Streamed);
            Assert.AreEqual(1, result.Statistics.ImagesQuantized);
            Assert.IsGreaterThanOrEqualTo(0.97, Ssim.Compare(drawing, new Raster(600, 400, 3, Expanded(File.ReadAllBytes(target)).Samples)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The source with its image swapped for one under an Indexed space, as another tool might write it.</summary>
    private static byte[] WithPalette(Raster size, byte[] indices, byte[] lookup, int bits = 8, string extra = "")
    {
        string hex = string.Concat(lookup.Select(x => x.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        string dictionary = $"/Type/XObject/Subtype/Image/Width {size.Width}/Height {size.Height}/ColorSpace[/Indexed/DeviceRGB {(lookup.Length / 3) - 1}<{hex}>]/BitsPerComponent {bits}/Filter/FlateDecode{extra}";
        return Pdf(dictionary, Deflate(indices));
    }

    private static (byte[] Input, byte[] Indices, byte[] Lookup) TwoColors()
    {
        Raster flat = Flat(200, 120, 0x204060, 0xE0C0A0);
        var indices = new byte[200 * 120];
        for (int i = 0; i < indices.Length; i++) indices[i] = flat.Samples[i * 3] == 0x20 ? (byte)0 : (byte)1;
        return (Pdf(flat), indices, [0x20, 0x40, 0x60, 0xE0, 0xC0, 0xA0]);
    }

    [TestMethod]
    public void ProofAcceptsAFaithfulPalette()
    {
        (byte[] input, byte[] indices, byte[] lookup) = TwoColors();
        Assert.IsNull(PdfEquivalence.FindDifference(input, WithPalette(Flat(200, 120, 0), indices, lookup), Limit, false, 0.97));
    }

    [TestMethod]
    public void ProofRefusesAPaletteWithoutAnAllowance()
    {
        (byte[] input, byte[] indices, byte[] lookup) = TwoColors();
        Assert.IsNotNull(PdfEquivalence.FindDifference(input, WithPalette(Flat(200, 120, 0), indices, lookup), Limit, false, null));
    }

    [TestMethod]
    public void ProofRefusesSwappedColors()
    {
        (byte[] input, byte[] indices, _) = TwoColors();
        string? difference = PdfEquivalence.FindDifference(input, WithPalette(Flat(200, 120, 0), indices, [0xE0, 0xC0, 0xA0, 0x20, 0x40, 0x60]), Limit, false, 0.5);
        Assert.IsNotNull(difference);
    }

    [TestMethod]
    public void ProofRefusesAHueDriftLuminanceCannotSee()
    {
        // Same luminance within a step on both colors, so SSIM alone stays near 1.
        (byte[] input, byte[] indices, _) = TwoColors();
        byte[] drifted = [0x20 + 40, 0x40 - 20, 0x60 - 2, 0xE0 - 30, 0xC0 + 15, 0xA0 + 2];
        string? difference = PdfEquivalence.FindDifference(input, WithPalette(Flat(200, 120, 0), indices, drifted), Limit, false, 0.5);
        Assert.IsNotNull(difference);
        StringAssert.Contains(difference, "quantized image");
    }

    [TestMethod]
    public void ProofRefusesAnIndexOutsideTheTable()
    {
        (byte[] input, byte[] indices, byte[] lookup) = TwoColors();
        var broken = (byte[])indices.Clone();
        broken[5] = 200;
        Assert.IsNotNull(PdfEquivalence.FindDifference(input, WithPalette(Flat(200, 120, 0), broken, lookup), Limit, false, 0.5));
    }

    [TestMethod]
    public void ProofRefusesAPaletteWithDecode()
    {
        (byte[] input, byte[] indices, byte[] lookup) = TwoColors();
        Assert.IsNotNull(PdfEquivalence.FindDifference(input, WithPalette(Flat(200, 120, 0), indices, lookup, extra: "/Decode[1 0]"), Limit, false, 0.5));
    }

    [TestMethod]
    public void QuantizerIsDeterministicAndKeepsItsLimit()
    {
        Raster drawing = Drawing(300, 200);
        foreach (int colors in new[] { 256, 64, 16, 4 })
        {
            QuantizedImage first = ColorQuantizer.For(drawing).Reduce(colors);
            QuantizedImage second = ColorQuantizer.For(drawing).Reduce(colors);
            Assert.IsLessThanOrEqualTo(colors, first.Colors);
            CollectionAssert.AreEqual(first.Palette, second.Palette);
            CollectionAssert.AreEqual(first.Indices, second.Indices);
        }
    }

    [TestMethod]
    public void QuantizerRoundsImagesWithTooManyColorsToCount()
    {
        Raster noise = Noise(400, 400);
        QuantizedImage image = ColorQuantizer.For(noise).Reduce(256);

        Assert.AreEqual(256, image.Colors);
        Assert.HasCount(400 * 400, image.Indices);
    }

    [TestMethod]
    public void QuantizingNeedsRecompression()
    {
        byte[] input = Pdf(Flat(32, 32, 0, 0xFFFFFF));
        Assert.ThrowsExactly<ArgumentException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { QuantizeImages = true, RecompressStreams = false }));
    }

    [TestMethod]
    public void PresetsShrinkADrawingAndStayProven()
    {
        byte[] input = Pdf(Drawing(600, 400));
        foreach (PdfOptimizerOptions preset in new[] { PdfOptimizerOptions.Balanced, PdfOptimizerOptions.Smallest })
        {
            (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, preset);
            Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
            Assert.IsGreaterThan(0, result.Statistics.ObjectsVerified);
            Assert.IsLessThan(input.Length * 3 / 4, output.Length);
        }

        Assert.IsFalse(PdfOptimizerOptions.Default.QuantizeImages, "the default stays lossless");
        Assert.IsNull(PdfOptimizerOptions.Default.JpegQuality, "the default stays lossless");
    }
}
