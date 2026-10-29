using PdfNetStack.Imaging;

namespace PdfNetStack.Tests;

/// <summary>The building blocks of the lossy image profile: JPEG encoding, progressive coding, resampling, SSIM and classification.</summary>
[TestClass]
public sealed class ImageCodecTests
{
    private const long Limit = 64 << 20;

    private static byte[] Fixture(string name) => JpegCodecTests.Fixture(name);

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    [DataRow(4, false)]
    public void EncodedJpegDecodesCloseToTheSource(int channels, bool subsample)
    {
        Raster photo = SyntheticImages.Photo(101, 67, channels);

        byte[] jpeg = SyntheticImages.Jpeg(photo, 92, subsample);
        Raster decoded = JpegDecoder.Decode(jpeg, null, Limit);

        Assert.AreEqual(photo.Width, decoded.Width);
        Assert.AreEqual(photo.Height, decoded.Height);
        Assert.AreEqual(channels, decoded.Channels);
        Assert.IsGreaterThan(0.93, Ssim.Compare(photo, decoded));
        Assert.IsLessThan(1.5, Ssim.MeanShift(photo, decoded));
    }

    [TestMethod]
    public void QualityTradesSizeForFidelity()
    {
        Raster photo = SyntheticImages.Photo(160, 120, 3);
        byte[] high = SyntheticImages.Jpeg(photo, 90);
        byte[] low = SyntheticImages.Jpeg(photo, 30);

        Assert.IsLessThan(high.Length, low.Length);
        double ssimHigh = Ssim.Compare(photo, JpegDecoder.Decode(high, null, Limit));
        double ssimLow = Ssim.Compare(photo, JpegDecoder.Decode(low, null, Limit));
        Assert.IsGreaterThan(ssimLow, ssimHigh);
    }

    [TestMethod]
    public void EncoderMarksItsColorModel()
    {
        using JpegImage rgb = JpegReader.Read(SyntheticImages.Jpeg(SyntheticImages.Photo(32, 32, 3), 80), Limit);
        Assert.IsTrue(rgb.HasJfif);
        Assert.IsNull(rgb.AdobeTransform);
        Assert.AreEqual(2, rgb.MaxHorizontal);

        using JpegImage cmyk = JpegReader.Read(SyntheticImages.Jpeg(SyntheticImages.Photo(32, 32, 4), 80), Limit);
        Assert.AreEqual(0, cmyk.AdobeTransform);
        Assert.IsFalse(cmyk.ChromaComponents(null).Any(c => c), "four channels are coded as stored");
    }

    [TestMethod]
    public void QualityEstimateRecoversTheLibjpegScale()
    {
        foreach (int quality in new[] { 10, 50, 75, 90, 95, 100 })
        {
            Assert.AreEqual(quality, JpegQuantization.EstimateQuality(JpegQuantization.ScaledTable(quality, chrominance: false)));
        }

        using JpegImage photo = JpegReader.Read(Fixture("photo-q95.jpg"), Limit);
        Assert.AreEqual(95, JpegQuantization.EstimateQuality(photo.Components[0].Quantization));
    }

    [TestMethod]
    [DataRow("ycc420-baseline.jpg")]
    [DataRow("ycc422-baseline.jpg")]
    [DataRow("ycc444-baseline.jpg")]
    [DataRow("gray-baseline.jpg")]
    [DataRow("cmyk-baseline.jpg")]
    [DataRow("rgb-baseline.jpg")]
    [DataRow("photo-q95.jpg")]
    [DataRow("ycc420-progressive.jpg")]
    public void ProgressiveCodingKeepsEveryCoefficient(string name)
    {
        byte[] source = Fixture(name);
        using JpegImage image = JpegReader.Read(source, Limit);

        byte[]? progressive = JpegWriter.TryWriteProgressive(image, source.Length);

        Assert.IsNotNull(progressive);
        using (JpegImage reread = JpegReader.Read(progressive, Limit))
        {
            Assert.AreEqual(JpegCoding.Progressive, reread.Coding);
        }

        Assert.IsNull(JpegEquivalence.FindDifference(source, progressive, allowRequantization: false, Limit));
        CollectionAssert.AreEqual(JpegDecoder.Decode(source, null, Limit).Samples, JpegDecoder.Decode(progressive, null, Limit).Samples);
    }

    [TestMethod]
    public void ProgressiveCodingWinsOnLargerImages()
    {
        byte[] source = Fixture("photo-q95.jpg");
        using JpegImage image = JpegReader.Read(source, Limit);

        byte[] sequential = JpegWriter.TryWrite(image, source.Length)!;
        byte[] smallest = JpegWriter.TryWriteSmallest(image, source.Length, allowProgressive: true)!;

        Assert.IsLessThan(sequential.Length, smallest.Length);
        using JpegImage chosen = JpegReader.Read(smallest, Limit);
        Assert.AreEqual(JpegCoding.Progressive, chosen.Coding);
    }

