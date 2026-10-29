using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Optimization;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>The lossy image profile end to end: placement, downsampling, conversion, the gate and the proof.</summary>
[TestClass]
public sealed class LossyImageTests
{
    private const long Limit = 64 << 20;
    private static readonly PdfOptimizerOptions NoThreshold = new() { MinSavingPercent = 0 };

    private static byte[] Deflate(byte[] data) => StreamCodec.Deflate(data, CompressionLevel.Optimal);

    private static string Raw(Raster r, string extra = "") =>
        $"/Type/XObject/Subtype/Image/Width {r.Width}/Height {r.Height}/ColorSpace{SyntheticImages.ColorSpace(r.Channels)}/BitsPerComponent 8/Filter/FlateDecode{extra}";

    private static string Dct(Raster r, string extra = "") =>
        $"/Type/XObject/Subtype/Image/Width {r.Width}/Height {r.Height}/ColorSpace{SyntheticImages.ColorSpace(r.Channels)}/BitsPerComponent 8/Filter/DCTDecode{extra}";

    /// <summary>One page drawing every image with its own content, as /Im0, /Im1 and so on. Images are objects 10, 11, ...</summary>
    private static byte[] Pdf(params (string Dictionary, byte[] Data, string Draw)[] images) => Pdf(null, images);

