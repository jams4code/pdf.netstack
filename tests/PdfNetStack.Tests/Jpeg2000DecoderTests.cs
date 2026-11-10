using System.Diagnostics;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class Jpeg2000DecoderTests
{
    private const long Limit = 64 << 20;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpx", name);

    /// <summary>
    /// The reference samples per channel (tools/make_jpx_fixtures.py): OpenJPEG's decode as one
    /// PNG, or one PNG per channel (name.c0.png, ...) when channels differ in size or depth.
    /// </summary>
    private static int[][] Reference(string name, int offset = 0)
    {
        string whole = Fixture(Path.Combine("Reference", name + ".png"));
        if (File.Exists(whole)) return Planes(whole);
        var planes = new List<int[]>();
        for (int c = 0; File.Exists(Fixture(Path.Combine("Reference", $"{name}.c{c}.png"))); c++)
        {
            planes.Add([.. Planes(Fixture(Path.Combine("Reference", $"{name}.c{c}.png")))[0].Select(v => v - offset)]);
        }

        Assert.IsNotEmpty(planes, name);
        return [.. planes];
    }

    private static int[][] Planes(string path)
    {
        PngImage png = PngImage.Read(File.ReadAllBytes(path));
        int channels = png.ColorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new AssertFailedException($"color type {png.ColorType}") };
        byte[] raw = png.Decode();
        int bytes = png.BitDepth / 8, pixels = png.Width * png.Height;
        var planes = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            planes[c] = new int[pixels];
            for (int i = 0; i < pixels; i++)
            {
                int at = ((i * channels) + c) * bytes;
                planes[c][i] = bytes == 1 ? raw[at] : (raw[at] << 8) | raw[at + 1];
            }
        }

        return planes;
    }

    private static Jpeg2000Image Decode(string file, bool applyPalette = true) =>
        Jpeg2000Decoder.Decode(File.ReadAllBytes(Fixture(file)), Limit, applyPalette: applyPalette);

    /// <summary>The largest difference from the reference over every channel, with the sizes checked.</summary>
    private static int Worst(string file, string? reference = null, int offset = 0, bool applyPalette = true)
    {
        Jpeg2000Image image = Decode(file, applyPalette);
        int[][] expected = Reference(reference ?? Path.GetFileNameWithoutExtension(file), offset);
        Assert.HasCount(expected.Length, image.Channels, file);
        int worst = 0, differ = 0;
        for (int c = 0; c < expected.Length; c++)
        {
            int[] actual = image.Channels[c].Samples;
            Assert.HasCount(expected[c].Length, actual, file);
            for (int i = 0; i < actual.Length; i++)
            {
                int d = Math.Abs(actual[i] - expected[c][i]);
                worst = Math.Max(worst, d);
                if (d > 0) differ++;
            }
        }

        // Rounding differences are rare, never systematic.
        Assert.IsLessThan(Math.Max(1, expected.Sum(p => p.Length) / 20), differ, file);
        return worst;
    }

    [TestMethod]
    [DataRow("gray-53.jp2")]
    [DataRow("gray-plt.j2k")]
    [DataRow("gray-1res.jp2")]
    [DataRow("gray-3res.jp2")]
    [DataRow("gray16-53.jp2")]
    [DataRow("rgb-53.jp2")]
    [DataRow("rgba-53.jp2")]
    [DataRow("rgb-offset.jp2")]
    [DataRow("rgb-lrcp.jp2")]
    [DataRow("rgb-rlcp.jp2")]
    [DataRow("rgb-rpcl.jp2")]
    [DataRow("rgb-pcrl.jp2")]
    [DataRow("rgb-cprl.jp2")]
    [DataRow("style-bypass.j2k")]
    [DataRow("style-reset.j2k")]
    [DataRow("style-termall.j2k")]
    [DataRow("style-causal.j2k")]
    [DataRow("style-predictable.j2k")]
    [DataRow("style-segmark.j2k")]
    [DataRow("style-allstyles.j2k")]
    [DataRow("rgb-sop-eph.j2k")]
    [DataRow("rgb-ppm.j2k")]
    [DataRow("rgb-ppt.j2k")]
    [DataRow("rgb-poc.j2k")]
    [DataRow("gray-roi.j2k")]
    [DataRow("palette.jp2")]
    public void LosslessMatchesOpenJpegExactly(string file) => Assert.AreEqual(0, Worst(file), file);

    [TestMethod]
    [DataRow("gray-97.j2k")]
    [DataRow("rgb-97.jp2")]
    [DataRow("rgb-97-tiles.jp2")]
    public void LossyMatchesOpenJpegWithinOne(string file) => Assert.IsLessThanOrEqualTo(1, Worst(file), file);

    /// <summary>
    /// Fixtures whose lossless source is the reference: subsampled chroma (Pillow cannot hand over
    /// planes of different sizes), odd depths, signed samples and a minimal ROI shift.
    /// </summary>
    [TestMethod]
    [DataRow("sub-rpcl.j2k", 0)]
    [DataRow("sub-pcrl.j2k", 0)]
    [DataRow("sub-cprl.j2k", 0)]
    [DataRow("sub-lrcp.j2k", 0)]
    [DataRow("gray-1bit.j2k", 0)]
    [DataRow("gray-12bit.j2k", 0)]
    [DataRow("gray-signed.j2k", 128)]
    [DataRow("gray-roi-min.j2k", 0)]
    public void LosslessMatchesTheSource(string file, int offset) => Assert.AreEqual(0, Worst(file, offset: offset), file);

    [TestMethod]
    public void KeepsPaletteIndexesWhenAsked()
    {
        Assert.AreEqual(0, Worst("palette.jp2", "palette-index", applyPalette: false));
        Jpeg2000Image colors = Decode("palette.jp2");
        Assert.HasCount(3, colors.Channels);
        Assert.AreEqual(16, colors.ColorSpace);
    }

    [TestMethod]
    public void DescribesChannels()
    {
        Jpeg2000Image rgba = Decode("rgba-53.jp2");
        Assert.HasCount(4, rgba.Channels);
        Assert.IsTrue(rgba.Channels[3].Opacity, "the cdef box marks the alpha channel");
        Assert.IsFalse(rgba.Channels[0].Opacity);

        Jpeg2000Image sub = Decode("sub-rpcl.j2k");
        Assert.AreEqual(61, sub.Width);
        Assert.AreEqual(45, sub.Height);
        Assert.AreEqual(2, sub.Channels[1].Dx);
        Assert.AreEqual(30, sub.Channels[1].Width);

        Assert.AreEqual(16, Decode("gray16-53.jp2").Channels[0].Depth);
        Assert.IsNull(Decode("gray-97.j2k").ColorSpace, "a raw codestream has no color specification");
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void RastersScaleToEightBits()
    {
        // 16 bit samples scale to 8 bits, rounded.
        Jpeg2000Image deep = Decode("gray16-53.jp2");
        Raster raster = deep.ToRaster(1, scale: true);
        for (int i = 0; i < raster.Samples.Length; i++) Assert.AreEqual((int)Math.Round(deep.Channels[0].Samples[i] * 255.0 / 65535), raster.Samples[i]);

        // One bit samples become black and white.
        Assert.IsTrue(Decode("gray-1bit.j2k").ToRaster(1, scale: true).Samples.All(v => v is 0 or 255));

        // Subsampled chroma repeats each sample over the positions up to the next one on the grid.
        Jpeg2000Image sub = Decode("sub-rpcl.j2k");
        Raster full = sub.ToRaster(3, scale: true);
        Assert.HasCount(61 * 45 * 3, full.Samples);
        int[] cb = sub.Channels[1].Samples;
        for (int y = 0; y < 45; y++)
        {
            for (int x = 0; x < 61; x++)
            {
                int cx = Math.Max(0, ((3 + x) / 2) - 2), cy = Math.Max(0, ((1 + y) / 2) - 1);
                Assert.AreEqual(cb[(cy * 30) + cx], full.Samples[(((y * 61) + x) * 3) + 1]);
            }
        }
    }

    /// <summary>Decodes or fails with a format error within a time bound; anything else fails the test.</summary>
    private static bool Survives(byte[] data, long limit = Limit)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            Jpeg2000Decoder.Decode(data, limit);
            return true;
        }
        catch (Jpeg2000FormatException)
        {
            return false;
        }
        finally
        {
            Assert.IsLessThan(5000, watch.ElapsedMilliseconds, "hostile data must not hang the decoder");
        }
    }

    [TestMethod]
    [DataRow("gray-97.j2k")]
    [DataRow("rgb-53.jp2")]
    [DataRow("rgb-97-tiles.jp2")]
    [DataRow("style-allstyles.j2k")]
    [DataRow("rgb-ppm.j2k")]
    [DataRow("rgb-ppt.j2k")]
    [DataRow("rgb-poc.j2k")]
    [DataRow("sub-pcrl.j2k")]
    [DataRow("palette.jp2")]
    public void SurvivesTruncationAndDamage(string file)
    {
        byte[] data = File.ReadAllBytes(Fixture(file));
        for (int length = 0; length < data.Length; length += Math.Max(1, data.Length / 150)) Survives(data.AsSpan(0, length).ToArray());

        // Cut inside the tile data, the packets before the cut still decode.
        Assert.IsTrue(Survives(data.AsSpan(0, data.Length - 40).ToArray()), "a truncated tile keeps what it has");

        var random = new Random(file.Length);
        for (int round = 0; round < 250; round++)
        {
            byte[] copy = (byte[])data.Clone();
            for (int k = 0; k < 1 + (round % 4); k++) copy[random.Next(2, copy.Length)] = (byte)random.Next(256);
            Survives(copy);
        }
    }

    /// <summary>gray-97.j2k with a big endian field of the main header replaced.</summary>
    private static byte[] Patched(params (int Offset, int Bytes, long Value)[] fields)
    {
        byte[] data = File.ReadAllBytes(Fixture("gray-97.j2k"));
        foreach ((int offset, int bytes, long value) in fields)
        {
            for (int i = 0; i < bytes; i++) data[offset + i] = (byte)(value >> (8 * (bytes - 1 - i)));
        }

        return data;
    }

    [TestMethod]
    public void RejectsHugeDeclaredSizesAndCounts()
    {
        // SIZ: Xsiz at 8, Ysiz at 12, XTsiz at 24, YTsiz at 28, Csiz at 40, Ssiz at 42.
        Assert.IsFalse(Survives(Patched((8, 4, 0x7FFFFFFF), (12, 4, 0x7FFFFFFF))), "an image over the pixel ceiling");
        Assert.IsFalse(Survives(Patched((8, 4, 20000), (12, 4, 20000), (24, 4, 20000), (28, 4, 20000))), "an image over the byte limit");
        Assert.IsFalse(Survives(Patched((8, 4, 4000), (12, 4, 4000), (24, 4, 1), (28, 4, 1))), "16 million tiles");
        Assert.IsFalse(Survives(Patched((40, 2, 16384))), "16384 components");
        Assert.IsFalse(Survives(Patched((42, 1, 31))), "32 bit samples");
        Assert.IsFalse(Survives(Patched((43, 1, 0))), "zero subsampling");
        Assert.IsFalse(Survives(Patched((24, 4, 0))), "zero tile width");

        // COD: layers at 51, levels at 54, code-block exponents at 55 and 56, style at 57.
        Assert.IsTrue(Survives(Patched((51, 2, 65535))), "65535 layers with one layer of data");
        Assert.IsFalse(Survives(Patched((54, 1, 33))), "33 decomposition levels");
        Assert.IsFalse(Survives(Patched((54, 1, 32))), "32 levels the quantization does not cover");
        Assert.IsFalse(Survives(Patched((55, 1, 8), (56, 1, 8))), "code-blocks over 4096 samples");
        Assert.IsFalse(Survives(Patched((57, 1, 0x40))), "high throughput blocks");
        Assert.IsFalse(Survives(Patched((50, 1, 9))), "an unknown progression order");

        // A big image of mostly missing data fails on the byte limit or decodes in bounded time.
        Survives(Patched((8, 4, 3000), (12, 4, 2000), (24, 4, 3000), (28, 4, 2000), (51, 2, 65535)), 64L << 20);

        // Not JPEG 2000 at all.
        Assert.IsFalse(Survives([]));
        Assert.IsFalse(Survives([0xFF, 0x4F, 0xFF, 0x51]));
        Assert.IsFalse(Survives([0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 13, 10, 0x87, 10]));
    }

    [TestMethod]
    public void RejectsHostileJp2Boxes()
    {
        byte[] data = File.ReadAllBytes(Fixture("palette.jp2"));

        // A box length past the end of the file is cut there; a length below its header is an error.
        byte[] longBox = (byte[])data.Clone();
        longBox[12 + 0] = 0x7F;
        Survives(longBox);
        byte[] shortBox = (byte[])data.Clone();
        shortBox[3] = 4;
        Assert.IsFalse(Survives(shortBox));

        // Every byte of the header boxes damaged in turn.
        int codestream = data.AsSpan().IndexOf("jp2c"u8.ToArray());
        for (int i = 0; i < codestream; i++)
        {
            foreach (byte v in new byte[] { 0, 0xFF, 0x80 })
            {
                byte[] copy = (byte[])data.Clone();
                copy[i] = v;
                Survives(copy);
            }
        }
    }

    /// <summary>A one page PDF drawing a JPXDecode image, reopened and decoded.</summary>
    private static PdfBitmap? ThroughPdf(byte[] data, int width, int height, PdfObject? colorSpace, params (string Key, PdfObject Value)[] extra)
    {
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = PdfNumber.FromInteger(width),
            ["Height"] = PdfNumber.FromInteger(height),
            ["Filter"] = new PdfName("JPXDecode"),
        };
        if (colorSpace is not null) dict.Set("ColorSpace", colorSpace);
        foreach ((string key, PdfObject value) in extra) dict.Set(key, value);

        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Dictionary.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = doc.AddObject(new PdfStream(dict, data)) } });
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData("q 100 0 0 100 0 0 cm /Im Do Q"u8)));
        PdfPageImage image = PdfDocument.Open(doc.ToArray()).Pages[0].GetImages().Single();
        Assert.AreEqual("JPXDecode", image.Encoding);
        return image.Decode();
    }

    [TestMethod]
    public void DecodesThroughAPdf()
    {
        int[][] rgb = Reference("rgb-53");
        byte[] data = File.ReadAllBytes(Fixture("rgb-53.jp2"));

        // The dictionary's color space, or without one the JP2 header's sRGB.
        foreach (PdfObject? space in new PdfObject?[] { new PdfName("DeviceRGB"), null })
        {
            PdfBitmap bitmap = ThroughPdf(data, 61, 45, space)!;
            Assert.AreEqual(PdfBitmapFormat.Rgb24, bitmap.Format);
            byte[] pixels = bitmap.Pixels.ToArray();
            for (int i = 0; i < pixels.Length; i++) Assert.AreEqual(rgb[i % 3][i / 3], pixels[i]);
        }

        // A raw codestream has no header: one component is gray.
        PdfBitmap gray = ThroughPdf(File.ReadAllBytes(Fixture("gray-plt.j2k")), 61, 45, null)!;
        Assert.AreEqual(PdfBitmapFormat.Gray8, gray.Format);
        CollectionAssert.AreEqual(Reference("gray-plt")[0].Select(v => (byte)v).ToArray(), gray.Pixels.ToArray());

        // The Decode array applies as for any image: [1 0] inverts.
        PdfBitmap inverted = ThroughPdf(File.ReadAllBytes(Fixture("gray-plt.j2k")), 61, 45, new PdfName("DeviceGray"), ("Decode", PdfArray.Of(1, 0)))!;
        CollectionAssert.AreEqual(Reference("gray-plt")[0].Select(v => (byte)(255 - v)).ToArray(), inverted.Pixels.ToArray());

        // An alpha channel (SMaskInData) is left out; the colors stay.
        PdfBitmap rgba = ThroughPdf(File.ReadAllBytes(Fixture("rgba-53.jp2")), 61, 45, null, ("SMaskInData", PdfNumber.FromInteger(1)))!;
        Assert.AreEqual(PdfBitmapFormat.Rgb24, rgba.Format);
        int[][] four = Reference("rgba-53");
        Assert.AreEqual(four[2][100], rgba.Pixels.Span[(100 * 3) + 2]);

        // The data's own size wins over a dictionary that disagrees.
        Assert.AreEqual(61, ThroughPdf(data, 30, 20, null)!.Width);
    }

    [TestMethod]
    public void UsesThePdfPaletteForIndexedImages()
    {
        byte[] data = File.ReadAllBytes(Fixture("palette.jp2"));
        int[] index = Reference("palette-index")[0];

        // The JP2 palette applies when the dictionary has no color space.
        PdfBitmap own = ThroughPdf(data, 61, 45, null)!;
        int[][] colors = Reference("palette");
        for (int i = 0; i < 61 * 45; i++) Assert.AreEqual(colors[1][i], own.Pixels.Span[(i * 3) + 1]);

        // An Indexed space in the dictionary reads the indexes through its own lookup table.
        var lookup = new byte[256 * 3];
        for (int i = 0; i < 256; i++) lookup[(i * 3) + 2] = (byte)(i * 5);
        PdfArray indexed = PdfArray.Of(new PdfName("Indexed"), new PdfName("DeviceRGB"), PdfNumber.FromInteger(255), new PdfString(lookup));
        PdfBitmap mapped = ThroughPdf(data, 61, 45, indexed)!;
        for (int i = 0; i < 61 * 45; i++) Assert.AreEqual((byte)(index[i] * 5), mapped.Pixels.Span[(i * 3) + 2]);
    }

    [TestMethod]
    public void DamagedDataIsAFormatErrorInAPdf()
    {
        byte[] data = File.ReadAllBytes(Fixture("rgb-53.jp2"));
        Assert.ThrowsExactly<PdfFormatException>(() => ThroughPdf(data.AsSpan(0, 60).ToArray(), 61, 45, null));
        Assert.ThrowsExactly<PdfFormatException>(() => ThroughPdf([1, 2, 3], 61, 45, new PdfName("DeviceRGB")));
    }

    /// <summary>
    /// Decoded a few rows at a time, every fixture gives the samples of the decode in one band:
    /// each band's window of rows reaches far enough for every lifting step, at every resolution,
    /// whatever the parity of the tile, the image origin or the band's first row.
    /// </summary>
    [TestMethod]
    [DataRow("gray-53.jp2")]
    [DataRow("gray-97.j2k")]
    [DataRow("gray-1res.jp2")]
    [DataRow("gray-3res.jp2")]
    [DataRow("gray16-53.jp2")]
    [DataRow("gray-signed.j2k")]
    [DataRow("gray-roi.j2k")]
    [DataRow("rgb-97.jp2")]
    [DataRow("rgb-97-tiles.jp2")]
    [DataRow("rgb-offset.jp2")]
    [DataRow("rgb-rpcl.jp2")]
    [DataRow("sub-rpcl.j2k")]
    [DataRow("sub-cprl.j2k")]
    [DataRow("style-allstyles.j2k")]
    [DataRow("palette.jp2")]
    public void BandsOfRowsGiveTheSameSamples(string file)
    {
        byte[] data = File.ReadAllBytes(Fixture(file));
        Jpeg2000Image whole = Jpeg2000Decoder.Decode(data, Limit);
        (Raster? once, _) = Jpeg2000Decoder.DecodeForPdf(data, null, Limit);
        foreach (int rows in new[] { 1, 2, 3, 5, 8 })
        {
            Jpeg2000Image banded = Jpeg2000Decoder.Decode(data, Limit, bandRows: rows);
            for (int c = 0; c < whole.Channels.Count; c++) CollectionAssert.AreEqual(whole.Channels[c].Samples, banded.Channels[c].Samples, $"{file}, {rows} rows, channel {c}");

            (Raster? raster, _) = Jpeg2000Decoder.DecodeForPdf(data, null, Limit, rows);
            CollectionAssert.AreEqual(once!.Samples, raster!.Samples, $"{file}, {rows} rows, raster");
        }
    }

    /// <summary>The raster written band by band is the one made from the planes, subsampled chroma and missing tiles included.</summary>
    [TestMethod]
    [DataRow("rgb-97-tiles.jp2", 0)]
    [DataRow("rgb-offset.jp2", 0)]
    [DataRow("gray16-53.jp2", 0)]
    [DataRow("sub-pcrl.j2k", 0)]
    [DataRow("rgb-lrcp.jp2", 3000)]
    [DataRow("rgb-97-tiles.jp2", 700)]
    public void RasterMatchesThePlanes(string file, int cut)
    {
        byte[] data = File.ReadAllBytes(Fixture(file));
        if (cut > 0) data = data.AsSpan(0, Math.Min(data.Length, cut)).ToArray();
        Jpeg2000Image planes = Jpeg2000Decoder.Decode(data, Limit);
        (Raster? raster, ColorSpace? space) = Jpeg2000Decoder.DecodeForPdf(data, null, Limit, bandRows: 3);
        Assert.IsNotNull(space);
        CollectionAssert.AreEqual(planes.ToRaster(space.Components, scale: true).Samples, raster!.Samples, file);
    }

    /// <summary>An sYCC image (JP2 colour space 18) comes out as RGB by IEC 61966-2-1, sample by sample.</summary>
    [TestMethod]
    public void ConvertsSyccToRgb()
    {
        byte[] data = File.ReadAllBytes(Fixture("rgb-53.jp2"));
        int colr = data.AsSpan().IndexOf("colr"u8.ToArray());
        Assert.AreEqual(1, data[colr + 4], "an enumerated colour space");
        data[colr + 10] = 18;
        Jpeg2000Image planes = Jpeg2000Decoder.Decode(data, Limit);
        Assert.AreEqual(18, planes.ColorSpace);
        static int Byte(double v) => Math.Max(0, Math.Min(255, (int)Math.Round(v)));
        foreach (int rows in new[] { 0, 4 })
        {
            (Raster? raster, ColorSpace? space) = Jpeg2000Decoder.DecodeForPdf(data, null, Limit, rows);
            Assert.AreEqual(ColorSpace.Rgb, space);
            for (int i = 0; i < 61 * 45; i++)
            {
                double y = planes.Channels[0].Samples[i], cb = planes.Channels[1].Samples[i] - 128, cr = planes.Channels[2].Samples[i] - 128;
                Assert.AreEqual(Byte(y + (1.402 * cr)), raster!.Samples[3 * i]);
                Assert.AreEqual(Byte(y - (0.344136 * cb) - (0.714136 * cr)), raster.Samples[(3 * i) + 1]);
                Assert.AreEqual(Byte(y + (1.772 * cb)), raster.Samples[(3 * i) + 2]);
            }
        }
    }

    /// <summary>
    /// A codestream of one tile that codes no coefficients: SIZ, a 5/3 COD with five levels and
    /// one layer, QCD, then an empty packet, one byte, for each resolution and component.
    /// </summary>
    private static byte[] EmptyCodestream(int width, int height, int components)
    {
        var bytes = new List<byte> { 0xFF, 0x4F };
        void U16(int v) => bytes.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(long v) => bytes.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

        U16(0xFF51);
        U16(38 + (3 * components));
        U16(0);
        foreach (long v in new long[] { width, height, 0, 0, width, height, 0, 0 }) U32(v);
        U16(components);
        for (int c = 0; c < components; c++) bytes.AddRange([7, 1, 1]);

        U16(0xFF52);
        U16(12);
        bytes.AddRange([0, 0, 0, 1, (byte)(components >= 3 ? 1 : 0), 5, 4, 4, 0, 1]);

        U16(0xFF5C);
        U16(19);
        bytes.AddRange([0x40, 8 << 3]);
        for (int r = 0; r < 5; r++) bytes.AddRange([9 << 3, 9 << 3, 10 << 3]);

        int packets = 6 * components;
        U16(0xFF90);
        U16(10);
        U16(0);
        U32(12 + 2 + packets);
        bytes.AddRange([0, 1]);
        U16(0xFF93);
        bytes.AddRange(new byte[packets]);
        U16(0xFFD9);
        return [.. bytes];
    }

    /// <summary>
    /// A colour image as one tile: its 32 bit planes alone would pass a 64 MB limit, while its
    /// 8 bit raster fits under it with room for the bands of rows on the way.
    /// </summary>
    [TestMethod]
    public void LargeColorImagesFitTheDecodeLimit()
    {
        const int width = 3000, height = 2400;
        const long limit = 64L << 20;
        byte[] data = EmptyCodestream(width, height, 3);
        Assert.ThrowsExactly<Jpeg2000FormatException>(() => Jpeg2000Decoder.Decode(data, limit), "planes of 82 MB");

        (Raster? raster, ColorSpace? space) = Jpeg2000Decoder.DecodeForPdf(data, null, limit);
        Assert.AreEqual(ColorSpace.Rgb, space);
        Assert.HasCount(width * height * 3, raster!.Samples);
        Assert.IsTrue(Array.TrueForAll(raster.Samples, v => v == 128), "no coefficients: mid-grey");

        // A limit under the raster and its bands of rows is still refused before anything is allocated.
        Assert.ThrowsExactly<Jpeg2000FormatException>(() => Jpeg2000Decoder.DecodeForPdf(data, null, 30L << 20));
    }

    [TestMethod]
    public void DecodesQuickly()
    {
        // The fastest of five rounds counts, so other work on a busy machine does not.
        byte[] data = File.ReadAllBytes(Fixture("rgb-97-tiles.jp2"));
        Jpeg2000Decoder.Decode(data, Limit);
        long best = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) Jpeg2000Decoder.Decode(data, Limit);
            best = Math.Min(best, watch.ElapsedMilliseconds);
        }

        Assert.IsLessThan(1000, best, $"10 decodes of 4800 pixels took {best} ms");
    }
}