    [TestMethod]
    public void ProgressiveCodingHandlesLongZeroRunsAndLargeMagnitudes()
    {
        // Flat areas give long end-of-band runs; hard edges at quality 100 give large coefficients.
        var samples = new byte[256 * 256];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)((i % 256) < 128 ? 0 : (i / 256) < 200 ? 255 : 128);
        var raster = new Raster(256, 256, 1, samples);
        using JpegImage image = JpegEncoder.Encode(raster, 100, subsampleChroma: false);

        byte[] baseline = JpegWriter.TryWrite(image, 1 << 16)!;
        byte[] progressive = JpegWriter.TryWriteProgressive(image, 1 << 16)!;

        Assert.IsNull(JpegEquivalence.FindDifference(baseline, progressive, allowRequantization: false, Limit));
    }

    [TestMethod]
    public void ResamplingKeepsFlatAreasAndRampsExact()
    {
        var flat = new Raster(90, 60, 3, Enumerable.Repeat((byte)77, 90 * 60 * 3).ToArray());
        Raster small = Resampler.Downsample(flat, 31, 20);
        Assert.AreEqual(31, small.Width);
        Assert.AreEqual(20, small.Height);
        Assert.IsTrue(small.Samples.All(v => v == 77));

        // A linear ramp resamples to the same ramp at the output sample centers, edges aside.
        var ramp = new byte[200 * 10];
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 200; x++) ramp[(y * 200) + x] = (byte)x;
        }

        Raster half = Resampler.Downsample(new Raster(200, 10, 1, ramp), 100, 5);
        for (int x = 4; x < 96; x++) Assert.AreEqual((2 * x) + 0.5, half.Samples[x], 1.0, "column " + x);
    }

    [TestMethod]
    public void ResamplingRemovesDetailTheTargetCannotHold()
    {
        // One pixel stripes cannot survive a 4:1 reduction; a box would alias, Lanczos averages them out.
        var stripes = new byte[400 * 40];
        for (int i = 0; i < stripes.Length; i++) stripes[i] = (byte)(i % 2 == 0 ? 0 : 255);
        Raster small = Resampler.Downsample(new Raster(400, 40, 1, stripes), 100, 10);
        for (int y = 0; y < 10; y++)
        {
            // The outermost samples lean toward the repeated edge pixel.
            for (int x = 2; x < 98; x++) Assert.AreEqual(127.5, small.Samples[(y * 100) + x], 1.0);
        }
    }

    [TestMethod]
    public void ResamplingRefusesToEnlarge()
    {
        Raster photo = SyntheticImages.Photo(20, 20, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Resampler.Downsample(photo, 21, 20));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Resampler.Downsample(photo, 0, 10));
        Assert.AreSame(photo, Resampler.Downsample(photo, 20, 20));
    }

    [TestMethod]
    public void SsimRanksDamage()
    {
        Raster photo = SyntheticImages.Photo(120, 90, 3);
        Assert.AreEqual(1.0, Ssim.Compare(photo, photo), 1e-12);
        Assert.IsLessThan(0.75, Ssim.Compare(photo, SyntheticImages.Flipped(photo)));

        var noisy = (byte[])photo.Samples.Clone();
        var random = new Random(5);
        for (int i = 0; i < noisy.Length; i++) noisy[i] = (byte)Math.Max(0, Math.Min(255, noisy[i] + random.Next(-40, 41)));
        double damaged = Ssim.Compare(photo, new Raster(photo.Width, photo.Height, 3, noisy));
        Assert.IsTrue(damaged is > 0.2 and < 0.9, damaged.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Images smaller than one window are compared as a whole.
        Raster tiny = SyntheticImages.Photo(5, 3, 1);
        Assert.AreEqual(1.0, Ssim.Compare(tiny, tiny), 1e-12);
        Assert.ThrowsExactly<ArgumentException>(() => Ssim.Compare(photo, tiny));
    }

    [TestMethod]
    public void ClassifierTellsPhotographsFromGraphics()
    {
        Assert.IsTrue(ImageClassifier.IsContinuousTone(SyntheticImages.Photo(200, 150, 3)));
        Assert.IsTrue(ImageClassifier.IsContinuousTone(SyntheticImages.Photo(200, 150, 1)));
        Assert.IsFalse(ImageClassifier.IsContinuousTone(SyntheticImages.Screenshot(200, 150, 3)));
        Assert.IsFalse(ImageClassifier.IsContinuousTone(SyntheticImages.Screenshot(200, 150, 1)));
        Assert.IsFalse(ImageClassifier.IsContinuousTone(SyntheticImages.Bilevel(200, 150)));
        Assert.IsFalse(ImageClassifier.IsContinuousTone(SyntheticImages.Photo(12, 12, 3)), "too small to judge");

        // A smooth gradient has thousands of colors but repeats along one axis.
        var gradient = new byte[256 * 64 * 3];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int o = ((y * 256) + x) * 3;
                gradient[o] = (byte)x;
                gradient[o + 1] = (byte)(y * 4);
                gradient[o + 2] = 128;
            }
        }

        Assert.IsTrue(ImageClassifier.IsContinuousTone(new Raster(256, 64, 3, gradient)));
    }
}
