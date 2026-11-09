using System.Diagnostics;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>
/// JBIG2 against PDFium's rendering of the same streams (tools/make_jbig2_fixtures.py): the T.88
/// Annex H test datastream and synthetic streams from a test-only encoder.
/// </summary>
[TestClass]
public sealed class Jbig2DecoderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jbig2", name);

    private static byte[]? Globals(string name) => File.Exists(Fixture(name + ".globals.jb2")) ? File.ReadAllBytes(Fixture(name + ".globals.jb2")) : null;

    /// <summary>PDFium's rendering, one bool per pixel, true for black.</summary>
    private static (bool[] Black, int Width, int Height) Reference(string name)
    {
        PngImage png = PngImage.Read(File.ReadAllBytes(Fixture(name + ".png")));
        Assert.AreEqual(1, png.BitDepth);
        return (Unpack(png.Decode(), png.Width, png.Height, blackBit: 0), png.Width, png.Height);
    }

    private static bool[] Unpack(byte[] packed, int width, int height, int blackBit)
    {
        int rowBytes = (width + 7) / 8;
        var black = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) black[(y * width) + x] = ((packed[(y * rowBytes) + (x >> 3)] >> (7 - (x & 7))) & 1) == blackBit;
        }

        return black;
    }

    private static byte[] Hex(string hex) => [.. Enumerable.Range(0, hex.Length / 2).Select(i => System.Convert.ToByte(hex.Substring(2 * i, 2), 16))];

    private static int Differences(bool[] a, bool[] b) => a.Zip(b, (x, y) => x != y).Count(d => d);

    /// <summary>A one page PDF drawing the stream as an image XObject, as a scanner would write it.</summary>
    private static PdfPageImage Placed(byte[] data, byte[]? globals, int width, int height, bool mask)
    {
        PdfDocument doc = PdfDocument.Create();
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = PdfNumber.FromInteger(width),
            ["Height"] = PdfNumber.FromInteger(height),
            ["Filter"] = new PdfName("JBIG2Decode"),
        };
        if (globals is not null) dict.Set("DecodeParms", new PdfDictionary { ["JBIG2Globals"] = doc.AddObject(new PdfStream(new PdfDictionary(), globals)) });
        if (mask)
        {
            dict.Set("ImageMask", PdfBoolean.True);
        }
        else
        {
            dict.Set("ColorSpace", new PdfName("DeviceGray"));
            dict.Set("BitsPerComponent", PdfNumber.FromInteger(1));
        }

        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Dictionary.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = doc.AddObject(new PdfStream(dict, data)) } });
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData("q 100 0 0 100 0 0 cm /Im Do Q"u8)));
        return PdfDocument.Open(doc.ToArray()).Pages[0].GetImages().Single();
    }

    [TestMethod]
    [DataRow("annexh-p1", DisplayName = "Annex H page 1: Huffman symbols and text, MMR generic, pattern and halftone")]
    [DataRow("annexh-p2", DisplayName = "Annex H page 2: the same page arithmetic coded")]
    [DataRow("annexh-p3", DisplayName = "Annex H page 3: symbols refined and aggregated, a refined instance")]
    [DataRow("generic-t0")]
    [DataRow("generic-t0-at")]
    [DataRow("generic-t1-tpgd")]
    [DataRow("generic-t2-at")]
    [DataRow("generic-t3-tpgd")]
    [DataRow("generic-mmr")]
    [DataRow("compose")]
    [DataRow("striped")]
    [DataRow("refine")]
    [DataRow("text-arith")]
    [DataRow("text-refine-t0")]
    [DataRow("text-refine-t1")]
    [DataRow("text-huffman")]
    public void MatchesPdfium(string name)
    {
        (bool[] expected, int width, int height) = Reference(name);
        Assert.IsGreaterThan(50, expected.Count(b => b), "the page has black on it");
        byte[] data = File.ReadAllBytes(Fixture(name + ".jb2"));

        // The decoder codes black as 1, as T.88 does.
        byte[] packed = Jbig2Decoder.Decode(data, Globals(name), width, height);
        Assert.AreEqual(0, Differences(expected, Unpack(packed, width, height, blackBit: 1)), name);

        // Through a PDF, gray or a stencil mask, black text lands on white.
        foreach (bool mask in new[] { false, true })
        {
            PdfPageImage image = Placed(data, Globals(name), width, height, mask);
            Assert.AreEqual("JBIG2Decode", image.Encoding);
            PdfBitmap bitmap = image.Decode()!;
            Assert.AreEqual(PdfBitmapFormat.Gray8, bitmap.Format);
            Assert.AreEqual(0, Differences(expected, [.. bitmap.Pixels.ToArray().Select(v => v == 0)]), mask ? "mask" : "gray");
        }
    }

    [TestMethod]
    public void AnnexHPagesOneAndTwoAgree()
    {
        // H.1: page 1 is Huffman and MMR coded, page 2 arithmetic coded, and the bitmaps are the same.
        byte[] globals = File.ReadAllBytes(Fixture("annexh-p1.globals.jb2"));
        byte[] one = Jbig2Decoder.Decode(File.ReadAllBytes(Fixture("annexh-p1.jb2")), globals, 64, 56);
        byte[] two = Jbig2Decoder.Decode(File.ReadAllBytes(Fixture("annexh-p2.jb2")), globals, 64, 56);
        CollectionAssert.AreEqual(one, two);
    }

    [TestMethod]
    public void ArithmeticDecoderPassesAnnexH2()
    {
        // T.88, H.2: 30 coded bytes decode with one context to 32 bytes of decisions.
        byte[] coded = Hex("84C73BFCE1A1430402200000410DBB86F4317FFF88FF37471ADB6ADFFFAC");
        byte[] expected = Hex("00020051000000C00352872AAAAAAAAA82C02000FCD79EF6BF7FED904F46A3BF");
        var decoder = new Jbig2ArithmeticDecoder(coded, 0, coded.Length);
        var context = new byte[1];
        var decoded = new byte[32];
        for (int i = 0; i < 256; i++) decoded[i >> 3] |= (byte)(decoder.Decode(context, 0) << (7 - (i & 7)));
        CollectionAssert.AreEqual(expected, decoded);
    }

    [TestMethod]
    public void StandardTablesHaveTheirCodes()
    {
        // T.88, B.5: a sample of the printed prefix codes, which the B.3 assignment must reproduce.
        static (long Code, int Length) Code(int table, long low, int kind = 0) =>
            Jbig2HuffmanTable.Standard(table).Codes().Where(c => c.Low == low && c.Kind == kind).Select(c => (c.Code, c.Length)).Single();

        Assert.AreEqual((0b110L, 3), Code(1, 272));
        Assert.AreEqual((0b111L, 3), Code(1, 65808, kind: 2));
        Assert.AreEqual((0b111110L, 6), Code(3, 0, kind: 3));
        Assert.AreEqual((0b11111111L, 8), Code(3, -257, kind: 1));
        Assert.AreEqual((0b11100L, 5), Code(6, -2048));
        Assert.AreEqual((0b01L, 2), Code(8, 0, kind: 3));
        Assert.AreEqual((0b111111101L, 9), Code(8, -3));
        Assert.AreEqual((0b1111101L, 7), Code(10, 2118));
        Assert.AreEqual((0b1111110L, 7), Code(15, -25, kind: 1));
        foreach (int table in Enumerable.Range(1, 15))
        {
            // Prefix free: no code begins another.
            List<(long Low, byte Kind, long Code, int Length)> codes = [.. Jbig2HuffmanTable.Standard(table).Codes()];
            foreach (var a in codes)
            {
                foreach (var b in codes)
                {
                    if (a != b && a.Length <= b.Length) Assert.AreNotEqual(a.Code, b.Code >> (b.Length - a.Length), $"B.{table}");
                }
            }
        }
    }

    [TestMethod]
    public void OutputTakesTheImageSize()
    {
        // The image dictionary's size wins: a smaller image crops the page, a larger one pads it white.
        (bool[] expected, int width, int height) = Reference("generic-t0");
        byte[] data = File.ReadAllBytes(Fixture("generic-t0.jb2"));
        bool[] small = Unpack(Jbig2Decoder.Decode(data, null, 100, 50), 100, 50, blackBit: 1);
        bool[] large = Unpack(Jbig2Decoder.Decode(data, null, width + 30, height + 20), width + 30, height + 20, blackBit: 1);
        for (int y = 0; y < height + 20; y++)
        {
            for (int x = 0; x < width + 30; x++)
            {
                bool inside = x < width && y < height && expected[(y * width) + x];
                Assert.AreEqual(inside, large[(y * (width + 30)) + x]);
                if (x < 100 && y < 50) Assert.AreEqual(inside, small[(y * 100) + x]);
            }
        }
    }

    [TestMethod]
    public void UnknownLengthGenericRegionEndsAtItsMarker()
    {
        // 7.2.7: the data length 0xFFFFFFFF, the data ending with 0xFFAC and the row count.
        byte[] data = File.ReadAllBytes(Fixture("generic-t0.jb2"));
        (bool[] expected, int width, int height) = Reference("generic-t0");
        int start = 11 + 19 + 7;
        int length = (int)Jbig2Decoder.UInt32(data, start);
        byte[] region = data.AsSpan(start + 4, length).ToArray();
        Assert.AreEqual(0xFF, region[^2]);
        Assert.AreEqual(0xAC, region[^1]);
        byte[] rows = [0, 0, 0, (byte)height];
        byte[] unknown = [.. data.AsSpan(0, start), 0xFF, 0xFF, 0xFF, 0xFF, .. region, .. rows, .. data.AsSpan(start + 4 + length)];
        Assert.AreEqual(0, Differences(expected, Unpack(Jbig2Decoder.Decode(unknown, null, width, height), width, height, blackBit: 1)));
    }

    /// <summary>Decodes, allowing only the decoder's own exception.</summary>
    private static void Survives(byte[] data, byte[]? globals, int width, int height)
    {
        try
        {
            byte[] output = Jbig2Decoder.Decode(data, globals, width, height);
            Assert.HasCount(((width + 7) / 8) * height, output);
        }
        catch (Jbig2FormatException)
        {
        }
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void SurvivesTruncationAndDamage()
    {
        var clock = Stopwatch.StartNew();
        var random = new Random(7);
        foreach (string name in new[] { "annexh-p1", "annexh-p2", "annexh-p3", "generic-t1-tpgd", "generic-mmr", "refine", "text-refine-t0", "text-huffman", "striped" })
        {
            byte[] data = File.ReadAllBytes(Fixture(name + ".jb2"));
            byte[]? globals = Globals(name);
            (_, int width, int height) = Reference(name);
            for (int cut = 0; cut < data.Length; cut += Math.Max(1, data.Length / 40)) Survives(data.AsSpan(0, cut).ToArray(), globals, width, height);
            for (int round = 0; round < 150; round++)
            {
                byte[] copy = (byte[])data.Clone();
                for (int i = 0; i < 3; i++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
                byte[]? damagedGlobals = globals is null || round % 2 == 0 ? globals : Damage(globals, random);
                Survives(copy, damagedGlobals, width, height);
            }
        }

        Assert.IsLessThan(60_000, clock.ElapsedMilliseconds);
    }

    private static byte[] Damage(byte[] data, Random random)
    {
        byte[] copy = (byte[])data.Clone();
        copy[random.Next(copy.Length)] = (byte)random.Next(256);
        return copy;
    }

    private static byte[] Segment(int number, int type, byte[] data, byte[]? referred = null, uint? length = null) =>
        [.. Be(number), (byte)type, (byte)((referred?.Length ?? 0) << 5), .. referred ?? [], 1, .. Be((int)(length ?? (uint)data.Length)), .. data];

    private static byte[] Be(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static readonly byte[] PageInfo = [.. Be(64), .. Be(64), .. Be(0), .. Be(0), 0, 0, 0];

    private static byte[] RegionInfo(uint width, uint height) => [.. Be((int)width), .. Be((int)height), .. Be(0), .. Be(0), 0];

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void RejectsHostileSizesAndCounts()
    {
        var clock = Stopwatch.StartNew();
        byte[] noise = [.. Enumerable.Range(0, 64).Select(i => (byte)(i * 37))];
        byte[][] streams =
        [
            // A generic region of 2^32 - 1 by 2^32 - 1 pixels, MMR and arithmetic.
            [.. Segment(0, 48, PageInfo), .. Segment(1, 38, [.. RegionInfo(uint.MaxValue, uint.MaxValue), 1, .. noise])],
            [.. Segment(0, 48, PageInfo), .. Segment(1, 38, [.. RegionInfo(1 << 20, 1 << 20), 0, 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE, .. noise])],

            // A symbol dictionary declaring 2^32 - 1 new symbols, then one declaring 2^20 that its data cannot hold.
            [.. Segment(0, 48, PageInfo), .. Segment(1, 0, [0, 0, 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE, .. Be(1), .. Be(-1), .. noise])],
            [.. Segment(0, 48, PageInfo), .. Segment(1, 0, [0, 0, 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE, .. Be(1 << 20), .. Be(1 << 20), .. noise])],

            // A text region of 2^32 - 1 instances over a symbol it reads from noise.
            [.. Segment(0, 0, [0, 0, 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE, .. Be(1), .. Be(1), .. noise]), .. Segment(1, 48, PageInfo),
                .. Segment(2, 6, [.. RegionInfo(64, 64), 0, 0x10, .. Be(-1), .. noise], referred: [0])],

            // A pattern dictionary of 2^32 patterns, a halftone grid of 2^32 - 1 squared cells.
            [.. Segment(0, 48, PageInfo), .. Segment(1, 16, [0, 8, 8, .. Be(-1), .. noise])],
            [.. Segment(0, 48, PageInfo), .. Segment(1, 16, [0, 4, 4, .. Be(3), .. noise]),
                .. Segment(2, 22, [.. RegionInfo(64, 64), 0, .. Be(-1), .. Be(-1), .. Be(0), .. Be(0), 0, 4, 0, 0, .. noise], referred: [1])],

            // A code table with 2^31 one value lines, segment lengths past the end, unknown types, referred-to segments missing.
            [.. Segment(0, 48, PageInfo), .. Segment(1, 53, [0, .. Be(int.MinValue), .. Be(int.MaxValue), .. noise])],
            [.. Segment(0, 48, PageInfo), .. Segment(1, 38, noise, length: 0x7FFFFFFF)],
            [.. Segment(0, 48, PageInfo), .. Segment(1, 27, noise)],
            [.. Segment(0, 48, PageInfo), .. Segment(1, 6, [.. RegionInfo(8, 8), 0, 0, .. Be(1), .. noise], referred: [9])],
            [.. Segment(0, 38, [.. RegionInfo(8, 8), 1, .. noise])],
        ];
        foreach (byte[] stream in streams)
        {
            Assert.ThrowsExactly<Jbig2FormatException>(() => Jbig2Decoder.Decode(stream, null, 64, 64), BitConverter.ToString(stream, 0, Math.Min(40, stream.Length)));
        }

        Assert.IsLessThan(20_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    public void HostileDataIsAFormatErrorThroughAPdf()
    {
        byte[] stream = [.. Segment(0, 48, PageInfo), .. Segment(1, 38, [.. RegionInfo(uint.MaxValue, 1), 1])];
        Assert.ThrowsExactly<PdfFormatException>(() => Placed(stream, null, 64, 64, mask: false).Decode());

        // No page information at all.
        Assert.ThrowsExactly<PdfFormatException>(() => Placed([], null, 64, 64, mask: false).Decode());
    }

    /// <summary>
    /// Scanned pages often code the page as one symbol drawn by a text region, which costs five
    /// times the page: the symbol, the page, the region and drawing both. Two full page regions
    /// over a page cost the same and must decode. Each MMR row here is one V0 code, a white row.
    /// </summary>
    [TestMethod]
    public void FullPageRegionsFitTheWorkBudget()
    {
        const int width = 2048, height = 1700;
        byte[] page = [.. Be(width), .. Be(height), .. Be(0), .. Be(0), 0x04, 0, 0];
        byte[] whiteRows = [.. Enumerable.Repeat((byte)0xFF, (height + 7) / 8)];
        byte[] Region(int op) => [.. Be(width), .. Be(height), .. Be(0), .. Be(0), (byte)op, 1, .. whiteRows];
        byte[] stream = [.. Segment(0, 48, page), .. Segment(1, 38, Region(Jbig2Bitmap.Replace)), .. Segment(2, 38, Region(Jbig2Bitmap.Or))];

        byte[] packed = Jbig2Decoder.Decode(stream, null, width, height);
        Assert.HasCount((width / 8) * height, packed);
        Assert.IsTrue(packed.All(b => b == 0), "The black default page is replaced by white regions.");
    }

    private static Jbig2Bitmap Random(Random random, int width, int height)
    {
        Jbig2Bitmap bitmap = Jbig2Bitmap.Create(width, height, new Jbig2Budget(1L << 30));
        random.NextBytes(bitmap.Data);
        return bitmap;
    }

    /// <summary>
    /// Composition a byte at a time draws what composition pixel by pixel draws: every operator,
    /// every bit alignment, sources hanging off each edge, and padding bits in the source rows.
    /// </summary>
    [TestMethod]
    public void ComposeMatchesPixelByPixel()
    {
        var random = new Random(3);
        var budget = new Jbig2Budget(1L << 40);
        for (int round = 0; round < 400; round++)
        {
            Jbig2Bitmap target = Random(random, random.Next(1, 70), random.Next(1, 12));
            Jbig2Bitmap source = Random(random, random.Next(1, 70), random.Next(1, 12));
            int x = random.Next(-source.Width - 3, target.Width + 3), y = random.Next(-source.Height - 2, target.Height + 2);
            int op = round % 5;
            var expected = new bool[target.Width * target.Height];
            for (int ty = 0; ty < target.Height; ty++)
            {
                for (int tx = 0; tx < target.Width; tx++)
                {
                    int d = target.Get(tx, ty), sx = tx - x, sy = ty - y;
                    bool inside = sx >= 0 && sx < source.Width && sy >= 0 && sy < source.Height;
                    int s = inside ? source.Get(sx, sy) : 0;
                    int v = !inside ? d : op switch { 0 => d | s, 1 => d & s, 2 => d ^ s, 3 => 1 ^ d ^ s, _ => s };
                    expected[(ty * target.Width) + tx] = v != 0;
                }
            }

            target.Compose(source, x, y, op, budget);
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], target.Get(i % target.Width, i / target.Width) != 0, $"round {round}, operator {op}, at ({x}, {y})");
        }

        // A symbol cut from a collective bitmap, at any offset.
        Jbig2Bitmap collective = Random(random, 61, 7);
        for (int at = 0; at < 61; at += 5)
        {
            Jbig2Bitmap symbol = collective.Columns(at, 13, budget);
            for (int py = 0; py < 7; py++)
            {
                for (int px = 0; px < 13; px++) Assert.AreEqual(collective.Get(at + px, py), symbol.Get(px, py));
            }
        }
    }

    /// <summary>
    /// The text-arith page with its region's combination operator, its default pixel and the
    /// page's default pixel changed: regions that combine with the page symbol by symbol are drawn
    /// straight onto it, the others through a bitmap of their own, to the same result.
    /// </summary>
    [TestMethod]
    [DataRow(4, 0, 1, "replace: the region over a black page")]
    [DataRow(2, 0, 1, "xor: the region inverts a black page")]
    [DataRow(1, 0, 1, "and: white around the symbols clears a black page")]
    [DataRow(0, 1, 0, "or with a black region default: all black")]
    [DataRow(3, 0, 0, "xnor: the region inverted on a white page")]
    public void TextRegionsCombineWithThePage(int op, int regionDefault, int pageDefault, string what)
    {
        (bool[] reference, int width, int height) = Reference("text-arith");
        byte[] data = File.ReadAllBytes(Fixture("text-arith.jb2"));

        // Page information data at 11, its flags at 27; the text region's data at 42: the external
        // operator at 58, SBDEFPIXEL in bit 1 of byte 59.
        Assert.AreEqual(48, data[4]);
        Assert.AreEqual(6, data[34]);
        data[27] = (byte)((data[27] & ~0x04) | (pageDefault << 2));
        data[58] = (byte)op;
        data[59] = (byte)((data[59] & ~0x02) | (regionDefault << 1));
        bool[] page = Unpack(Jbig2Decoder.Decode(data, Globals("text-arith"), width, height), width, height, blackBit: 1);
        for (int i = 0; i < page.Length; i++)
        {
            bool region = regionDefault == 1 || reference[i], under = pageDefault == 1;
            bool expected = op switch { 0 => under | region, 1 => under & region, 2 => under ^ region, 3 => !(under ^ region), _ => region };
            Assert.AreEqual(expected, page[i], what);
        }
    }
}
