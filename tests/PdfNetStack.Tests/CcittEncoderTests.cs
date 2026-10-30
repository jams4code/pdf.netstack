using PdfNetStack.Imaging;

namespace PdfNetStack.Tests;

/// <summary>The Group 4 encoder, the exact CCITT decode the proof relies on, and the bilevel reduction.</summary>
[TestClass]
public sealed class CcittEncoderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ccitt", name);

    private static CcittParameters Group4(int columns, int rows, bool blackIs1 = false) => new() { K = -1, Columns = columns, Rows = rows, BlackIs1 = blackIs1 };

    /// <summary>Packed rows with random runs: long and short, of either color, at the given density of 1 bits.</summary>
    internal static byte[] RandomRows(int width, int height, double ones, int seed)
    {
        var random = new Random(seed);
        int rowBytes = (width + 7) / 8;
        var rows = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                int run = random.Next(4) == 0 ? random.Next(1, 4000) : random.Next(1, 12);
                bool one = random.NextDouble() < ones;
                for (int i = x; i < Math.Min(width, x + run); i++)
                {
                    if (one) rows[(y * rowBytes) + (i >> 3)] |= (byte)(0x80 >> (i & 7));
                }

                x += run;
            }

            // Some rows repeat the one above, as scanned text does, to exercise vertical and pass modes.
            if (y > 0 && random.Next(3) == 0) Array.Copy(rows, (y - 1) * rowBytes, rows, y * rowBytes, rowBytes);
        }

        return rows;
    }

    private static void AssertSamePixels(byte[] expected, byte[] actual, int width, int height, string message)
    {
        int rowBytes = (width + 7) / 8;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int bit = 0x80 >> (x & 7);
                if ((expected[(y * rowBytes) + (x >> 3)] & bit) != (actual[(y * rowBytes) + (x >> 3)] & bit))
                {
                    Assert.Fail($"{message}: pixel ({x}, {y}) differs");
                }
            }
        }
    }

    [TestMethod]
    public void MatchesLibtiffByteForByte()
    {
        // tools/make_ccitt_fixtures.py writes g4.bin with libtiff; page.png has 0 bits for black.
        PngImage png = PngImage.Read(File.ReadAllBytes(Fixture("page.png")));
        byte[] encoded = CcittEncoder.EncodeGroup4(png.Decode(), png.Width, png.Height, blackIs1: false, 1 << 20)!;
        CollectionAssert.AreEqual(File.ReadAllBytes(Fixture("g4.bin")), encoded);
    }

    [TestMethod]
    public void RoundTripsEveryWidthAndPolarity()
    {
        int[] widths = [.. Enumerable.Range(1, 40), 63, 64, 65, 127, 1727, 1728, 1729, 2559, 2560, 2561, 2623, 2624, 2625, 5200, 9000];
        int seed = 0;
        foreach (int width in widths)
        {
            foreach (double ones in new[] { 0.0, 0.1, 0.5, 0.9, 1.0 })
            {
                foreach (bool blackIs1 in new[] { false, true })
                {
                    int height = width > 2000 ? 6 : 23;
                    byte[] rows = RandomRows(width, height, ones, ++seed);
                    byte[] encoded = CcittEncoder.EncodeGroup4(rows, width, height, blackIs1, 1 << 24)!;
                    string message = $"width {width}, ones {ones}, BlackIs1 {blackIs1}";
                    AssertSamePixels(rows, CcittDecoder.DecodeExact(encoded, Group4(width, height, blackIs1), height)!, width, height, message);
                    AssertSamePixels(rows, CcittDecoder.Decode(encoded, Group4(width, 0, blackIs1), height), width, height, message + ", rows unset");
                }
            }
        }
    }

    [TestMethod]
    public void IgnoresPaddingAndRespectsTheCeiling()
    {
        byte[] rows = RandomRows(13, 9, 0.5, 7);
        byte[] padded = (byte[])rows.Clone();
        for (int y = 0; y < 9; y++) padded[(y * 2) + 1] |= 0x07;
        CollectionAssert.AreEqual(CcittEncoder.EncodeGroup4(rows, 13, 9, false, 4096), CcittEncoder.EncodeGroup4(padded, 13, 9, false, 4096));

        byte[] noise = RandomRows(2000, 50, 0.5, 8);
        int size = CcittEncoder.EncodeGroup4(noise, 2000, 50, false, 1 << 20)!.Length;
        Assert.IsNull(CcittEncoder.EncodeGroup4(noise, 2000, 50, false, size - 1));
        Assert.IsNotNull(CcittEncoder.EncodeGroup4(noise, 2000, 50, false, size));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CcittEncoder.EncodeGroup4(new byte[3], 20, 2, false, 100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CcittEncoder.EncodeGroup4(new byte[3], 0, 2, false, 100));
    }

    [TestMethod]
    [DataRow("g4", -1, false, false)]
    [DataRow("g3-1d", 0, true, false)]
    [DataRow("g3-2d", 4, true, false)]
    [DataRow("g3-1d-aligned", 0, true, true)]
    [DataRow("mh", 0, false, true)]
    public void ExactDecodeAcceptsCleanDataAndRefusesDamage(string name, int k, bool endOfLine, bool byteAlign)
    {
        var p = new CcittParameters { K = k, EndOfLine = endOfLine, EncodedByteAlign = byteAlign, Columns = 301, Rows = 97 };
        byte[] encoded = File.ReadAllBytes(Fixture(name + ".bin"));
        CollectionAssert.AreEqual(CcittDecoder.Decode(encoded, p, 97), CcittDecoder.DecodeExact(encoded, p, 97), name);

        // Cut short: the lenient decoder keeps what it read, the exact one refuses.
        Assert.IsNull(CcittDecoder.DecodeExact(encoded.AsSpan(0, encoded.Length / 2), p, 97), name);

        // More rows than the data holds.
        Assert.IsNull(CcittDecoder.DecodeExact(encoded, p with { Rows = 120 }, 120), name);

        // Damage anywhere never throws, and whatever the exact decode returns is what the lenient one returns.
        var random = new Random(k + 10);
        for (int round = 0; round < 200; round++)
        {
            byte[] copy = (byte[])encoded.Clone();
            copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
            byte[]? exact = CcittDecoder.DecodeExact(copy, p, 97);
            if (exact is not null) CollectionAssert.AreEqual(CcittDecoder.Decode(copy, p, 97), exact);
        }
    }

    [TestMethod]
    public void ExactDecodeRefusesRunsPastTheRow()
    {
        // One row of 10 columns: horizontal mode, white 8 then black 4, which overshoots by 2.
        // 001 | white 8 = 10011 | black 4 = 011, then EOFB.
        byte[] data = Bits("001" + "10011" + "011" + "000000000001000000000001");
        var p = Group4(10, 1);
        Assert.IsNotNull(CcittDecoder.Decode(data, p, 1));
        Assert.IsNull(CcittDecoder.DecodeExact(data, p, 1));

        // The same row coded correctly, white 8 and black 2.
        Assert.IsNotNull(CcittDecoder.DecodeExact(Bits("001" + "10011" + "11" + "000000000001000000000001"), p, 1));
    }

    private static byte[] Bits(string bits)
    {
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        return [.. Enumerable.Range(0, bits.Length / 8).Select(i => System.Convert.ToByte(bits.Substring(i * 8, 8), 2))];
    }

    /// <summary>Rows of the given width and height from a predicate that says where the 1 bits are.</summary>
    private static byte[] Draw(int width, int height, Func<int, int, bool> one)
    {
        int rowBytes = (width + 7) / 8;
        var rows = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (one(x, y)) rows[(y * rowBytes) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return rows;
    }

    private static bool Bit(byte[] rows, int width, int x, int y) => (rows[(y * ((width + 7) / 8)) + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    [TestMethod]
    public void BilevelReductionKeepsThinStrokesAndDropsSpeckles()
    {
        // Black text on white, 0 bits black as scanners write it, reduced four times.
        // A one pixel vertical line at x = 41, a one pixel horizontal line at y = 81, a solid
        // block, and a lone speckle.
        static bool White(int x, int y) =>
            x != 41 && y != 81 && !(x >= 120 && x < 160 && y >= 20 && y < 60) && !(x == 202 && y == 150);
        byte[] rows = Draw(240, 200, White);
        Assert.IsFalse(BilevelResampler.OnesAreForeground(rows, 240, 200), "white is the background");

        double[] coverage = BilevelResampler.Coverage(rows, 240, 200, 60, 50);
        byte[] small = BilevelResampler.Threshold(coverage, 60, 50, onesAreForeground: false);
        for (int y = 0; y < 50; y++) Assert.IsFalse(Bit(small, 60, 10, y), $"vertical stroke at row {y}");
        for (int x = 0; x < 60; x++) Assert.IsFalse(Bit(small, 60, x, 20), $"horizontal stroke at column {x}");
        for (int y = 5; y < 15; y++)
        {
            for (int x = 30; x < 40; x++) Assert.IsFalse(Bit(small, 60, x, y), "block");
        }

        Assert.IsTrue(Bit(small, 60, 50, 37), "speckle dropped");
        Assert.IsTrue(Bit(small, 60, 9, 5) && Bit(small, 60, 11, 5), "strokes are not widened");
        Assert.IsTrue(Bit(small, 60, 29, 10) && Bit(small, 60, 40, 10), "the block is not widened");

        // Share of 1 bits: a fully white area is 1, the block 0, the line's column a quarter dark.
        Assert.AreEqual(1.0, coverage[(2 * 60) + 2], 1e-12);
        Assert.AreEqual(0.0, coverage[(10 * 60) + 35], 1e-12);
        Assert.AreEqual(0.75, coverage[(2 * 60) + 10], 1e-12);
    }

    [TestMethod]
    public void BilevelReductionWeighsFractionalPixels()
    {
        // 3 columns to 2: the middle source column is split evenly between both new pixels.
        byte[] rows = Draw(3, 1, (x, _) => x == 1);
        double[] coverage = BilevelResampler.Coverage(rows, 3, 1, 2, 1);
        Assert.AreEqual(1.0 / 3, coverage[0], 1e-12);
        Assert.AreEqual(1.0 / 3, coverage[1], 1e-12);

        // Every reduction keeps the overall share of 1 bits.
        byte[] random = RandomRows(997, 311, 0.3, 4);
        double ones = 0;
        for (int y = 0; y < 311; y++)
        {
            for (int x = 0; x < 997; x++) ones += Bit(random, 997, x, y) ? 1 : 0;
        }

        double[] reduced = BilevelResampler.Coverage(random, 997, 311, 413, 129);
        Assert.AreEqual(ones / (997.0 * 311), reduced.Average(), 1e-9);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BilevelResampler.Coverage(random, 997, 311, 998, 10));
    }
}
