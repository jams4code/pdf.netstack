using PdfNetStack.Drawing;
using PdfNetStack.Html;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>
/// WebP input against the fixtures of tools/make_webp_fixtures.py. The references are libwebp's
/// own output, and every file, lossy ones included, must decode to exactly the same pixels.
/// </summary>
[TestClass]
public sealed class WebPTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "WebP", name);

    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WebP"), "*.webp").Select(f => new object[] { Path.GetFileNameWithoutExtension(f) }).OrderBy(n => (string)n[0]);

    [TestMethod]
    [DynamicData(nameof(Files))]
    public void MatchesLibwebpExactly(string name)
    {
        DecodedImage decoded = WebPDecoder.Read(File.ReadAllBytes(Fixture(name + ".webp")));
        (int width, int height, byte[] rgba) = DecodedImages.Reference(Fixture(name + ".png"));
        Assert.AreEqual(width, decoded.Width);
        Assert.AreEqual(height, decoded.Height);
        byte[] actual = DecodedImages.Rgba(decoded);

        // Color under fully transparent pixels counts too: libwebp keeps it, and so must this.
        for (int i = 0; i < rgba.Length; i++)
        {
            if (rgba[i] != actual[i]) Assert.Fail($"{name}: pixel {i / 4} channel {i % 4} is {actual[i]}, expected {rgba[i]}");
        }
    }

    [TestMethod]
    [DataRow("lossless", false)]
    [DataRow("lossless-alpha", true)]
    [DataRow("lossy-q50", false)]
    [DataRow("lossy-alpha", true)]
    [DataRow("animated", false)]
    public void LoadsAndEmbeds(string name, bool transparent)
    {
        PdfImage image = PdfImage.Load(Fixture(name + ".webp"));
        Assert.AreEqual(PdfImageFormat.WebP, image.Format);
        Assert.AreEqual(transparent, image.HasTransparency);
        (PdfStream x, PdfBitmap pixels, _) = DecodedImages.Embedded(image);
        Assert.AreEqual(transparent, x.Dictionary["SMask"] is not null);
        (_, _, byte[] rgba) = DecodedImages.Reference(Fixture(name + ".png"));
        byte[] rgb = pixels.Pixels.ToArray();
        for (int i = 0; i < rgb.Length / 3; i++)
        {
            for (int c = 0; c < 3; c++) Assert.AreEqual(rgba[4 * i + c], rgb[3 * i + c], $"{name} pixel {i}");
        }
    }

    [TestMethod]
    public void AnAnimationGivesItsFirstFrame()
    {
        DecodedImage first = WebPDecoder.Read(File.ReadAllBytes(Fixture("animated.webp")));
        DecodedImage still = WebPDecoder.Read(File.ReadAllBytes(Fixture("lossless.webp")));
        Assert.AreEqual(60, first.Width);
        Assert.AreEqual(40, first.Height);
        Assert.AreNotEqual(still.Width, first.Width);
        Assert.AreEqual(1, PdfImage.GetPageCount(Fixture("animated.webp")));
    }

    [TestMethod]
    [DataRow("WebP", "lossy-alpha.webp", "image/webp", 53, 41)]
    [DataRow("Tiff", "g4.tif", "image/tiff", 61, 45)]
    [DataRow("Bmp", "rgb24.bmp", "image/bmp", 97, 61)]
    public void BmpTiffAndWebPLoadInHtml(string folder, string file, string type, int width, int height)
    {
        string data = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, file)));
        string html = $"<p><img src=\"data:{type};base64,{data}\" width={width} height={height}></p>";
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        PdfPageImage image = doc.Pages[0].GetImages().Single();
        Assert.AreEqual(width, image.Width);
        Assert.AreEqual(height, image.Height);
    }

    [TestMethod]
    public void BadFilesAreRejected()
    {
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load("RIFF\x04\0\0\0WEBP\0\0\0\0"u8.ToArray()));
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load("RIFF\x0c\0\0\0WEBPXYZW\0\0\0\0"u8.ToArray()));

        // A lossless header naming version 1.
        byte[] lossless = File.ReadAllBytes(Fixture("lossless.webp"));
        byte[] version = (byte[])lossless.Clone();
        version[20 + 4] |= 0x20;
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(version));

        // A lossy frame that is not a key frame.
        byte[] lossy = File.ReadAllBytes(Fixture("lossy-q50.webp"));
        byte[] inter = (byte[])lossy.Clone();
        inter[20] |= 1;
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(inter));
    }

    [TestMethod]
    public void DamagedFilesDecodeOrFailCleanly()
    {
        foreach (string name in new[] { "lossless", "lossless-alpha", "lossless-palette4", "lossless-noisy", "lossy-q50", "lossy-noisy", "lossy-alpha", "lossy-partitions", "lossy-simple", "animated" })
        {
            DecodedImages.Survives(File.ReadAllBytes(Fixture(name + ".webp")), data => WebPDecoder.Read(data), 250, name.Length);
        }
    }
}
