using PdfNetStack.Imaging;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class JpegDecoderTests
{
    private const long Limit = 64 << 20;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpeg", name);

    /// <summary>
    /// The samples libjpeg-turbo 3 (through Pillow 11) decodes from a fixture, with default
    /// settings: integer DCT and fancy upsampling. Adobe CMYK is stored as is, in RGBA.
    /// </summary>
    private static (int Width, int Height, int Channels, byte[] Samples) Reference(string name)
    {
        PngImage png = PngImage.Read(File.ReadAllBytes(Fixture(Path.Combine("Reference", name + ".png"))));
        Assert.AreEqual(8, png.BitDepth);
        int channels = png.ColorType switch { 0 => 1, 2 => 3, 6 => 4, _ => throw new AssertFailedException($"color type {png.ColorType}") };
        return (png.Width, png.Height, channels, png.Decode());
    }

    [TestMethod]
    [DataRow("gray-baseline", 1)]
    [DataRow("gray-progressive", 1)]
    [DataRow("rgb-baseline", 1)]
    [DataRow("cmyk-baseline", 1)]
    [DataRow("cmyk-progressive", 1)]
    [DataRow("ycc444-baseline", 2)]
    [DataRow("ycc444-optimized", 2)]
    [DataRow("ycc444-progressive", 2)]
    [DataRow("ycc444-restart", 2)]
    [DataRow("ycc422-baseline", 2)]
    [DataRow("ycc422-optimized", 2)]
    [DataRow("ycc422-progressive", 2)]
    [DataRow("ycc422-restart", 2)]
    [DataRow("ycc420-baseline", 2)]
    [DataRow("ycc420-optimized", 2)]
    [DataRow("ycc420-progressive", 2)]
    [DataRow("ycc420-restart", 2)]
    public void MatchesLibjpeg(string name, int tolerance)
    {
        Raster raster = JpegDecoder.Decode(File.ReadAllBytes(Fixture(name + ".jpg")), null, Limit);
        (int width, int height, int channels, byte[] expected) = Reference(name);
        Assert.AreEqual(width, raster.Width);
        Assert.AreEqual(height, raster.Height);
        Assert.AreEqual(channels, raster.Channels);

        // libjpeg's integer DCT rounds differently from an exact one, and YCbCr conversion
        // amplifies that by up to 1.8: a step or two per sample, never more, rarely at all.
        int worst = 0, off = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int d = Math.Abs(expected[i] - raster.Samples[i]);
            worst = Math.Max(worst, d);
            if (d > 0) off++;
        }

        Assert.IsLessThanOrEqualTo(tolerance, worst, $"largest difference in {name}");
        Assert.IsLessThan(expected.Length / 10, off, $"samples that differ in {name}");
    }

    [TestMethod]
    public void HonoursTheColorTransform()
    {
        byte[] data = File.ReadAllBytes(Fixture("ycc444-baseline.jpg"));
        Raster converted = JpegDecoder.Decode(data, null, Limit);
        Raster raw = JpegDecoder.Decode(data, 0, Limit);

        // /ColorTransform 0 hands the YCbCr samples over unconverted; mid-gray chroma is near 128.
        Assert.HasCount(converted.Samples.Length, raw.Samples);
        CollectionAssert.AreNotEqual(converted.Samples, raw.Samples);
        Assert.IsTrue(Enumerable.Range(0, raw.Width * raw.Height).Select(i => raw.Samples[(i * 3) + 1]).Any(cb => cb is > 100 and < 156));
    }

    [TestMethod]
    public void DecodesALargerPhotoQuickly()
    {
        byte[] data = File.ReadAllBytes(Fixture("photo-q95.jpg"));
        Raster raster = JpegDecoder.Decode(data, null, Limit);
        Assert.AreEqual(320, raster.Width);
        Assert.AreEqual(240, raster.Height);
        Assert.AreEqual(3, raster.Channels);

        // Not flat: a photo has detail in every channel.
        for (int c = 0; c < 3; c++)
        {
            IEnumerable<byte> channel = Enumerable.Range(0, 320 * 240).Select(i => raster.Samples[(i * 3) + c]);
            Assert.IsGreaterThan(50, channel.Max() - channel.Min());
        }
    }

    [TestMethod]
    public void RejectsDamagedOrOversizedData()
    {
        byte[] data = File.ReadAllBytes(Fixture("ycc420-baseline.jpg"));
        Assert.ThrowsExactly<JpegFormatException>(() => JpegDecoder.Decode(data, null, 1000));
        Assert.ThrowsExactly<JpegFormatException>(() => JpegDecoder.Decode(data.AsSpan(0, data.Length / 3), null, Limit));
        Assert.ThrowsExactly<JpegFormatException>(() => JpegDecoder.Decode([0xFF, 0xD8, 0xFF, 0xD9], null, Limit));

        // Mutations either decode or fail with a JPEG error, nothing else.
        var random = new Random(7);
        for (int round = 0; round < 300; round++)
        {
            byte[] copy = (byte[])data.Clone();
            for (int k = 0; k < 4; k++) copy[random.Next(2, copy.Length)] = (byte)random.Next(256);
            try
            {
                JpegDecoder.Decode(copy, null, Limit);
            }
            catch (JpegFormatException)
            {
            }
        }
    }
}