    private static byte[] Pdf(Action<PdfBuilder>? extra, params (string Dictionary, byte[] Data, string Draw)[] images)
    {
        var b = new PdfBuilder();
        extra?.Invoke(b);
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        var names = new List<string>();
        var content = new List<string>();
        for (int i = 0; i < images.Length; i++)
        {
            b.SetStream(10 + i, images[i].Dictionary, images[i].Data);
            names.Add($"/Im{i} {10 + i} 0 R");
            content.Add($"q {images[i].Draw} cm /Im{i} Do Q");
        }

        b.SetStream(4, "", System.Text.Encoding.ASCII.GetBytes(string.Join("\n", content)));
        b.Set(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<<{string.Join("", names)}>>>>/Contents 4 0 R>>");
        return b.Build();
    }

    /// <summary>The image the first page draws as /Im{index}; the optimizer renumbers objects.</summary>
    private static PdfStream Image(byte[] pdf, int index)
    {
        PdfFile file = PdfFile.Open(pdf, Limit);
        PdfDictionary catalog = file.ResolveDictionary(file.Trailer["Root"])!;
        PdfDictionary pages = file.ResolveDictionary(catalog["Pages"])!;
        PdfDictionary page = file.ResolveDictionary(((PdfArray)file.Resolve(pages["Kids"])!)[0])!;
        PdfDictionary xobjects = file.ResolveDictionary(file.ResolveDictionary(page["Resources"])!["XObject"])!;
        PdfStream image = (PdfStream)file.Resolve(xobjects["Im" + index])!;
        return image;
    }

    /// <summary>The soft mask of the first page's /Im0.</summary>
    private static PdfStream SoftMask(byte[] pdf)
    {
        PdfFile file = PdfFile.Open(pdf, Limit);
        PdfDictionary catalog = file.ResolveDictionary(file.Trailer["Root"])!;
        PdfDictionary pages = file.ResolveDictionary(catalog["Pages"])!;
        PdfDictionary page = file.ResolveDictionary(((PdfArray)file.Resolve(pages["Kids"])!)[0])!;
        PdfDictionary xobjects = file.ResolveDictionary(file.ResolveDictionary(page["Resources"])!["XObject"])!;
        return (PdfStream)file.Resolve(((PdfStream)file.Resolve(xobjects["Im0"])!).Dictionary["SMask"])!;
    }

    private static int Int(PdfStream s, string key) => (int)((PdfNumber)s.Dictionary[key]!).IntegerValue;

    [TestMethod]
    public void OversampledRawImageIsDownsampledAndProven()
    {
        // 1200 by 900 pixels on 2 by 1.5 inches: 600 dpi.
        Raster photo = SyntheticImages.Photo(1200, 900, 3);
        byte[] input = Pdf((Raw(photo), Deflate(photo.Samples), "144 0 0 108 50 50"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 150 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesDownsampled);
        StringAssert.Contains(result.Reason, "1 image(s) downsampled to 150 dpi");
        PdfStream image = Image(output, 0);
        Assert.AreEqual(300, Int(image, "Width"));
        Assert.AreEqual(225, Int(image, "Height"));
        Assert.AreEqual("FlateDecode", image.Dictionary.GetName("Filter"), "raw samples stay lossless after resampling");
        Assert.IsLessThan(input.Length / 8, output.Length);

        StringAssert.Contains(PdfEquivalence.FindDifference(input, output), "Width");
        Assert.IsNull(PdfEquivalence.FindDifference(input, output, Limit, false, 0.99));
    }

    [TestMethod]
    public void ImagesWithinTheThresholdKeepTheirSize()
    {
        // 200 dpi against a 150 dpi target: under the 1.5 times threshold.
        Raster photo = SyntheticImages.Photo(400, 300, 1);
        byte[] input = Pdf((Raw(photo), Deflate(photo.Samples), "144 0 0 108 50 50"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 150 });

        Assert.AreEqual(0, result.Statistics.ImagesDownsampled, result.Reason);
        if (result.Disposition == OptimizationDisposition.Optimized) Assert.AreEqual(400, Int(Image(output, 0), "Width"));
    }

    [TestMethod]
    public void LargestPlacementDecidesAcrossFormsAndRotation()
    {
        // Drawn small on the page, and large through a form scaled by 4 and rotated 90 degrees.
        Raster photo = SyntheticImages.Photo(800, 400, 3);
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, Raw(photo), Deflate(photo.Samples));
        b.SetStream(11, "/Type/XObject/Subtype/Form/BBox[0 0 100 100]/Matrix[0 4 -4 0 0 0]/Resources<</XObject<</Im0 10 0 R>>>>", "q 72 0 0 36 0 0 cm /Im0 Do Q"u8.ToArray());
        b.SetStream(4, "", "q 36 0 0 18 0 0 cm /Im0 Do Q q 1 0 0 1 300 100 cm /Fm0 Do Q"u8.ToArray());
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 10 0 R/Fm0 11 0 R>>>>/Contents 4 0 R>>");
        byte[] input = b.Build();

        PdfFile file = PdfFile.Open(input, Limit);
        Dictionary<int, PlacedSize> sizes = ImagePlacement.Measure(file, ObjectGraph.Collect(file, false, false, CancellationToken.None), [10], Limit, CancellationToken.None);

        // Largest placement: 4 inches wide (72 * 4 points), 2 inches high: 200 dpi both ways.
        Assert.AreEqual(4.0, sizes[10].Width, 1e-9);
        Assert.AreEqual(2.0, sizes[10].Height, 1e-9);
        Assert.AreEqual((400, 200), LossyImages.Target(800, 400, sizes[10], 100));
        Assert.IsNull(LossyImages.Target(800, 400, sizes[10], 150));
    }

    [TestMethod]
    public void UserUnitScalesThePage()
    {
        Raster photo = SyntheticImages.Photo(600, 600, 1);
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, Raw(photo), Deflate(photo.Samples));
        b.SetStream(4, "", "q 72 0 0 72 0 0 cm /Im0 Do Q"u8.ToArray());
        b.Set(3, "<</Type/Page/Parent 2 0 R/UserUnit 3/MediaBox[0 0 200 200]/Resources<</XObject<</Im0 10 0 R>>>>/Contents 4 0 R>>");
        PdfFile file = PdfFile.Open(b.Build(), Limit);

        Dictionary<int, PlacedSize> sizes = ImagePlacement.Measure(file, ObjectGraph.Collect(file, false, false, CancellationToken.None), [10], Limit, CancellationToken.None);

        Assert.AreEqual(3.0, sizes[10].Width, 1e-9);
    }

    [TestMethod]
    public void AnnotationAppearancesAndPatternsAreMeasured()
    {
        Raster photo = SyntheticImages.Photo(300, 300, 1);
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, Raw(photo), Deflate(photo.Samples));
        b.SetStream(11, Raw(photo), Deflate(photo.Samples));

