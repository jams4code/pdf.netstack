using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>The JPEG pass end to end: through the optimizer, the writer and the equivalence proof.</summary>
[TestClass]
public sealed class JpegOptimizationTests
{
    private static readonly PdfOptimizerOptions NoThreshold = new() { MinSavingPercent = 0 };

    private static byte[] Fixture(string name) => JpegCodecTests.Fixture(name);

    /// <summary>One page per image, each drawn full page as /Im0.</summary>
    private static byte[] ImagePdf(params (byte[] Data, string Dictionary)[] images)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        var kids = new List<int>();
        int next = 3;
        foreach ((byte[] data, string dictionary) in images)
        {
            int image = next++;
            int content = next++;
            int page = next++;
            b.SetStream(image, "/Type/XObject/Subtype/Image" + dictionary, data);
            b.SetStream(content, "", "q 400 0 0 300 50 50 cm /Im0 Do Q"u8.ToArray());
            b.Set(page, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 500 400]/Resources<</XObject<</Im0 {image} 0 R>>>>/Contents {content} 0 R>>");
            kids.Add(page);
        }

        b.Set(2, $"<</Type/Pages/Kids[{string.Join(" ", kids.Select(k => k + " 0 R"))}]/Count {kids.Count}>>");
        return b.Build();
    }

    private static string Jpeg(int width, int height, string colorSpace, string extra = "") =>
        $"/Width {width}/Height {height}/ColorSpace{colorSpace}/BitsPerComponent 8/Filter/DCTDecode{extra}";

    private static List<PdfStream> Images(byte[] pdf)
    {
        PdfFile file = PdfFile.Open(pdf, 64 << 20);
        return file.Xref.Keys
            .Where(n => file.Exists(n))
            .Select(file.GetObject)
            .OfType<PdfStream>()
            .Where(s => s.Dictionary.GetName("Subtype") == "Image")
            .ToList();
    }

    private static byte[] Deflate(byte[] data) => StreamCodec.Deflate(data, CompressionLevel.Optimal);

    [TestMethod]
    public void JpegImagesAreReencodedLosslesslyByDefault()
    {
        byte[] input = ImagePdf(
            (Fixture("photo-q95.jpg"), Jpeg(320, 240, "/DeviceRGB")),
            (Fixture("cmyk-baseline.jpg"), Jpeg(61, 45, "/DeviceCMYK", "/Decode[1 0 1 0 1 0 1 0]")),
            (Fixture("gray-progressive.jpg"), Jpeg(61, 45, "/DeviceGray")));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(3, result.Statistics.JpegImagesRecompressed);
        Assert.AreEqual(0, result.Statistics.JpegImagesRequantized);
        Assert.AreEqual("Rewritten and verified equivalent to the source.", result.Reason);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output), "lossless needs no allowance");
        foreach (PdfStream image in Images(output))
        {
            Assert.AreEqual("DCTDecode", image.Dictionary.GetName("Filter"));
        }
    }

    [TestMethod]
    public void JpegQualityRequantizesAndTheProofHoldsOnlyWithTheAllowance()
    {
        byte[] input = ImagePdf((Fixture("photo-q95.jpg"), Jpeg(320, 240, "/DeviceRGB")));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { JpegQuality = 50 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.JpegImagesRequantized);
        StringAssert.Contains(result.Reason, "1 JPEG image(s) requantized to quality 50");
        Assert.IsLessThan(input.Length * 6 / 10, output.Length);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output, allowJpegRequantization: true));
        StringAssert.EndsWith(PdfEquivalence.FindDifference(input, output), "JPEG quantization changed");
    }

    [TestMethod]
    public void SoftMasksStayLossless()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[5 0 R]/Count 1>>");
        b.SetStream(3, "/Type/XObject/Subtype/Image" + Jpeg(61, 45, "/DeviceGray"), Fixture("gray-baseline.jpg"));
        b.SetStream(4, "/Type/XObject/Subtype/Image" + Jpeg(320, 240, "/DeviceRGB", "/SMask 3 0 R"), Fixture("photo-q95.jpg"));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 500 400]/Resources<</XObject<</Im0 4 0 R>>>>/Contents 6 0 R>>");
        b.SetStream(6, "", "q 400 0 0 300 50 50 cm /Im0 Do Q"u8.ToArray());
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { JpegQuality = 30 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(2, result.Statistics.JpegImagesRecompressed);
        Assert.AreEqual(1, result.Statistics.JpegImagesRequantized, "the q80 mask would be requantized at q30 if it were not protected");
        PdfStream mask = Images(output).Single(s => s.Dictionary["SMask"] is null);
        Assert.IsNull(JpegEquivalence.FindDifference(Fixture("gray-baseline.jpg"), mask.Data.Span, allowRequantization: false, 64 << 20));
    }

    [TestMethod]
    public void FlateWrappedJpegIsUnwrappedAndReencoded()
    {
        byte[] jpeg = Fixture("ycc444-baseline.jpg");
        byte[] input = ImagePdf((Deflate(jpeg), $"/Width 61/Height 45/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter[/FlateDecode/DCTDecode]"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.JpegImagesRecompressed);
        PdfStream image = Images(output).Single();
        Assert.AreEqual("DCTDecode", image.Dictionary.GetName("Filter"));
        Assert.IsNull(JpegEquivalence.FindDifference(jpeg, image.Data.Span, allowRequantization: false, 64 << 20));
    }

    [TestMethod]
    public void ColorTransformParameterIsKept()
    {
        byte[] input = ImagePdf((Fixture("rgb-baseline.jpg"), Jpeg(61, 45, "/DeviceRGB", "/DecodeParms<</ColorTransform 0>>")));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { JpegQuality = 40 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        PdfStream image = Images(output).Single();
        var parms = (PdfDictionary)image.Dictionary["DecodeParms"]!;
        Assert.AreEqual(0L, ((PdfNumber)parms["ColorTransform"]!).IntegerValue);
    }

    [TestMethod]
    public void ImagesThatDoNotFitTheirColorSpaceAreLeftAlone()
    {
        byte[] rgbAsGray = Fixture("ycc420-baseline.jpg");
        byte[] palette = Fixture("gray-baseline.jpg");
        byte[] input = ImagePdf(
            (rgbAsGray, Jpeg(61, 45, "/DeviceGray")),
            (palette, Jpeg(61, 45, "[/Indexed/DeviceRGB 1<000000FFFFFF>]")),
            (palette, "/Width 61/Height 45/ColorSpace/DeviceGray/BitsPerComponent 4/Filter/DCTDecode"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { JpegQuality = 20 });

        Assert.AreEqual(0, result.Statistics.JpegImagesRecompressed, result.Reason);
        if (result.Disposition == OptimizationDisposition.Optimized)
        {
            foreach (PdfStream image in Images(output))
            {
                Assert.IsTrue(image.Data.Span.SequenceEqual(rgbAsGray) || image.Data.Span.SequenceEqual(palette));
            }
        }
    }

    [TestMethod]
    public void DisablingTheJpegPassKeepsJpegBytes()
    {
        byte[] jpeg = Fixture("photo-q95.jpg");
        byte[] input = ImagePdf((jpeg, Jpeg(320, 240, "/DeviceRGB")));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { OptimizeJpegImages = false });

        Assert.AreEqual(0, result.Statistics.JpegImagesRecompressed);
        if (result.Disposition == OptimizationDisposition.Optimized)
        {
            Assert.IsTrue(Images(output).Single().Data.Span.SequenceEqual(jpeg));
        }
    }

    [TestMethod]
    public void DamagedJpegIsCarriedOverUntouched()
    {
        byte[] damaged = Fixture("ycc420-baseline.jpg");
        damaged = damaged.AsSpan(0, damaged.Length - 200).ToArray();
        byte[] input = ImagePdf((damaged, Jpeg(61, 45, "/DeviceRGB")), (Fixture("photo-q95.jpg"), Jpeg(320, 240, "/DeviceRGB")));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { JpegQuality = 60 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.JpegImagesRecompressed);
        Assert.IsTrue(Images(output).Exists(s => s.Data.Span.SequenceEqual(damaged)));
    }

    [TestMethod]
    public void InvalidJpegOptionsAreRejected()
    {
        byte[] input = ImagePdf((Fixture("gray-baseline.jpg"), Jpeg(61, 45, "/DeviceGray")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { JpegQuality = 0 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { JpegQuality = 101 }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { JpegQuality = 50, OptimizeJpegImages = false }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { JpegQuality = 50, RecompressStreams = false }));
    }
}
