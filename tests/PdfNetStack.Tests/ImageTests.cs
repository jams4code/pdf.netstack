using System.Globalization;
using System.Text;
using System.Text.Json;
using PdfNetStack.Drawing;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// Images against the fixtures of tools/make_image_fixtures.py: every PNG is embedded, read back
/// from the saved file and turned into RGBA, which must equal the pixels the generator recorded.
/// </summary>
[TestClass]
public sealed class ImageTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);

    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(new PdfSaveOptions { Optimize = false });
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    private static PdfDocument DrawOne(PdfImage image)
    {
        PdfDocument doc = PdfDocument.Create();
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawImage(image, 50, 50, 70, 50);
        return doc;
    }

    private static PdfStream XObject(PdfDocument doc, int page = 0)
    {
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[page].Dictionary["Resources"])!;
        var xobjects = (PdfDictionary)doc.Resolve(resources["XObject"])!;
        return (PdfStream)doc.Resolve(xobjects.First().Value)!;
    }

    public static IEnumerable<object[]> Pngs() =>
        JsonDocument.Parse(File.ReadAllText(Fixture("images.json"))).RootElement.EnumerateObject().Select(p => new object[] { p.Name, p.Value.GetString()! });

    [TestMethod]
    [DynamicData(nameof(Pngs))]
    public void PngPixelsSurviveEmbedding(string name, string expected)
    {
        PdfImage image = PdfImage.Load(Fixture(name));
        Assert.AreEqual(7, image.PixelWidth);
        Assert.AreEqual(5, image.PixelHeight);
        PdfDocument doc = Reopen(DrawOne(image));
        Assert.AreEqual(expected, ToRgba(doc, XObject(doc)), name);
        bool transparentPixel = expected.Where((_, i) => i % 8 >= 6).Any(c => c != 'f');
        if (transparentPixel) Assert.IsTrue(image.HasTransparency, name + " transparency");
    }

    [TestMethod]
    public void SimplePngsKeepTheirCompressedData()
    {
        PdfStream x = XObject(Reopen(DrawOne(PdfImage.Load(Fixture("rgb8.png")))));
        var parms = (PdfDictionary)x.Dictionary["DecodeParms"]!;
        Assert.AreEqual(15, ((PdfNumber)parms["Predictor"]!).ToDouble());
        Assert.AreEqual(3, ((PdfNumber)parms["Colors"]!).ToDouble());
        Assert.IsNull(x.Dictionary["SMask"]);

        PdfStream alpha = XObject(Reopen(DrawOne(PdfImage.Load(Fixture("rgba8.png")))));
        Assert.IsNull(alpha.Dictionary["DecodeParms"]);
        Assert.IsNotNull(alpha.Dictionary["SMask"]);
    }

    [TestMethod]
    public void ResolutionSetsTheNaturalSize()
    {
        PdfImage image = PdfImage.Load(Fixture("rgb8-interlaced-300dpi.png"));
        Assert.AreEqual(300, image.HorizontalResolution, 0.01);
        Assert.AreEqual(7 * 72 / 300.0, image.Width, 1e-3);
        Assert.AreEqual(96, PdfImage.Load(Fixture("rgb8.png")).HorizontalResolution);
    }

    [TestMethod]
    public void JpegsAreEmbeddedAsIs()
    {
        byte[] file = File.ReadAllBytes(Fixture("rotated.jpg"));
        PdfDocument doc = Reopen(DrawOne(PdfImage.Load(file)));
        PdfStream x = XObject(doc);
        Assert.AreEqual("DCTDecode", ((PdfName)x.Dictionary["Filter"]!).Value);
        CollectionAssert.AreEqual(file, x.Data.ToArray());
        Assert.AreEqual("DeviceRGB", ((PdfName)x.Dictionary["ColorSpace"]!).Value);
    }

    [TestMethod]
    public void ExifOrientationTurnsThePhotoUpright()
    {
        // Stored 40 by 20 pixels at 144 per inch, orientation 6: shown 10 points wide, 20 high.
        PdfImage image = PdfImage.Load(Fixture("rotated.jpg"));
        Assert.AreEqual(40, image.PixelWidth);
        Assert.AreEqual(10, image.Width, 1e-9);
        Assert.AreEqual(20, image.Height, 1e-9);

        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawImage(image, 100, 200);
        string content = Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(doc.Pages[0].Dictionary["Contents"])!));

        // Image x runs down the page and image y across: the stored top edge ends on the right.
        StringAssert.Contains(content, "0 20 10 0 100 200 cm\n/Im1 Do");
    }

    [TestMethod]
    public void AdobeCmykJpegsAreInverted()
    {
        PdfStream x = XObject(DrawOne(PdfImage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpeg", "cmyk-baseline.jpg"))));
        Assert.AreEqual("DeviceCMYK", ((PdfName)x.Dictionary["ColorSpace"]!).Value);
        CollectionAssert.AreEqual(new double[] { 1, 0, 1, 0, 1, 0, 1, 0 }, ((PdfArray)x.Dictionary["Decode"]!).Select(v => ((PdfNumber)v).ToDouble()).ToArray());
    }

    [TestMethod]
    public void AnImageIsWrittenOncePerDocument()
    {
        PdfImage image = PdfImage.Load(Fixture("rgba8.png"));
        PdfDocument doc = PdfDocument.Create();
        for (int i = 0; i < 3; i++)
        {
            using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
            g.DrawImage(image, 10, 10);
            g.DrawImage(image, 100, 10, 20, 20);
        }

        PdfDocument back = Reopen(doc);
        int[] numbers = [.. Enumerable.Range(0, 3).Select(p => ((PdfReference)((PdfDictionary)back.Resolve(((PdfDictionary)back.Resolve(back.Pages[p].Dictionary["Resources"])!)["XObject"])!).First().Value).Number)];
        Assert.AreEqual(1, numbers.Distinct().Count());
    }

    [TestMethod]
    public void BadImagesAreRejected()
    {
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        byte[] png = File.ReadAllBytes(Fixture("rgba8.png"));
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(png.AsSpan(0, 30).ToArray()));

        // A header claiming 100,000 by 100,000 pixels is refused before anything is allocated.
        byte[] huge = (byte[])png.Clone();
        huge[16] = 0;
        huge[17] = 1;
        huge[18] = 0x86;
        huge[19] = 0xA0;
        huge[20] = 0;
        huge[21] = 1;
        huge[22] = 0x86;
        huge[23] = 0xA0;
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(huge));
    }

    /// <summary>Decodes an image XObject from a saved file into RGBA hex, as the fixture records it.</summary>
    private static string ToRgba(PdfDocument doc, PdfStream image)
    {
        PdfDictionary d = image.Dictionary;
        int width = (int)((PdfNumber)d["Width"]!).ToDouble(), height = (int)((PdfNumber)d["Height"]!).ToDouble();
        int bpc = (int)((PdfNumber)d["BitsPerComponent"]!).ToDouble();
        byte[] data = doc.GetStreamData(image);
        PdfObject space = doc.Resolve(d["ColorSpace"])!;
        byte[]? lookup = null;
        int colors;
        if (space is PdfArray { Count: 4 } indexed && ((PdfName)indexed[0]).Value == "Indexed")
        {
            lookup = ((PdfString)doc.Resolve(indexed[3])!).Bytes.ToArray();
            colors = 1;
        }
        else
        {
            colors = ((PdfName)space).Value == "DeviceGray" ? 1 : 3;
        }

        byte[]? alpha = d["SMask"] is PdfObject smask ? doc.GetStreamData((PdfStream)doc.Resolve(smask)!) : null;
        int alphaBits = alpha is null ? 0 : (int)((PdfNumber)((PdfStream)doc.Resolve(d["SMask"])!).Dictionary["BitsPerComponent"]!).ToDouble();
        double[]? key = doc.Resolve(d["Mask"]) is PdfArray k ? [.. k.Select(v => ((PdfNumber)v).ToDouble())] : null;

        long stride = ((long)width * colors * bpc + 7) / 8;
        var sb = new StringBuilder();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var s = new int[colors];
                for (int c = 0; c < colors; c++) s[c] = Sample(data, y * stride, x * colors + c, bpc);
                int r, g, b;
                if (lookup is not null)
                {
                    (r, g, b) = (lookup[3 * s[0]], lookup[3 * s[0] + 1], lookup[3 * s[0] + 2]);
                }
                else
                {
                    int[] e = [.. s.Select(v => To8(v, bpc))];
                    (r, g, b) = colors == 1 ? (e[0], e[0], e[0]) : (e[0], e[1], e[2]);
                }

                int a = 255;
                if (alpha is not null) a = To8(Sample(alpha, y * (((long)width * alphaBits + 7) / 8), x, alphaBits), alphaBits);
                if (key is not null)
                {
                    bool inside = true;
                    for (int c = 0; c < colors; c++) inside &= s[c] >= key[2 * c] && s[c] <= key[2 * c + 1];
                    if (inside) a = 0;
                }

                sb.Append(r.ToString("x2", CultureInfo.InvariantCulture)).Append(g.ToString("x2", CultureInfo.InvariantCulture))
                  .Append(b.ToString("x2", CultureInfo.InvariantCulture)).Append(a.ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static int Sample(byte[] data, long rowStart, int index, int bits)
    {
        if (bits == 16) return data[rowStart + 2 * index] << 8 | data[rowStart + 2 * index + 1];
        if (bits == 8) return data[rowStart + index];
        int bit = index * bits;
        return (data[rowStart + bit / 8] >> (8 - bits - bit % 8)) & ((1 << bits) - 1);
    }

    private static int To8(int v, int bits) => bits == 16 ? v >> 8 : v * 255 / ((1 << bits) - 1);
}