        // The appearance's 1 by 1 box is stretched onto a 2 inch square.
        b.SetStream(12, "/Type/XObject/Subtype/Form/BBox[0 0 1 1]/Resources<</XObject<</Im0 10 0 R>>>>", "/Im0 Do"u8.ToArray());
        b.Set(13, "<</Type/Annot/Subtype/Stamp/Rect[100 100 244 244]/AP<</N 12 0 R>>>>");

        // A tiling pattern whose matrix scales its cell by 3; the cell draws the image 36 points wide.
        b.SetStream(14, "/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 36 36]/XStep 36/YStep 36/Matrix[3 0 0 3 0 0]/Resources<</XObject<</Im1 11 0 R>>>>",
            "q 36 0 0 36 0 0 cm /Im1 Do Q"u8.ToArray());
        b.SetStream(4, "", "/Pattern cs /P0 scn 0 0 300 300 re f"u8.ToArray());
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Pattern<</P0 14 0 R>>>>/Annots[13 0 R]/Contents 4 0 R>>");
        PdfFile file = PdfFile.Open(b.Build(), Limit);

        Dictionary<int, PlacedSize> sizes = ImagePlacement.Measure(file, ObjectGraph.Collect(file, false, false, CancellationToken.None), [10, 11], Limit, CancellationToken.None);

