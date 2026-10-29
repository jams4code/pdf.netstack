using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class ImageExtractionTests
{
    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToArray());

    /// <summary>A one page document drawing the given content with the given resources.</summary>
    private static PdfDocument WithContent(string content, PdfDictionary? resources = null)
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData(Encoding.GetEncoding("ISO-8859-1").GetBytes(content))));
        page.Dictionary.Set("Resources", resources ?? new PdfDictionary());
        return Reopen(doc);
    }

    private static PdfStream Image(int width, int height, int bpc, PdfObject? colorSpace, byte[] samples, params (string Key, PdfObject Value)[] extra)
    {
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = PdfNumber.FromInteger(width),
            ["Height"] = PdfNumber.FromInteger(height),
            ["BitsPerComponent"] = PdfNumber.FromInteger(bpc),
        };
        if (colorSpace is not null) dict.Set("ColorSpace", colorSpace);
        foreach ((string key, PdfObject value) in extra) dict.Set(key, value);

        // The data is stored as given, under whatever filter the caller names.
        return new PdfStream(dict, samples);
    }

    private static PdfDictionary XObjects(PdfDocument doc, params (string Name, PdfStream Stream)[] items)
    {
        var xobjects = new PdfDictionary();
        foreach ((string name, PdfStream stream) in items) xobjects.Set(name, doc.AddObject(stream));
        return new PdfDictionary { ["XObject"] = xobjects };
    }

    private static byte[] Pixels(PdfBitmap bitmap) => bitmap.Pixels.ToArray();

    [TestMethod]
    public void FindsImagesWhereTheyAreDrawn()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        var photo = PdfImage.Load(Fixture("Jpeg", "ycc420-baseline.jpg"));
        var icon = PdfImage.Load(Fixture("Images", "rgb8.png"));
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawImage(photo, 100, 50, 122, 90);
            g.DrawImage(icon, 10, 10, 20, 20);
            g.DrawImage(icon, 300, 400, 40, 40);
        }

        IReadOnlyList<PdfPageImage> images = Reopen(doc).Pages[0].GetImages();
        Assert.HasCount(3, images);
        PdfPageImage first = images[0];
        Assert.AreEqual("DCTDecode", first.Encoding);
        Assert.IsFalse(first.IsInline);
        Assert.AreEqual(61, first.Width);
        Assert.AreEqual(45, first.Height);
        Assert.AreEqual(8, first.BitsPerComponent);

        // Graphics measure from the top left; bounds are in PDF space, from the bottom left.
        Assert.AreEqual(100, first.Bounds.Left, 1e-6);
        Assert.AreEqual(222, first.Bounds.Right, 1e-6);
        Assert.AreEqual(842 - 50, first.Bounds.Top, 1e-6);
        Assert.AreEqual(842 - 140, first.Bounds.Bottom, 1e-6);
        Assert.AreEqual(61 / (122 / 72.0), first.HorizontalResolution, 1e-6);

        // The same XObject drawn twice is found twice, each where it lands.
        Assert.AreSame(images[1].Stream, images[2].Stream);
        Assert.AreEqual(300, images[2].Bounds.Left, 1e-6);
        Assert.AreEqual(40, images[2].Bounds.Width, 1e-6);
        // A JPEG has no general filter around it: the encoded data is the stream's data, a JPEG file.
        byte[] jpeg = first.GetEncodedData();
        CollectionAssert.AreEqual(first.Stream!.Data.ToArray(), jpeg);
        Assert.AreEqual(61, JpegDecoder.Decode(jpeg, null, 1 << 20).Width);
    }

    /// <summary>The samples a PNG holds, as 8 bit gray or RGB: palettes expanded, depths scaled, alpha dropped.</summary>
    private static (int Channels, byte[] Pixels) Expected(string file)
    {
        PngImage png = PngImage.Read(File.ReadAllBytes(Fixture("Images", file)));
        byte[] raw = png.Decode();
        int channels = png.ColorType is 0 or 4 ? 1 : 3;
        int stored = png.ColorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
        int max = (1 << png.BitDepth) - 1;
        long rowBytes = png.RowBytes(png.Width);
        int Sample(int y, long index) => png.BitDepth switch
        {
            16 => (raw[(y * rowBytes) + (2 * index)] << 8) | raw[(y * rowBytes) + (2 * index) + 1],
            8 => raw[(y * rowBytes) + index],
            _ => (raw[(y * rowBytes) + (index * png.BitDepth / 8)] >> (8 - png.BitDepth - (int)(index * png.BitDepth % 8))) & max,
        };

        var pixels = new byte[png.Width * png.Height * channels];
        for (int y = 0; y < png.Height; y++)
        {
            for (int x = 0; x < png.Width; x++)
            {
                int o = ((y * png.Width) + x) * channels;
                if (png.ColorType == 3)
                {
                    // One index per pixel into the palette's RGB entries.
                    png.Palette.AsSpan(3 * Sample(y, x), 3).CopyTo(pixels.AsSpan(o));
                    continue;
                }

                for (int c = 0; c < channels; c++) pixels[o + c] = (byte)Math.Round(Sample(y, ((long)x * stored) + c) * 255.0 / max);
            }
        }

        return (channels, pixels);
    }

    [TestMethod]
    [DataRow("gray1.png")]
    [DataRow("gray2.png")]
    [DataRow("gray4.png")]
    [DataRow("gray8.png")]
    [DataRow("gray16.png")]
    [DataRow("gray-alpha8.png")]
    [DataRow("rgb8.png")]
    [DataRow("rgb16.png")]
    [DataRow("rgba8.png")]
    [DataRow("rgb8-interlaced-300dpi.png")]
    [DataRow("palette1.png")]
    [DataRow("palette2.png")]
    [DataRow("palette4.png")]
    [DataRow("palette8.png")]
    public void DecodesEmbeddedPngs(string file)
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawImage(PdfImage.Load(Fixture("Images", file)), 0, 0);
        PdfPageImage image = Reopen(doc).Pages[0].GetImages().Single();
        Assert.IsNull(image.Encoding);
        PdfBitmap bitmap = image.Decode()!;

        (int channels, byte[] expected) = Expected(file);
        Assert.AreEqual(channels == 1 ? PdfBitmapFormat.Gray8 : PdfBitmapFormat.Rgb24, bitmap.Format);
        byte[] actual = Pixels(bitmap);
        Assert.HasCount(expected.Length, actual);

        // 16 bit samples round to 8 bits, so they may land one step either side.
        for (int i = 0; i < expected.Length; i++) Assert.IsLessThanOrEqualTo(1, Math.Abs(expected[i] - actual[i]), $"sample {i}");
    }

    [TestMethod]
    public void DecodesEmbeddedJpegs()
    {
        foreach (string name in new[] { "gray-baseline", "ycc444-progressive", "ycc420-baseline", "cmyk-baseline" })
        {
            byte[] jpeg = File.ReadAllBytes(Fixture("Jpeg", name + ".jpg"));
            PdfDocument doc = PdfDocument.Create();
            using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawImage(PdfImage.Load(jpeg), 0, 0);
            PdfBitmap bitmap = Reopen(doc).Pages[0].GetImages().Single().Decode()!;
            Raster raw = JpegDecoder.Decode(jpeg, null, 64 << 20);
            byte[] actual = Pixels(bitmap);
            if (raw.Channels < 4)
            {
                CollectionAssert.AreEqual(raw.Samples, actual, name);
                continue;
            }

            // Adobe CMYK is stored inverted; the Decode array the writer adds turns it back.
            Assert.AreEqual(PdfBitmapFormat.Rgb24, bitmap.Format);
            for (int i = 0; i < raw.Width * raw.Height; i++)
            {
                double k = raw.Samples[(4 * i) + 3] / 255.0;
                for (int c = 0; c < 3; c++)
                {
                    double ink = raw.Samples[(4 * i) + c] / 255.0;
                    Assert.AreEqual(Math.Round(ink * k * 255), actual[(3 * i) + c], 1, $"{name} pixel {i}");
                }
            }
        }
    }

    [TestMethod]
    public void ReadsInlineImagesFormsAndColorSpaces()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);

        // A form, scaled by its matrix, drawing an Indexed image; a stencil mask; Lab; a spot color.
        var palette = PdfArray.Of(new PdfName("Indexed"), new PdfName("DeviceRGB"), PdfNumber.FromInteger(1), new PdfString([255, 0, 0, 0, 0, 255]));
        PdfStream indexed = Image(4, 1, 1, palette, [0b1010_0000]);
        var formResources = XObjects(doc, ("I", indexed));
        PdfStream form = PdfStream.FromData("q 8 0 0 2 0 0 cm /I Do Q"u8, new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Form"),
            ["BBox"] = PdfArray.Of(0, 0, 10, 10),
            ["Matrix"] = PdfArray.Of(2, 0, 0, 2, 100, 100),
            ["Resources"] = formResources,
        });
        PdfStream mask = Image(8, 1, 1, null, [0b1100_0000], ("ImageMask", PdfBoolean.True), ("Decode", PdfArray.Of(1, 0)));
        mask.Dictionary.Remove("BitsPerComponent");
        var lab = PdfArray.Of(new PdfName("Lab"), new PdfDictionary { ["WhitePoint"] = PdfArray.Of(0.9505, 1, 1.089) });
        PdfStream labImage = Image(2, 1, 8, lab, [255, 128, 128, 0, 128, 128]);
        var tint = new PdfDictionary
        {
            ["FunctionType"] = PdfNumber.FromInteger(2),
            ["Domain"] = PdfArray.Of(0, 1),
            ["C0"] = PdfArray.Of(1, 1, 1),
            ["C1"] = PdfArray.Of(0, 0.5, 1),
            ["N"] = PdfNumber.FromInteger(1),
        };
        var spot = PdfArray.Of(new PdfName("Separation"), new PdfName("Blue"), new PdfName("DeviceRGB"), tint);
        PdfStream spotImage = Image(2, 1, 8, spot, [0, 255]);
        PdfDictionary resources = XObjects(doc, ("F", form), ("M", mask), ("L", labImage), ("S", spotImage));
        resources.Set("ColorSpace", new PdfDictionary { ["Pal"] = palette });

        string content = "q 1 0 0 1 10 20 cm /F Do Q /M Do /L Do /S Do\n"
            + "q 30 0 0 10 400 500 cm BI /W 2 /H 1 /CS /RGB /BPC 8 /F /AHx ID ff000000ff00> EI Q\n"
            + "BI /W 3 /H 1 /CS /Pal /BPC 8 ID \u0000\u0001\u0000 EI\n"
            + "BI /IM true /W 4 /H 1 ID   EI";
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData(Encoding.GetEncoding("ISO-8859-1").GetBytes(content))));
        page.Dictionary.Set("Resources", resources);

        IReadOnlyList<PdfPageImage> images = Reopen(doc).Pages[0].GetImages();
        Assert.HasCount(7, images);

        // Inside the form: its matrix, the cm before it and the cm inside all compose.
        PdfPageImage inForm = images[0];
        Assert.AreEqual(110, inForm.Bounds.Left, 1e-9);
        Assert.AreEqual(120, inForm.Bounds.Bottom, 1e-9);
        Assert.AreEqual(16, inForm.Bounds.Width, 1e-9);
        Assert.AreEqual(4, inForm.Bounds.Height, 1e-9);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255, 0, 0, 0, 0, 255, 255, 0, 0 }, Pixels(inForm.Decode()!));

        // Decode [1 0] makes 1 the painting value: black there, white elsewhere.
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255 }, Pixels(images[1].Decode()!));

        // L* 100 is white and L* 0 black.
        byte[] labPixels = Pixels(images[2].Decode()!);
        Assert.IsTrue(labPixels.Take(3).All(v => v >= 254) && labPixels.Skip(3).All(v => v <= 1), string.Join(",", labPixels));

        // No tint is white; full tint is the C1 color through the alternate space.
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 0, 128, 255 }, Pixels(images[3].Decode()!));

        // Inline images: hex filtered, a named color space from the resources, a stencil.
        Assert.IsTrue(images[4].IsInline);
        Assert.AreEqual(30, images[4].Bounds.Width, 1e-9);
        CollectionAssert.AreEqual(new byte[] { 255, 0, 0, 0, 255, 0 }, Pixels(images[4].Decode()!));
        CollectionAssert.AreEqual(new byte[] { 255, 0, 0, 0, 0, 255, 255, 0, 0 }, Pixels(images[5].Decode()!));
        CollectionAssert.AreEqual(new byte[] { 255, 0, 255, 0 }, Pixels(images[6].Decode()!));
    }

    [TestMethod]
    public void WritesPngs()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawImage(PdfImage.Load(Fixture("Jpeg", "photo-q95.jpg")), 0, 0);
        PdfBitmap bitmap = Reopen(doc).Pages[0].GetImages().Single().Decode()!;
        byte[] png = bitmap.ToPng();

        PngImage read = PngImage.Read(png);
        Assert.AreEqual(320, read.Width);
        Assert.AreEqual(2, read.ColorType);
        CollectionAssert.AreEqual(Pixels(bitmap), read.Decode());
        using var stream = new MemoryStream();
        bitmap.SavePng(stream);
        CollectionAssert.AreEqual(png, stream.ToArray());
    }

    [TestMethod]
    public void LeavesCodecsItDoesNotDecode()
    {
        PdfStream jbig2 = Image(8, 8, 1, new PdfName("DeviceGray"), [1, 2, 3], ("Filter", new PdfName("JBIG2Decode")));
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Dictionary.Set("Resources", XObjects(doc, ("J", jbig2)));
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData("/J Do"u8)));
        PdfPageImage image = Reopen(doc).Pages[0].GetImages().Single();
        Assert.AreEqual("JBIG2Decode", image.Encoding);
        Assert.IsNull(image.Decode());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, image.GetEncodedData());
    }

    [TestMethod]
    public void SurvivesHostileContent()
    {
        // A form that draws itself, deep q nesting, junk operators and unbalanced Q.
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        var formDict = new PdfDictionary { ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["BBox"] = PdfArray.Of(0, 0, 1, 1) };
        PdfReference formRef = doc.AddObject(PdfStream.FromData("/Self Do /Self Do"u8, formDict));
        formDict.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["Self"] = formRef } });
        page.Dictionary.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["Self"] = formRef } });
        string junk = string.Concat(Enumerable.Repeat("q ", 10_000)) + "Q Q Q ] } ) 1 2 cm /Nope Do (unterminated";
        page.Dictionary.Set("Contents", PdfArray.Of(doc.AddObject(PdfStream.FromData("/Self Do"u8)), doc.AddObject(PdfStream.FromData(Encoding.ASCII.GetBytes(junk)))));
        Assert.IsEmpty(Reopen(doc).Pages[0].GetImages());

        // Random content bytes: read as far as they go, never an exception.
        var random = new Random(11);
        for (int round = 0; round < 200; round++)
        {
            var bytes = new byte[random.Next(1, 400)];
            random.NextBytes(bytes);
            _ = WithContent(Encoding.GetEncoding("ISO-8859-1").GetString(bytes)).Pages[0].GetImages();
        }

        // Damaged image data fails as a PDF error; sizes past the cap are refused before allocating.
        PdfDocument bad = WithContent("/D Do /H Do", null);
        PdfStream damaged = Image(4, 4, 8, new PdfName("DeviceGray"), [1, 2, 3], ("Filter", new PdfName("DCTDecode")));
        PdfStream huge = Image(1 << 20, 1 << 20, 8, new PdfName("DeviceGray"), [0]);
        PdfDocument doc2 = PdfDocument.Create();
        PdfPage p2 = doc2.Pages.Add(PdfRectangle.A4);
        p2.Dictionary.Set("Resources", XObjects(doc2, ("D", damaged), ("H", huge)));
        p2.Dictionary.Set("Contents", doc2.AddObject(PdfStream.FromData("/D Do /H Do"u8)));
        IReadOnlyList<PdfPageImage> images = Reopen(doc2).Pages[0].GetImages();
        Assert.ThrowsExactly<PdfFormatException>(() => images[0].Decode());
        Assert.ThrowsExactly<PdfFormatException>(() => images[1].Decode());
        Assert.IsEmpty(bad.Pages[0].GetImages());
    }
}
