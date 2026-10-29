using System.Diagnostics;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class CcittDecoderTests
{
    private const int Columns = 301;
    private const int Rows = 97;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ccitt", name);

    /// <summary>The page every fixture encodes, one bool per pixel, true for black (tools/make_ccitt_fixtures.py).</summary>
    private static bool[] Page()
    {
        PngImage png = PngImage.Read(File.ReadAllBytes(Fixture("page.png")));
        Assert.AreEqual(1, png.BitDepth);
        return Unpack(png.Decode(), png.Width, png.Height, blackBit: 0);
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

    private static CcittParameters Parameters(int k, bool endOfLine, bool byteAlign, bool blackIs1 = false, int rows = Rows) =>
        new() { K = k, EndOfLine = endOfLine, EncodedByteAlign = byteAlign, Columns = Columns, Rows = rows, BlackIs1 = blackIs1 };

    private static int Differences(bool[] a, bool[] b) => a.Zip(b, (x, y) => x != y).Count(d => d);

    [TestMethod]
    public void CodeTablesArePrefixFree()
    {
        foreach (bool white in new[] { true, false })
        {
            List<(string Code, int Run)> codes = [.. CcittDecoder.Codes(white)];
            Assert.HasCount(64 + 27 + 13 + 1, codes);
            foreach ((string a, int runA) in codes)
            {
                foreach ((string b, int runB) in codes)
                {
                    if (runA != runB) Assert.IsFalse(b.StartsWith(a, StringComparison.Ordinal), $"{a} ({runA}) is a prefix of {b} ({runB})");
                }
            }
        }
    }

    [TestMethod]
    [DataRow("g4", -1, false, false)]
    [DataRow("g3-1d", 0, true, false)]
    [DataRow("g3-2d", 4, true, false)]
    [DataRow("g3-1d-aligned", 0, true, true)]
    [DataRow("mh", 0, false, true)]
    public void MatchesLibtiff(string name, int k, bool endOfLine, bool byteAlign)
    {
        bool[] page = Page();
        Assert.IsGreaterThan(1000, page.Count(b => b), "the page has black on it");
        byte[] encoded = File.ReadAllBytes(Fixture(name + ".bin"));
        Assert.AreEqual(0, Differences(page, Unpack(CcittDecoder.Decode(encoded, Parameters(k, endOfLine, byteAlign), Rows), Columns, Rows, blackBit: 0)), name);

        // BlackIs1 inverts the output bits, not the picture.
        byte[] inverted = CcittDecoder.Decode(encoded, Parameters(k, endOfLine, byteAlign, blackIs1: true), Rows);
        Assert.AreEqual(0, Differences(page, Unpack(inverted, Columns, Rows, blackBit: 1)), name);

        // Rows 0 means as many as the image is high.
        Assert.AreEqual(0, Differences(page, Unpack(CcittDecoder.Decode(encoded, Parameters(k, endOfLine, byteAlign, rows: 0), Rows), Columns, Rows, blackBit: 0)), name);
    }

    [TestMethod]
    public void DecodesThroughAPdf()
    {
        bool[] page = Page();
        foreach (bool mask in new[] { false, true })
        {
            var parms = new PdfDictionary { ["K"] = PdfNumber.FromInteger(-1), ["Columns"] = PdfNumber.FromInteger(Columns), ["Rows"] = PdfNumber.FromInteger(Rows) };
            var dict = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(Columns),
                ["Height"] = PdfNumber.FromInteger(Rows),
                ["Filter"] = new PdfName("CCITTFaxDecode"),
                ["DecodeParms"] = parms,
            };
            if (mask) dict.Set("ImageMask", PdfBoolean.True);
            else dict.Set("ColorSpace", new PdfName("DeviceGray"));

            PdfDocument doc = PdfDocument.Create();
            PdfPage pdfPage = doc.Pages.Add(PdfRectangle.A4);
            pdfPage.Dictionary.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = doc.AddObject(new PdfStream(dict, File.ReadAllBytes(Fixture("g4.bin")))) } });
            pdfPage.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData("q 301 0 0 97 0 0 cm /Im Do Q"u8)));
            PdfPageImage image = PdfDocument.Open(doc.ToArray()).Pages[0].GetImages().Single();
            Assert.AreEqual("CCITTFaxDecode", image.Encoding);
            PdfBitmap bitmap = image.Decode()!;
            Assert.AreEqual(PdfBitmapFormat.Gray8, bitmap.Format);

            // A mask paints where its samples are 0, which here are the black pixels too.
            bool[] black = [.. bitmap.Pixels.ToArray().Select(v => v == 0)];
            Assert.AreEqual(0, Differences(page, black), mask ? "mask" : "gray");
        }
    }

    [TestMethod]
    public void KeepsWhatPrecedesDamage()
    {
        bool[] page = Page();
        byte[] encoded = File.ReadAllBytes(Fixture("g4.bin"));

        // Cut in half: the rows before the cut decode, the rest stay white.
        byte[] half = encoded.AsSpan(0, encoded.Length / 2).ToArray();
        bool[] partial = Unpack(CcittDecoder.Decode(half, Parameters(-1, false, false), Rows), Columns, Rows, blackBit: 0);
        int firstWrong = Enumerable.Range(0, page.Length).First(i => page[i] != partial[i]) / Columns;
        Assert.IsGreaterThan(20, firstWrong, "rows before the cut");
        Assert.IsTrue(partial.Skip((firstWrong + 2) * Columns).All(b => !b), "white after the cut");

        // No data at all is a white page.
        Assert.IsTrue(Unpack(CcittDecoder.Decode([], Parameters(-1, false, false), Rows), Columns, Rows, blackBit: 0).All(b => !b));
    }

    [TestMethod]
    public void SurvivesHostileData()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CcittDecoder.Decode([0], new CcittParameters { Columns = 0, Rows = 1 }, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CcittDecoder.Decode([0], new CcittParameters { Columns = CcittDecoder.MaxColumns + 1, Rows = 1 }, 1));

        var clock = Stopwatch.StartNew();
        var random = new Random(5);
        foreach (string name in new[] { "g4", "g3-2d", "mh" })
        {
            byte[] encoded = File.ReadAllBytes(Fixture(name + ".bin"));
            int k = name == "g4" ? -1 : name == "g3-2d" ? 4 : 0;
            for (int round = 0; round < 300; round++)
            {
                byte[] copy = (byte[])encoded.Clone();
                for (int i = 0; i < 3; i++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
                byte[] output = CcittDecoder.Decode(copy, Parameters(k, k >= 0 && name != "mh", name == "mh"), Rows);
                Assert.HasCount(((Columns + 7) / 8) * Rows, output);
            }
        }

        // Long runs of nothing but make-up codes, and pure zeros, end without hanging.
        byte[] zeros = new byte[1 << 16];
        byte[] ones = Enumerable.Repeat((byte)0xFF, 1 << 16).ToArray();
        foreach (byte[] data in new[] { zeros, ones })
        {
            foreach (int k in new[] { -1, 0, 1 }) CcittDecoder.Decode(data, new CcittParameters { K = k, Columns = 2000, Rows = 2000 }, 2000);
        }

        Assert.IsLessThan(20_000, clock.ElapsedMilliseconds);
    }
}