        Assert.AreEqual(2.0, sizes[10].Width, 1e-9);
        Assert.AreEqual(1.5, sizes[11].Height, 1e-9);
    }

    [TestMethod]
    public void ImagesReachableOutsideTheWalkAreNeverMeasured()
    {
        Raster photo = SyntheticImages.Photo(300, 300, 1);
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        for (int i = 10; i <= 14; i++) b.SetStream(i, Raw(photo), Deflate(photo.Samples));

        // 11: also in a Type 3 font's resources. 12: on a page sharing its resources with a Type 3
        // font that has none. 13: an alternate of image 10. 14: a page thumbnail only.
        b.Set(20, "<</Type/Font/Subtype/Type3/FontBBox[0 0 1 1]/FontMatrix[1 0 0 1 0 0]/CharProcs<<>>/Encoding<</Differences[]>>/FirstChar 0/LastChar 0/Widths[0]/Resources<</XObject<</X 11 0 R>>>>>>");
        b.Set(21, "<</Type/Font/Subtype/Type3/FontBBox[0 0 1 1]/FontMatrix[1 0 0 1 0 0]/CharProcs<<>>/Encoding<</Differences[]>>/FirstChar 0/LastChar 0/Widths[0]>>");
        b.Set(22, "<</Image 13 0 R>>");
        b.SetStream(4, "", "q 72 0 0 72 0 0 cm /Im0 Do /Im1 Do Q"u8.ToArray());
        b.SetStream(5, "", "q 72 0 0 72 0 0 cm /Im2 Do Q"u8.ToArray());
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Thumb 14 0 R/Resources<</Font<</T3 20 0 R>>/XObject<</Im0 10 0 R/Im1 11 0 R>>>>/Contents 4 0 R>>");
        b.Set(6, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</T3 21 0 R>>/XObject<</Im2 12 0 R>>>>/Contents 5 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R 6 0 R]/Count 2>>");
        b.SetStream(10, Raw(photo, "/Alternates[22 0 R]"), Deflate(photo.Samples));
        PdfFile file = PdfFile.Open(b.Build(), Limit);

        Dictionary<int, PlacedSize> sizes = ImagePlacement.Measure(file, ObjectGraph.Collect(file, false, false, CancellationToken.None), [10, 11, 12, 13, 14], Limit, CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { 10 }, sizes.Keys.ToArray());
    }

    [TestMethod]
    public void HostileContentDoesNotBreakTheWalk()
    {
        Raster photo = SyntheticImages.Photo(300, 300, 1);
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R 2 0 R]/Count 1>>");
        b.SetStream(10, Raw(photo), Deflate(photo.Samples));

        // A form that draws itself, a damaged content stream, operands of the wrong type and a huge cm.
        b.SetStream(11, "/Type/XObject/Subtype/Form/BBox[0 0 1 1]/Resources<</XObject<</Fm0 11 0 R/Im0 10 0 R>>>>", "/Fm0 Do 2 0 0 2 0 0 cm /Im0 Do"u8.ToArray());
        b.SetStream(4, "/Filter/FlateDecode", [1, 2, 3, 4]);
        b.SetStream(5, "", "(x) 1 0 0 cm /Im0 Do q q q Q Q Q Q Q 1e308 0 0 1e308 0 0 cm /Fm0 Do ] >> /Im0 Do"u8.ToArray());
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Im0 10 0 R/Fm0 11 0 R>>>>/Contents[4 0 R 5 0 R]>>");
        byte[] input = b.Build();
        PdfFile file = PdfFile.Open(input, Limit);

        Dictionary<int, PlacedSize> sizes = ImagePlacement.Measure(file, ObjectGraph.Collect(file, false, false, CancellationToken.None), [10], Limit, CancellationToken.None);

        // The damaged stream taints the page's resources, so the image is not measured.
        Assert.IsEmpty(sizes);
        (_, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 72, AllowRepairedFiles = true });
        Assert.AreEqual(0, result.Statistics.ImagesDownsampled, result.Reason);
    }

    [TestMethod]
    public void JpegSourceIsDownsampledAsJpegNeverFiner()
    {
        Raster photo = SyntheticImages.Photo(1200, 900, 3);
        byte[] jpeg = SyntheticImages.Jpeg(photo, 70);
        byte[] input = Pdf((Dct(photo), jpeg, "144 0 0 108 50 50"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 150, JpegQuality = 90, MinImageSsim = 0.9 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesDownsampled);
        PdfStream image = Image(output, 0);
        Assert.AreEqual(300, Int(image, "Width"));
        Assert.AreEqual("DCTDecode", image.Dictionary.GetName("Filter"));
        using JpegImage written = JpegReader.Read(image.Data.Span, Limit);
        Assert.AreEqual(300, written.Width);
        Assert.IsLessThanOrEqualTo(70, JpegQuantization.EstimateQuality(written.Components[0].Quantization));
    }

    [TestMethod]
    public void CmykJpegKeepsItsInkConvention()
    {
        Raster photo = SyntheticImages.Photo(600, 600, 4);
        byte[] jpeg = SyntheticImages.Jpeg(photo, 80);
        byte[] input = Pdf((Dct(photo, "/Decode[1 0 1 0 1 0 1 0]"), jpeg, "72 0 0 72 50 50"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 150 });

        Assert.AreEqual(1, result.Statistics.ImagesDownsampled, result.Reason);
        PdfStream image = Image(output, 0);
        Assert.AreEqual(150, Int(image, "Width"));
        Assert.IsNotNull(image.Dictionary["Decode"]);
        Raster decoded = JpegDecoder.Decode(image.Data.Span, null, Limit);
        Raster expected = Resampler.Downsample(JpegDecoder.Decode(jpeg, null, Limit), 150, 150);
        Assert.IsGreaterThan(0.8, Ssim.Compare(expected, decoded));
        Assert.IsLessThan(2.0, Ssim.MeanShift(expected, decoded));
    }

    [TestMethod]
    public void PhotographsBecomeJpegAndGraphicsStayLossless()
    {
        Raster photo = SyntheticImages.Photo(400, 300, 3);
        Raster screen = SyntheticImages.Screenshot(400, 300, 3);
        byte[] input = Pdf((Raw(photo), Deflate(photo.Samples), "400 0 0 300 0 0"), (Raw(screen), Deflate(screen.Samples), "400 0 0 300 0 400"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { ConvertImagesToJpeg = true, JpegQuality = 80 });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesConvertedToJpeg);
        StringAssert.Contains(result.Reason, "1 image(s) converted to JPEG at quality 80");
        Assert.AreEqual("DCTDecode", Image(output, 0).Dictionary.GetName("Filter"));
        Assert.AreEqual("FlateDecode", Image(output, 1).Dictionary.GetName("Filter"));
        Assert.AreEqual(400, Int(Image(output, 0), "Width"));
        Assert.IsNull(PdfEquivalence.FindDifference(input, output, Limit, false, 0.9));
    }

    [TestMethod]
    public void RawPhotographCanBeDownsampledAndConverted()
    {
        Raster photo = SyntheticImages.Photo(1200, 900, 3);
        byte[] input = Pdf((Raw(photo), Deflate(photo.Samples), "144 0 0 108 50 50"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input,
            NoThreshold with { MaxImageDpi = 150, ConvertImagesToJpeg = true, JpegQuality = 75, MinImageSsim = 0.9, ProgressiveJpeg = true });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesDownsampled);
        Assert.AreEqual(1, result.Statistics.ImagesConvertedToJpeg);
        StringAssert.Contains(result.Reason, "1 image(s) downsampled to 150 dpi, 1 image(s) converted to JPEG at quality 75");
        PdfStream image = Image(output, 0);
        Assert.AreEqual("DCTDecode", image.Dictionary.GetName("Filter"));
        Assert.AreEqual(300, Int(image, "Width"));
        Assert.IsNull(PdfEquivalence.FindDifference(input, output, Limit, false, 0.9));
    }

    [TestMethod]
    public void QualityGateKeepsImagesThatWouldSuffer()
    {
        Raster photo = SyntheticImages.Photo(400, 300, 3);
        byte[] input = Pdf((Raw(photo), Deflate(photo.Samples), "400 0 0 300 0 0"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { ConvertImagesToJpeg = true, JpegQuality = 10, MinImageSsim = 0.99 });

        Assert.AreEqual(0, result.Statistics.ImagesConvertedToJpeg, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesKeptByQualityGate);
        if (result.Disposition == OptimizationDisposition.Optimized) Assert.AreEqual("FlateDecode", Image(output, 0).Dictionary.GetName("Filter"));

        // The same gate stops requantization; the JPEG then only gets the lossless pass.
        byte[] jpeg = Pdf((Dct(new Raster(320, 240, 3, new byte[320 * 240 * 3])), JpegCodecTests.Fixture("photo-q95.jpg"), "320 0 0 240 0 0"));
        (output, result) = PdfOptimizer.Optimize(jpeg, NoThreshold with { JpegQuality = 20, MinImageSsim = 0.99 });
        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(0, result.Statistics.JpegImagesRequantized);
        Assert.AreEqual(1, result.Statistics.ImagesKeptByQualityGate);
        Assert.IsNull(PdfEquivalence.FindDifference(jpeg, output));

        // A gate the change meets lets it through, and the proof checks it again.
        (output, result) = PdfOptimizer.Optimize(jpeg, NoThreshold with { JpegQuality = 60, MinImageSsim = 0.5 });
        Assert.AreEqual(1, result.Statistics.JpegImagesRequantized, result.Reason);
        Assert.IsNull(PdfEquivalence.FindDifference(jpeg, output, Limit, true, 0.5));
        Assert.IsNotNull(PdfEquivalence.FindDifference(jpeg, output, Limit, true, 0.9999));
    }

    [TestMethod]
    public void MasksPalettesAndLineArtAreLeftAlone()
    {
        Raster photo = SyntheticImages.Photo(600, 600, 1);
        byte[] flate = Deflate(photo.Samples);
        string draw = "72 0 0 72 0 0";
        byte[] input = Pdf(
            b => b.SetStream(30, Raw(photo), flate),
            (Raw(photo, "/SMask 30 0 R"), flate, draw),
            (Raw(photo, "/Mask[0 10]"), flate, draw),
            ("/Type/XObject/Subtype/Image/Width 600/Height 600/ImageMask true/Filter/FlateDecode", Deflate(new byte[75 * 600]), draw),
            ($"/Type/XObject/Subtype/Image/Width 600/Height 600/ColorSpace[/Indexed/DeviceRGB 255 <{new string('8', 1536)}>]/BitsPerComponent 8/Filter/FlateDecode", flate, draw),
            ("/Type/XObject/Subtype/Image/Width 600/Height 600/ColorSpace/DeviceGray/BitsPerComponent 1/Filter/FlateDecode", Deflate(new byte[75 * 600]), draw),
            ("/Type/XObject/Subtype/Image/Width 600/Height 300/ColorSpace/DeviceGray/BitsPerComponent 16/Filter/FlateDecode", flate, draw),
            ("/Type/XObject/Subtype/Image/Width 600/Height 600/ColorSpace[/Separation/Spot/DeviceGray<</FunctionType 2/Domain[0 1]/N 1>>]/BitsPerComponent 8/Filter/FlateDecode", flate, draw),
            ("/Type/XObject/Subtype/Image/Width 600/Height 600/ColorSpace[/Lab<</WhitePoint[0.95 1 1.09]>>]/BitsPerComponent 8/Filter/FlateDecode", Deflate(new byte[600 * 600 * 3]), draw));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 72, ConvertImagesToJpeg = true, JpegQuality = 50 });

        // Only the base image with a soft mask (and no /Matte) is downsampled, and converted; the mask is not.
        Assert.AreEqual(1, result.Statistics.ImagesDownsampled, result.Reason);
        Assert.AreEqual(1, result.Statistics.ImagesConvertedToJpeg, result.Reason);
        Assert.AreEqual("DCTDecode", Image(output, 0).Dictionary.GetName("Filter"));
        Assert.AreEqual(72, Int(Image(output, 0), "Width"));
        Assert.AreEqual(600, Int(SoftMask(output), "Width"));
        for (int i = 1; i <= 7; i++) Assert.AreNotEqual(72, Int(Image(output, i), "Width"), "image " + i);
    }

    [TestMethod]
    public void SoftMaskWithMatteKeepsItsImageSize()
    {
        Raster photo = SyntheticImages.Photo(600, 600, 1);
        byte[] flate = Deflate(photo.Samples);
        byte[] input = Pdf(
            b => b.SetStream(30, Raw(photo, "/Matte[0]"), flate),
            (Raw(photo, "/SMask 30 0 R"), flate, "72 0 0 72 0 0"));

        (_, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 72 });

        Assert.AreEqual(0, result.Statistics.ImagesDownsampled, result.Reason);
    }

    [TestMethod]
    public void ProofCatchesBrokenResampling()
    {
        Raster photo = SyntheticImages.Photo(400, 300, 3);
        byte[] source = Pdf((Raw(photo), Deflate(photo.Samples), "400 0 0 300 0 0"));
        Raster small = Resampler.Downsample(photo, 100, 75);

        string? Check(Raster r) => PdfEquivalence.FindDifference(source, Pdf((Raw(r), Deflate(r.Samples), "400 0 0 300 0 0")), Limit, false, 0.5);

        Assert.IsNull(Check(small));
        StringAssert.Contains(Check(SyntheticImages.Flipped(small)), "coarse scale");
        StringAssert.Contains(Check(Resampler.Downsample(photo, 100, 40)), "aspect ratio");
        // Different noise on the same scene passes the floor, which only catches broken output, but not a real gate.
        Raster other = SyntheticImages.Photo(400, 300, 3, seed: 2);
        Assert.IsNull(Check(other));
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Pdf((Raw(other), Deflate(other.Samples), "400 0 0 300 0 0")), Limit, false, 0.95), "SSIM");

        var gray = new Raster(100, 75, 1, Ssim.Luminance(small));
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Pdf((Raw(gray), Deflate(gray.Samples), "400 0 0 300 0 0")), Limit, false, 0.5), "ColorSpace");

        var shifted = (byte[])small.Samples.Clone();
        for (int i = 0; i < shifted.Length; i += 3) shifted[i] = (byte)Math.Min(255, shifted[i] + 20);
        StringAssert.Contains(Check(new Raster(100, 75, 3, shifted)), "colors");

        // Without the allowance a resampled image is a difference like any other.
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Pdf((Raw(small), Deflate(small.Samples), "400 0 0 300 0 0"))), "Width");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfEquivalence.FindDifference(source, source, Limit, false, 1.5));
    }

    [TestMethod]
    public void ProofRejectsANewSizeOverUnchangedData()
    {
        // The dictionary claims half the size over the very same bytes: a crop at the wrong stride.
        Raster photo = SyntheticImages.Photo(400, 300, 3);
        byte[] flate = Deflate(photo.Samples);
        byte[] source = Pdf((Raw(photo), flate, "400 0 0 300 0 0"));
        string halved = Raw(photo).Replace("/Width 400/Height 300", "/Width 200/Height 150");

        Assert.IsNotNull(PdfEquivalence.FindDifference(source, Pdf((halved, flate, "400 0 0 300 0 0")), Limit, false, 0.5));
        Assert.IsNull(PdfEquivalence.FindDifference(source, source, Limit, false, 0.5));

        // Data that cannot be decoded at all may not change size either.
        byte[] broken = [0x78, 0x9C, 1, 2, 3, 4, 5, 6];
        byte[] damaged = Pdf((Raw(photo), broken, "400 0 0 300 0 0"));
        StringAssert.Contains(PdfEquivalence.FindDifference(damaged, Pdf((halved, broken, "400 0 0 300 0 0")), Limit, false, 0.5), "undecodable");
        Assert.IsNull(PdfEquivalence.FindDifference(damaged, damaged, Limit, false, 0.5));
    }

    [TestMethod]
    public void ProofKeepsMasksAndTheirImagesExact()
    {
        Raster photo = SyntheticImages.Photo(400, 300, 1);
        Raster small = Resampler.Downsample(photo, 200, 150);
        byte[] flate = Deflate(photo.Samples), smallFlate = Deflate(small.Samples);
        const string draw = "400 0 0 300 0 0";

        // A soft mask is opacity, not a picture: it may not be resampled, while its image may.
        byte[] source = Pdf(b => b.SetStream(30, Raw(photo), flate), (Raw(photo, "/SMask 30 0 R"), flate, draw));
        StringAssert.Contains(PdfEquivalence.FindDifference(source, Pdf(b => b.SetStream(30, Raw(small), smallFlate), (Raw(photo, "/SMask 30 0 R"), flate, draw)), Limit, false, 0.5), "Width");
        Assert.IsNull(PdfEquivalence.FindDifference(source, Pdf(b => b.SetStream(30, Raw(photo), flate), (Raw(small, "/SMask 30 0 R"), smallFlate, draw)), Limit, false, 0.5));

        // A /Matte soft mask ties the image to its size; so does a color key or a stencil mask.
        byte[] matte = Pdf(b => b.SetStream(30, Raw(photo, "/Matte[0]"), flate), (Raw(photo, "/SMask 30 0 R"), flate, draw));
        StringAssert.Contains(PdfEquivalence.FindDifference(matte, Pdf(b => b.SetStream(30, Raw(photo, "/Matte[0]"), flate), (Raw(small, "/SMask 30 0 R"), smallFlate, draw)), Limit, false, 0.5), "Width");
        byte[] keyed = Pdf((Raw(photo, "/Mask[0 10]"), flate, draw));
        StringAssert.Contains(PdfEquivalence.FindDifference(keyed, Pdf((Raw(small, "/Mask[0 10]"), smallFlate, draw)), Limit, false, 0.5), "Width");

        // Requantization is refused on a soft mask and accepted on its image.
        Raster gray = SyntheticImages.Photo(160, 120, 1);
        byte[] jpeg = SyntheticImages.Jpeg(gray, 95);
        byte[] coarse = JpegTranscoder.TryTranscode(jpeg, 30, null, 1, Limit, out bool requantized)!;
        Assert.IsTrue(requantized);
        byte[] Masked(byte[] maskData, byte[] imageData) => Pdf(b => b.SetStream(30, Dct(gray), maskData), (Dct(gray, "/SMask 30 0 R"), imageData, draw));
        byte[] original = Masked(jpeg, jpeg);
        Assert.IsNotNull(PdfEquivalence.FindDifference(original, Masked(coarse, jpeg), Limit, allowJpegRequantization: true));
        Assert.IsNull(PdfEquivalence.FindDifference(original, Masked(jpeg, coarse), Limit, allowJpegRequantization: true));

        // A color key matches exact samples, so the optimizer keeps such a JPEG lossless.
        byte[] colorKeyed = Pdf((Dct(gray, "/Mask[0 10]"), jpeg, draw));
        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(colorKeyed, NoThreshold with { JpegQuality = 30 });
        Assert.AreEqual(0, result.Statistics.JpegImagesRequantized, result.Reason);
        Assert.IsNull(PdfEquivalence.FindDifference(colorKeyed, output));
    }

    [TestMethod]
    public void ContentPartsCountAgainstTheDecodeLimit()
    {
        // A page lists one 2 KB part many times; only the first part draws the form small. The
        // joined content passes the decode limit, so the page, and every form it can reach, is
        // left unmeasured instead of being joined past the limit.
        Raster photo = SyntheticImages.Photo(300, 300, 1);
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.SetStream(10, Raw(photo), Deflate(photo.Samples));
        b.SetStream(11, "/Type/XObject/Subtype/Form/BBox[0 0 1 1]/Resources<</XObject<</Im0 10 0 R>>>>", "/Im0 Do"u8.ToArray());
        b.SetStream(4, "", "q 72 0 0 72 0 0 cm /Fm0 Do Q"u8.ToArray());
        b.SetStream(5, "", Enumerable.Repeat((byte)' ', 2000).ToArray());
        string parts = "4 0 R " + string.Concat(Enumerable.Repeat("5 0 R ", 50));
        b.Set(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</XObject<</Fm0 11 0 R>>>>/Contents[{parts}]>>");
        PdfFile file = PdfFile.Open(b.Build(), Limit);
        ObjectGraph graph = ObjectGraph.Collect(file, false, false, CancellationToken.None);

        Assert.IsEmpty(ImagePlacement.Measure(file, graph, [10], 50_000, CancellationToken.None));
        Assert.AreEqual(1.0, ImagePlacement.Measure(file, graph, [10], Limit, CancellationToken.None)[10].Width, 1e-9);
    }

    [TestMethod]
    public void DamagedImagesAreLeftAlone()
    {
        Raster photo = SyntheticImages.Photo(1200, 900, 3);
        byte[] jpeg = SyntheticImages.Jpeg(photo, 70);
        byte[] truncated = jpeg.AsSpan(0, jpeg.Length / 3).ToArray();
        byte[] input = Pdf(
            (Dct(photo), truncated, "144 0 0 108 50 50"),
            (Raw(photo), Deflate(photo.Samples.AsSpan(0, 1000).ToArray()), "144 0 0 108 50 50"),
            (Raw(photo).Replace("/Width 1200", "/Width 120000"), Deflate(photo.Samples), "144 0 0 108 50 50"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { MaxImageDpi = 72, ConvertImagesToJpeg = true, JpegQuality = 50 });

        Assert.AreEqual(0, result.Statistics.ImagesDownsampled, result.Reason);
        Assert.AreEqual(0, result.Statistics.ImagesConvertedToJpeg, result.Reason);
        if (result.Disposition == OptimizationDisposition.Optimized) Assert.IsTrue(Image(output, 0).Data.Span.SequenceEqual(truncated));
    }

    [TestMethod]
    public void ProgressiveOptionStaysLossless()
    {
        byte[] input = Pdf((Dct(new Raster(320, 240, 3, new byte[320 * 240 * 3])), JpegCodecTests.Fixture("photo-q95.jpg"), "320 0 0 240 0 0"));

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, NoThreshold with { ProgressiveJpeg = true });

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        using JpegImage written = JpegReader.Read(Image(output, 0).Data.Span, Limit);
        Assert.AreEqual(JpegCoding.Progressive, written.Coding);
        Assert.IsNull(PdfEquivalence.FindDifference(input, output));
    }

    [TestMethod]
    public void InvalidLossyOptionsAreRejected()
    {
        byte[] input = Pdf((Raw(SyntheticImages.Photo(20, 20, 1)), Deflate(new byte[400]), "20 0 0 20 0 0"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MaxImageDpi = 0 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinImageSsim = 0 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinImageSsim = 1.01 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinImageSsim = double.NaN }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { ConvertImagesToJpeg = true }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MaxImageDpi = 150, RecompressStreams = false }));
        Assert.ThrowsExactly<ArgumentException>(() => PdfOptimizer.Optimize(input, new PdfOptimizerOptions { ProgressiveJpeg = true, RecompressStreams = false }));
    }
}
