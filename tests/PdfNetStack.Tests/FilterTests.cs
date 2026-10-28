using System.IO.Compression;
using System.Text;
using PdfNetStack.Filters;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class FilterTests
{
    private const long Limit = 64 << 20;

    /// <summary>Copies leased decoder output into arrays for assertions.</summary>
    private static class D
    {
        public static byte[] Ascii85(ReadOnlySpan<byte> data, long limit) => Take(Decoders.Ascii85(data, limit));

        public static byte[] AsciiHex(ReadOnlySpan<byte> data, long limit) => Take(Decoders.AsciiHex(data, limit));

        public static byte[] RunLength(ReadOnlySpan<byte> data, long limit) => Take(Decoders.RunLength(data, limit));

        public static byte[] Lzw(ReadOnlySpan<byte> data, int earlyChange, long limit) => Take(Decoders.Lzw(data, earlyChange, limit));

        public static byte[] Flate(byte[] data, long limit) => Take(Decoders.Flate(data, limit));

        public static byte[] Take(BoundedBuffer lease)
        {
            using (lease) return lease.ToArray();
        }
    }

    [TestMethod]
    public void Ascii85KnownVector()
    {
        byte[] decoded = D.Ascii85("9jqo^BlbD-BleB1DJ+*+F(f,q~>"u8, Limit);
        Assert.AreEqual("Man is distinguished", Encoding.ASCII.GetString(decoded));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 1 }, D.Ascii85("z!<~>"u8, Limit));
    }

    [TestMethod]
    public void AsciiHexIgnoresWhitespaceAndPadsOddDigit() =>
        CollectionAssert.AreEqual(new byte[] { 0xAB, 0xCD, 0xE0 }, D.AsciiHex("ab c\nDe>"u8, Limit));

    [TestMethod]
    public void RunLength() =>
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 7, 7, 7, 7 }, D.RunLength([2, 1, 2, 3, 253, 7, 128], Limit));

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void LzwRoundTrip(int earlyChange)
    {
        var random = new Random(7);
        foreach (int size in new[] { 0, 1, 2, 100, 5000, 70_000 })
        {
            var data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)(random.Next(4) == 0 ? random.Next(256) : i % 7);
            byte[] encoded = LzwEncode(data, earlyChange);
            CollectionAssert.AreEqual(data, D.Lzw(encoded, earlyChange, Limit), $"size {size}");
        }
    }

    [TestMethod]
    public void FlateRoundTripsAndRejectsHeaderlessDeflate()
    {
        byte[] data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q 1 0 0 1 0 0 cm Q ", 500)));
        CollectionAssert.AreEqual(data, D.Flate(StreamCodec.Deflate(data, CompressionLevel.SmallestSize), Limit));

        // Without a zlib header the data is reported undecodable, so it is carried over untouched.
        var raw = new MemoryStream();
        using (var d = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
        Assert.ThrowsExactly<StreamDecodeException>(() => D.Flate(raw.ToArray(), Limit));
    }

    [TestMethod]
    public void FlateBombIsStoppedAtTheLimit()
    {
        byte[] bomb = StreamCodec.Deflate(new byte[32 << 20], CompressionLevel.SmallestSize);
        Assert.IsLessThan(64 * 1024, bomb.Length);
        Assert.ThrowsExactly<StreamDecodeException>(() => D.Flate(bomb, 1 << 20));
    }

    [TestMethod]
    public void CorruptFlateIsReportedNotReturned()
    {
        byte[] data = StreamCodec.Deflate(Encoding.ASCII.GetBytes(new string('x', 1000) + "abc"), CompressionLevel.Optimal);
        data[^2] ^= 0xFF;
        Assert.ThrowsExactly<StreamDecodeException>(() => D.Flate(data, Limit));
    }

    [TestMethod]
    [DataRow(1, 8, 1)]
    [DataRow(3, 8, 37)]
    [DataRow(4, 8, 5)]
    [DataRow(1, 1, 50)]
    [DataRow(1, 2, 13)]
    [DataRow(3, 4, 9)]
    [DataRow(2, 16, 11)]
    public void PngPredictorRoundTrip(int colors, int bpc, int columns)
    {
        var p = new PredictorParams(15, colors, bpc, columns);
        var random = new Random(colors * 100 + bpc);
        var data = new byte[p.RowBytes * 23];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i / 3 + random.Next(3));
        byte[] encoded = D.Take(Predictors.EncodePng(data, p));
        Assert.IsTrue(Predictors.TryDecode(encoded, p, Limit, out BoundedBuffer? decoded));
        CollectionAssert.AreEqual(data, D.Take(decoded!));
    }

    [TestMethod]
    public void TiffPredictorDecodes8Bit()
    {
        var p = new PredictorParams(2, 1, 8, 4);
        Assert.IsTrue(Predictors.TryDecode([1, 1, 1, 1, 10, 0, 255, 2], p, Limit, out BoundedBuffer? result));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 10, 10, 9, 11 }, D.Take(result!));
    }

    [TestMethod]
    public void UnknownPngRowFilterIsRejected()
    {
        var p = new PredictorParams(12, 1, 8, 2);
        Assert.IsFalse(Predictors.TryDecode([9, 1, 2], p, Limit, out _));
    }

    /// <summary>Reference LZW encoder used only to test the decoder.</summary>
    private static byte[] LzwEncode(byte[] data, int earlyChange)
    {
        var output = new List<byte>();
        int bitBuffer = 0, bitCount = 0, codeLength = 9;
        void Emit(int code)
        {
            bitBuffer = (bitBuffer << codeLength) | code;
            bitCount += codeLength;
            while (bitCount >= 8)
            {
                output.Add((byte)(bitBuffer >> (bitCount - 8)));
                bitCount -= 8;
            }

            bitBuffer &= (1 << bitCount) - 1;
        }

        var table = new Dictionary<string, int>();
        int next = 258;
        int emitted = 0;
        Emit(256);
        string w = "";
        foreach (byte b in data)
        {
            string wc = w + (char)b;
            if (wc.Length == 1 || table.ContainsKey(wc))
            {
                w = wc;
                continue;
            }

            Emit(w.Length == 1 ? w[0] : table[w]);
            emitted++;
            table[wc] = next++;
            if (next + earlyChange > (1 << codeLength) && codeLength < 12) codeLength++;
            if (next >= 4095)
            {
                Emit(256);
                table.Clear();
                next = 258;
                codeLength = 9;
                emitted = 0;
            }

            w = ((char)b).ToString();
        }

        if (w.Length > 0)
        {
            Emit(w.Length == 1 ? w[0] : table[w]);

            // The decoder adds one more table entry when it reads this final code, which can
            // widen the code before it reads end-of-data.
            if (emitted > 0 && ++next + earlyChange > (1 << codeLength) && codeLength < 12) codeLength++;
        }

        Emit(257);
        if (bitCount > 0) output.Add((byte)(bitBuffer << (8 - bitCount)));
        return output.ToArray();
    }
}
