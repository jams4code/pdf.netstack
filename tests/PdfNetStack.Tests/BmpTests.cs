using System.Buffers.Binary;
using PdfNetStack.Drawing;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>
/// BMP input against the fixtures of tools/make_bmp_fixtures.py and the pixels Pillow decodes from
/// them. The run length references come from the encoder's own indexes (see the script).
/// </summary>
[TestClass]
public sealed class BmpTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bmp", name);

    [TestMethod]
    [DataRow("rgb24", 0, false)]
    [DataRow("pal8", 0, false)]
    [DataRow("mono1", 0, false)]
    [DataRow("pal4", 0, false)]
    [DataRow("pal2", 0, false)]
    [DataRow("rle8", 0, true)]
    [DataRow("rle4", 0, true)]
    [DataRow("rgba32", 0, true)]
    [DataRow("topdown", 0, false)]
    [DataRow("core24", 0, false)]

    // 5 and 6 bit channels: Pillow scales by truncating, this decoder rounds to nearest.
    [DataRow("rgb565", 1, false)]
    [DataRow("rgb555", 1, false)]
    public void MatchesPillow(string name, int tolerance, bool transparent)
    {
        DecodedImage decoded = BmpDecoder.Read(File.ReadAllBytes(Fixture(name + ".bmp")));
        (int width, int height, byte[] rgba) = DecodedImages.Reference(Fixture(name + ".png"));
        Assert.AreEqual(width, decoded.Width);
        Assert.AreEqual(height, decoded.Height);
        DecodedImages.AssertSimilar(rgba, DecodedImages.Rgba(decoded), tolerance, name);

        PdfImage image = PdfImage.Load(Fixture(name + ".bmp"));
        Assert.AreEqual(PdfImageFormat.Bmp, image.Format);
        Assert.AreEqual(transparent, image.HasTransparency, name);
    }

    [TestMethod]
    public void PaletteImagesKeepTheirDepthAndPalette()
    {
        DecodedImage pal4 = BmpDecoder.Read(File.ReadAllBytes(Fixture("pal4.bmp")));
        Assert.AreEqual(4, pal4.BitsPerComponent);
        Assert.HasCount(48, pal4.Palette!);

        (PdfStream x, PdfBitmap pixels, _) = DecodedImages.Embedded(PdfImage.Load(Fixture("pal4.bmp")));
        Assert.AreEqual("Indexed", ((PdfName)((PdfArray)x.Dictionary["ColorSpace"]!)[0]).Value);
        Assert.AreEqual(4, ((PdfNumber)x.Dictionary["BitsPerComponent"]!).ToDouble());
        (_, _, byte[] rgba) = DecodedImages.Reference(Fixture("pal4.png"));
        byte[] rgb = pixels.Pixels.ToArray();
        for (int i = 0; i < pal4.Width * pal4.Height; i++)
        {
            for (int c = 0; c < 3; c++) Assert.AreEqual(rgba[4 * i + c], rgb[3 * i + c], $"pixel {i}");
        }
    }

    [TestMethod]
    public void AlphaBecomesASoftMaskAndResolutionIsKept()
    {
        PdfImage image = PdfImage.Load(Fixture("rgba32.bmp"));
        Assert.AreEqual(72, image.HorizontalResolution, 0.01);
        (PdfStream x, _, PdfDocument doc) = DecodedImages.Embedded(image);
        var mask = (PdfStream)doc.Resolve(x.Dictionary["SMask"])!;
        byte[] alpha = doc.GetStreamData(mask);
        (_, _, byte[] rgba) = DecodedImages.Reference(Fixture("rgba32.png"));
        for (int i = 0; i < alpha.Length; i++) Assert.AreEqual(rgba[4 * i + 3], alpha[i], $"alpha {i}");
    }

    [TestMethod]
    public void RunLengthGapsAreTransparent()
    {
        DecodedImage rle = BmpDecoder.Read(File.ReadAllBytes(Fixture("rle8.bmp")));
        int transparent = rle.Alpha!.Count(a => a == 0);
        Assert.AreEqual(70, transparent);
    }

    [TestMethod]
    public void RunLengthPastTheRightEdgeIsDropped()
    {
        // 4 by 2 pixels, RLE8, two palette entries: a jump far past the right edge and a run of
        // 255 that has nowhere to go, then two pixels at the start of the top row.
        byte[] bmp = Header(4, 2, 8, 1, 8 + 12);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54 + 8);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(46), 2);
        bmp = [.. bmp, 0, 0, 0, 0, 255, 255, 255, 0, 0, 2, 200, 0, 255, 1, 0, 0, 2, 1, 0, 1];
        DecodedImage image = BmpDecoder.Read(bmp);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 0, 0, 0, 0, 0, 0 }, image.Alpha);
        CollectionAssert.AreEqual(new byte[] { 1, 1 }, image.Samples.Take(2).ToArray());
    }

    [TestMethod]
    public void A32BitImageWithZeroAlphaIsOpaque()
    {
        // 2 by 1 pixels, BI_RGB, every fourth byte zero: an unused byte, not an invisible image.
        byte[] bmp = Header(2, 1, 32, 0, 8);
        bmp = [.. bmp, 10, 20, 30, 0, 40, 50, 60, 0];
        DecodedImage image = BmpDecoder.Read(bmp);
        Assert.IsNull(image.Alpha);
        CollectionAssert.AreEqual(new byte[] { 30, 20, 10, 60, 50, 40 }, image.Samples);
    }

    [TestMethod]
    [DataRow("RGB ", "ICCBased")]
    [DataRow("GRAY", "DeviceRGB")]
    public void AnEmbeddedProfileIsKeptWhenItFitsTheColors(string space, string expected)
    {
        // One 24 bit pixel and a version 5 header pointing at a bare 132 byte profile after it.
        var bmp = new byte[14 + 124 + 4 + 132];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 14 + 124);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 124);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14 + 56), 0x4D424544);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14 + 112), 124 + 4);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14 + 116), 132);
        for (int i = 0; i < 4; i++) bmp[14 + 124 + 4 + 16 + i] = (byte)space[i];

        (PdfStream x, _, PdfDocument doc) = DecodedImages.Embedded(PdfImage.Load(bmp));
        PdfObject colorSpace = doc.Resolve(x.Dictionary["ColorSpace"])!;
        Assert.AreEqual(expected, colorSpace is PdfArray array ? ((PdfName)array[0]).Value : ((PdfName)colorSpace).Value);
    }

    [TestMethod]
    public void AnEmbeddedPngLoadsAsThePng()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "rgba8.png"));
        byte[] bmp = [.. Header(7, 5, 0, 5, png.Length), .. png];
        PdfImage image = PdfImage.Load(bmp);
        Assert.AreEqual(PdfImageFormat.Bmp, image.Format);
        Assert.AreEqual(7, image.PixelWidth);
        Assert.IsTrue(image.HasTransparency);
    }

    [TestMethod]
    public void BadBitmapsAreRejected()
    {
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load("BM\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray()));

        // 100,000 by 100,000 pixels is refused before anything is allocated.
        Assert.ThrowsExactly<FormatException>(() => BmpDecoder.Read(Header(100_000, 100_000, 24, 0, 0)));

        // Pixel rows that are not there.
        Assert.ThrowsExactly<FormatException>(() => BmpDecoder.Read(Header(50, 50, 24, 0, 0)));

        // Compression the format does not define.
        Assert.ThrowsExactly<FormatException>(() => BmpDecoder.Read(Header(2, 2, 24, 9, 0)));
    }

    [TestMethod]
    public void DamagedBitmapsDecodeOrFailCleanly()
    {
        foreach (string name in new[] { "rgb24", "pal4", "rle8", "rle4", "rgba32", "rgb565", "core24" })
        {
            DecodedImages.Survives(File.ReadAllBytes(Fixture(name + ".bmp")), data => PdfImage.Load(data), 300, name.Length);
        }
    }

    /// <summary>A file header and a 40 byte info header, pixel data to follow directly.</summary>
    private static byte[] Header(int width, int height, int bits, int compression, int imageSize)
    {
        var h = new byte[54];
        h[0] = (byte)'B';
        h[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(2), 54 + imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(28), (short)bits);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(30), compression);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(34), imageSize);
        return h;
    }
}
