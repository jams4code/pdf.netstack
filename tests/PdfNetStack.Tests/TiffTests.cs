using System.Buffers.Binary;
using PdfNetStack.Drawing;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>TIFF input against the fixtures of tools/make_tiff_fixtures.py and the pixels libtiff decodes from them.</summary>
[TestClass]
public sealed class TiffTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tiff", name);

    private static TiffPage Page(string name, int page = 0) => TiffDecoder.ReadPage(File.ReadAllBytes(Fixture(name + ".tif")), page);

    [TestMethod]
    [DataRow("g4", 0)]
    [DataRow("g4-strips", 0)]
    [DataRow("g4-lsb", 0)]
    [DataRow("g3", 0)]
    [DataRow("g3-2d", 0)]
    [DataRow("mh", 0)]
    [DataRow("lzw-predictor", 0)]
    [DataRow("lzw-gray", 0)]
    [DataRow("deflate-gray", 0)]
    [DataRow("palette8", 0)]
    [DataRow("palette4", 0)]
    [DataRow("bilevel", 0)]
    [DataRow("bigtiff", 0)]
    [DataRow("tiles", 0)]
    [DataRow("planar", 0)]
    [DataRow("orientation", 0)]

    // JPEG: this decoder and libjpeg round the inverse DCT and color conversion differently.
    [DataRow("jpeg-rgb", 3)]
    [DataRow("jpeg-ycc", 6)]
    [DataRow("jpeg-strips", 3)]
    public void MatchesLibtiff(string name, int tolerance)
    {
        DecodedImage decoded = Page(name).Decode();
        (int width, int height, byte[] rgba) = DecodedImages.Reference(Fixture(name + ".png"));
        Assert.AreEqual(width, decoded.Width);
        Assert.AreEqual(height, decoded.Height);
        DecodedImages.AssertSimilar(rgba, DecodedImages.Rgba(decoded), tolerance, name);
    }

    [TestMethod]
    [DataRow("gray16")]
    [DataRow("rgb16")]
    [DataRow("cmyk")]
    [DataRow("associated")]
    public void MatchesTheSourceSamples(string name)
    {
        DecodedImage decoded = Page(name).Decode();
        byte[] expected = File.ReadAllBytes(Fixture(name + ".raw"));
        if (decoded.Alpha is null)
        {
            CollectionAssert.AreEqual(expected, decoded.Samples, name);
            return;
        }

        // The reference interleaves color and alpha; the decoder keeps alpha apart.
        int colors = decoded.Colors;
        for (int p = 0; p < decoded.Width * decoded.Height; p++)
        {
            for (int c = 0; c < colors; c++) Assert.AreEqual(expected[p * (colors + 1) + c], decoded.Samples[p * colors + c], $"{name} pixel {p} channel {c}");
            Assert.AreEqual(expected[p * (colors + 1) + colors], decoded.Alpha[p], $"{name} alpha {p}");
        }
    }

    [TestMethod]
    public void SixteenBitSamplesStaySixteenBit()
    {
        (PdfStream x, PdfBitmap pixels, _) = DecodedImages.Embedded(PdfImage.Load(Fixture("rgb16.tif")));
        Assert.AreEqual(16, ((PdfNumber)x.Dictionary["BitsPerComponent"]!).ToDouble());
        byte[] raw = File.ReadAllBytes(Fixture("rgb16.raw"));
        byte[] rgb = pixels.Pixels.ToArray();
        for (int i = 0; i < rgb.Length; i++) Assert.AreEqual(raw[2 * i], rgb[i], 1, $"sample {i}");
    }

    [TestMethod]
    [DataRow("g4", -1, false)]
    [DataRow("g3", 0, false)]
    [DataRow("g3-2d", 4, true)]
    [DataRow("mh", 0, true)]
    [DataRow("g4-lsb", -1, false)]
    public void SingleStripFaxIsCopied(string name, int k, bool byteAlign)
    {
        PdfImage image = PdfImage.Load(Fixture(name + ".tif"));
        Assert.AreEqual(PdfImageFormat.Tiff, image.Format);
        (PdfStream x, PdfBitmap pixels, _) = DecodedImages.Embedded(image);
        Assert.AreEqual("CCITTFaxDecode", ((PdfName)x.Dictionary["Filter"]!).Value);
        var parms = (PdfDictionary)x.Dictionary["DecodeParms"]!;
        Assert.AreEqual(k, ((PdfNumber)parms["K"]!).ToDouble());
        Assert.AreEqual(byteAlign, parms["EncodedByteAlign"] is PdfBoolean { Value: true });

        // What a PDF reader draws is what libtiff draws.
        (_, _, byte[] rgba) = DecodedImages.Reference(Fixture(name + ".png"));
        byte[] gray = pixels.Pixels.ToArray();
        int stride = pixels.Format == PdfBitmapFormat.Gray8 ? 1 : 3;
        for (int i = 0; i < pixels.Width * pixels.Height; i++) Assert.AreEqual(rgba[4 * i], gray[i * stride], $"{name} pixel {i}");
    }

    [TestMethod]
    public void FaxInSeveralStripsIsDecoded()
    {
        (PdfStream x, _, _) = DecodedImages.Embedded(PdfImage.Load(Fixture("g4-strips.tif")));
        Assert.AreEqual("FlateDecode", ((PdfName)x.Dictionary["Filter"]!).Value);
        Assert.AreEqual(1, ((PdfNumber)x.Dictionary["BitsPerComponent"]!).ToDouble());
    }

    [TestMethod]
    [DataRow("jpeg-rgb", 0)]
    [DataRow("jpeg-ycc", 1)]
    public void SingleStripJpegIsCopied(string name, int colorTransform)
    {
        (PdfStream x, PdfBitmap pixels, _) = DecodedImages.Embedded(PdfImage.Load(Fixture(name + ".tif")));
        Assert.AreEqual("DCTDecode", ((PdfName)x.Dictionary["Filter"]!).Value);
        Assert.AreEqual(colorTransform, ((PdfNumber)((PdfDictionary)x.Dictionary["DecodeParms"]!)["ColorTransform"]!).ToDouble());

        // The copied data is a complete JPEG: the strip with the shared tables put in front.
        Assert.AreEqual(97, JpegHeader.Read(x.Data.Span).Width);
        (_, _, byte[] rgba) = DecodedImages.Reference(Fixture(name + ".png"));
        byte[] rgb = pixels.Pixels.ToArray();
        for (int i = 0; i < rgb.Length / 3; i++)
        {
            for (int c = 0; c < 3; c++) Assert.AreEqual(rgba[4 * i + c], rgb[3 * i + c], 6, $"{name} pixel {i}");
        }
    }

    [TestMethod]
    public void JpegInSeveralStripsIsDecoded()
    {
        (PdfStream x, _, _) = DecodedImages.Embedded(PdfImage.Load(Fixture("jpeg-strips.tif")));
        Assert.AreEqual("FlateDecode", ((PdfName)x.Dictionary["Filter"]!).Value);
    }

    [TestMethod]
    public void EveryPageOfAMultiPageFileLoads()
    {
        string path = Fixture("multipage.tif");
        Assert.AreEqual(3, PdfImage.GetPageCount(path));
        int[][] sizes = [[40, 30], [20, 10], [61, 45]];
        for (int page = 0; page < 3; page++)
        {
            PdfImage image = PdfImage.Load(path, page);
            Assert.AreEqual(sizes[page][0], image.PixelWidth);
            Assert.AreEqual(sizes[page][1], image.PixelHeight);
            DecodedImage decoded = TiffDecoder.ReadPage(File.ReadAllBytes(path), page).Decode();
            (_, _, byte[] rgba) = DecodedImages.Reference(Fixture($"multipage-{page}.png"));
            DecodedImages.AssertSimilar(rgba, DecodedImages.Rgba(decoded), 0, $"page {page}");
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfImage.Load(path, 3));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfImage.Load(path, -1));
        Assert.AreEqual(1, PdfImage.GetPageCount(Fixture("g4.tif")));
        Assert.AreEqual(1, PdfImage.GetPageCount(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gif", "photo.gif")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfImage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gif", "photo.gif"), 1));
    }

    [TestMethod]
    public void OrientationAndResolutionAreHonored()
    {
        PdfImage image = PdfImage.Load(Fixture("orientation.tif"));
        Assert.AreEqual(24, image.PixelWidth);
        Assert.AreEqual(300, image.HorizontalResolution);
        Assert.AreEqual(150, image.VerticalResolution);

        // Orientation 6 turns the image a quarter: 10 rows at 150 per inch become its width.
        Assert.AreEqual(10 * 72 / 150.0, image.Width, 1e-9);
        Assert.AreEqual(24 * 72 / 300.0, image.Height, 1e-9);
    }

    [TestMethod]
    public void AlphaBecomesASoftMask()
    {
        PdfImage image = PdfImage.Load(Fixture("planar.tif"));
        Assert.IsTrue(image.HasTransparency);
        (PdfStream x, _, PdfDocument doc) = DecodedImages.Embedded(image);
        byte[] alpha = doc.GetStreamData((PdfStream)doc.Resolve(x.Dictionary["SMask"])!);
        (_, _, byte[] rgba) = DecodedImages.Reference(Fixture("planar.png"));
        for (int i = 0; i < alpha.Length; i++) Assert.AreEqual(rgba[4 * i + 3], alpha[i], $"alpha {i}");
    }

    [TestMethod]
    public void UnsupportedAndBadFilesAreRejected()
    {
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load("II*\0\0\0\0\0"u8.ToArray()));
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load("MM\0*\0\0\0\x08\0\0"u8.ToArray()));

        // A page 100,000 pixels square is refused before anything is allocated.
        byte[] huge = Minimal(100_000, 100_000, compression: 1);
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(huge));

        // Uncompressed rows the file does not hold: 10,000 bytes asked of a 134 byte file.
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(Minimal(100, 100, compression: 1)));

        // Old style JPEG and floating point samples are not read.
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(Minimal(4, 4, compression: 6)));
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(Minimal(4, 4, compression: 1, sampleFormat: 3)));

        // A 4 bit palette page with an extra sample, and fax wider than CCITT allows.
        int[] colormap = Enumerable.Range(0, 48).Select(i => i * 1000).ToArray();
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(Minimal(4, 4, compression: 1, extra: [(258, [4, 4]), (262, [3]), (277, [2]), (320, colormap), (338, [2])])));
        Assert.ThrowsExactly<FormatException>(() => PdfImage.Load(Minimal(2_000_000, 1, compression: 4, extra: [(258, [1])])));

        // A directory chain that loops back on itself counts each page once.
        byte[] loop = Minimal(4, 4, compression: 1);
        BinaryPrimitives.WriteInt32LittleEndian(loop.AsSpan(loop.Length - 4), 8);
        Assert.AreEqual(1, PdfImage.GetPageCount(loop));
    }

    [TestMethod]
    public void DamagedFilesDecodeOrFailCleanly()
    {
        foreach (string name in new[] { "g4", "g4-strips", "g3-2d", "lzw-predictor", "lzw-gray", "tiles", "planar", "rgb16", "jpeg-strips", "palette4", "bigtiff", "multipage" })
        {
            byte[] good = File.ReadAllBytes(Fixture(name + ".tif"));
            DecodedImages.Survives(good, data =>
            {
                for (int page = 0; page < PdfImage.GetPageCount(data); page++)
                {
                    TiffPage tiff = TiffDecoder.ReadPage(data, page);
                    tiff.Decode();
                    tiff.PassThrough();
                }
            }, 250, name.Length);
        }
    }

    /// <summary>
    /// A little endian, one strip, 8 bit gray page with no data, its tags all LONG. Extra tags add
    /// to or replace the defaults; values that do not fit in an entry go between the header and
    /// the IFD, so the IFD's next link is always the file's last 4 bytes.
    /// </summary>
    private static byte[] Minimal(int width, int height, int compression, int sampleFormat = 1, params (int Tag, int[] Values)[] extra)
    {
        var tags = new SortedDictionary<int, int[]>
        {
            [256] = [width], [257] = [height], [258] = [8], [259] = [compression], [262] = [1], [273] = [8],
            [277] = [1], [278] = [height], [279] = [0], [339] = [sampleFormat],
        };
        foreach ((int tag, int[] values) in extra) tags[tag] = values;

        int tail = tags.Values.Where(v => v.Length > 1).Sum(v => 4 * v.Length), ifd = 8 + tail;
        var file = new byte[ifd + 2 + 12 * tags.Count + 4];
        "II*\0"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), ifd);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(ifd), (short)tags.Count);
        int at = ifd + 2, data = 8;
        foreach (KeyValuePair<int, int[]> tag in tags)
        {
            BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(at), (short)tag.Key);
            BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(at + 2), 4);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at + 4), tag.Value.Length);
            if (tag.Value.Length == 1)
            {
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at + 8), tag.Value[0]);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at + 8), data);
                foreach (int v in tag.Value)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(data), v);
                    data += 4;
                }
            }

            at += 12;
        }

        return file;
    }
}
