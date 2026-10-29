using System.IO.Compression;
using System.Text;
using PdfNetStack.Filters;

namespace PdfNetStack.Tests;

/// <summary>The exhaustive Flate encoder: valid zlib for every kind of input, and never larger than zlib.</summary>
[TestClass]
public sealed class ExhaustiveDeflateTests
{
    private const long Limit = 256 << 20;

    private static byte[] Inflate(byte[] data)
    {
        using BoundedBuffer lease = Decoders.Flate(data, Limit);
        return lease.ToArray();
    }

    private static byte[] RoundTrip(byte[] data)
    {
        byte[]? compressed = ExhaustiveDeflate.TryCompress(data);
        Assert.IsNotNull(compressed);
        CollectionAssert.AreEqual(data, Inflate(compressed));
        return compressed;
    }

    /// <summary>A content stream: operators and coordinates with the repetition real pages have.</summary>
    private static byte[] ContentStream(int lines, int seed = 3)
    {
        var random = new Random(seed);
        var text = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            text.Append("BT /F").Append(random.Next(1, 4)).Append(' ').Append(random.Next(8, 13)).Append(" Tf ")
                .Append(random.Next(36, 560)).Append(' ').Append(random.Next(36, 760)).Append(" Td (")
                .Append(Words[random.Next(Words.Length)]).Append(' ').Append(Words[random.Next(Words.Length)]).Append(") Tj ET\n");
            if (i % 7 == 0) text.Append("q 0.5 w ").Append(random.Next(600)).Append(' ').Append(random.Next(800)).Append(" m ").Append(random.Next(600)).Append(" 400 l S Q\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static readonly string[] Words = ["invoice", "total", "amount", "the", "of", "delivery", "page", "number", "date", "reference", "quantity", "price"];

    [TestMethod]
    public void EdgeSizesRoundTrip()
    {
        RoundTrip([]);
        RoundTrip([42]);
        RoundTrip([1, 2]);
        RoundTrip([7, 7, 7]);
        RoundTrip("abcabcabcabcabcabc"u8.ToArray());
    }

    [TestMethod]
    public void TextIsSmallerThanZlib()
    {
        byte[] data = ContentStream(3000);
        byte[] exhaustive = RoundTrip(data);
        byte[] zlib = StreamCodec.Deflate(data, Compat.MaximumCompression);

        Assert.IsLessThan(zlib.Length, exhaustive.Length);
    }

    [TestMethod]
    public void RandomDataIsStored()
    {
        var data = new byte[200_000];
        new Random(9).NextBytes(data);

        byte[] compressed = RoundTrip(data);

        // Stored blocks: five bytes of framing per 65535, plus the zlib header and checksum.
        Assert.IsLessThanOrEqualTo(data.Length + 32, compressed.Length);
    }

    [TestMethod]
    public void LongRunsAndDistantRepeatsRoundTrip()
    {
        // Runs longer than one match, repeats at the window's edge and beyond it.
        var data = new byte[300_000];
        var random = new Random(4);
        for (int i = 0; i < 40_000; i++) data[i] = (byte)random.Next(256);
        data.AsSpan(0, 2000).CopyTo(data.AsSpan(32_768));
        data.AsSpan(1000, 3000).CopyTo(data.AsSpan(40_000 + 32_768 + 1));
        for (int i = 80_000; i < 200_000; i++) data[i] = (byte)(i % 3 == 0 ? 0 : 255);
        for (int i = 200_000; i < data.Length; i++) data[i] = (byte)random.Next(256);

        RoundTrip(data);
    }

    [TestMethod]
    public void MixedContentSplitsIntoBlocks()
    {
        // Text, then image-like noise, then text again: one set of codes would fit neither well.
        byte[] text = ContentStream(1500);
        var noise = new byte[60_000];
        var random = new Random(2);
        for (int i = 0; i < noise.Length; i++) noise[i] = (byte)(128 + random.Next(-20, 21));
        byte[] data = [.. text, .. noise, .. ContentStream(1500, seed: 8)];

        byte[] exhaustive = RoundTrip(data);

        Assert.IsLessThanOrEqualTo(StreamCodec.Deflate(data, Compat.MaximumCompression).Length, exhaustive.Length);
    }

    [TestMethod]
    public void InputsAcrossMasterBlocksRoundTrip()
    {
        // Over one master block, with matches that reach back across the boundary.
        byte[] text = ContentStream(25_000);
        Assert.IsGreaterThan(1 << 20, text.Length);

        RoundTrip(text);
    }

    [TestMethod]
    public void SkewedFrequenciesStayWithinFifteenBits()
    {
        // Fibonacci counts would give an unlimited Huffman code a depth near the alphabet size.
        var counts = new long[30];
        long a = 1, b = 1;
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = a;
            (a, b) = (b, a + b);
        }

        byte[] lengths = ExhaustiveDeflate.CodeLengths(counts, 15);
        Assert.IsTrue(lengths.All(l => l is >= 1 and <= 15));

        // Kraft equality: a complete prefix code.
        double kraft = lengths.Sum(l => Math.Pow(2, -l));
        Assert.AreEqual(1.0, kraft, 1e-12);

        // The same skew in real data: literals with Fibonacci frequencies.
        var data = new List<byte>();
        a = 1;
        b = 1;
        for (int symbol = 0; symbol < 25; symbol++)
        {
            for (long k = 0; k < a; k++) data.Add((byte)(symbol * 7));
            (a, b) = (b, a + b);
        }

        // Shuffle so runs do not turn into matches.
        var random = new Random(6);
        byte[] shuffled = [.. data.OrderBy(_ => random.Next())];
        RoundTrip(shuffled);
    }

    [TestMethod]
    public void OneOrTwoSymbolAlphabetsGetValidCodes()
    {
        // Inflaters want at least two codes; the lowest unused symbols fill in.
        CollectionAssert.AreEqual(new byte[] { 1, 0, 1, 0 }, ExhaustiveDeflate.CodeLengths([0, 0, 5, 0], 15));
        CollectionAssert.AreEqual(new byte[] { 1, 1, 0, 0 }, ExhaustiveDeflate.CodeLengths([0, 0, 0, 0], 15));
        CollectionAssert.AreEqual(new byte[] { 2, 2, 1 }, ExhaustiveDeflate.CodeLengths([1, 1, 5], 15));
    }

    [TestMethod]
    public void OversizedInputIsRefused()
    {
        Assert.IsNull(ExhaustiveDeflate.TryCompress(new byte[ExhaustiveDeflate.MaxInput + 1]));
    }

    [TestMethod]
    public void CancellationStopsTheSearch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => ExhaustiveDeflate.TryCompress(ContentStream(100), ct: cts.Token));
    }

    [TestMethod]
    public void OptimizerWritesSmallerVerifiedFiles()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R>>");
        b.SetStream(4, "", ContentStream(4000));
        byte[] input = b.Build();
        var options = new PdfOptimizerOptions { MinSavingPercent = 0 };

        (byte[] maximum, PdfOptimizationResult m) = PdfOptimizer.Optimize(input, options);
        (byte[] exhaustive, PdfOptimizationResult e) = PdfOptimizer.Optimize(input, options with { Compression = CompressionEffort.Exhaustive });

        Assert.AreEqual(OptimizationDisposition.Optimized, m.Disposition, m.Reason);
        Assert.AreEqual(OptimizationDisposition.Optimized, e.Disposition, e.Reason);
        Assert.IsLessThan(maximum.Length, exhaustive.Length);
        Assert.IsNull(PdfEquivalence.FindDifference(input, exhaustive));
    }

    [TestMethod]
    public void StreamCodecKeepsTheSmallerEncoding()
    {
        byte[] data = ContentStream(500);
        byte[] chosen = StreamCodec.Deflate(data, Compat.MaximumCompression, exhaustive: true);

        Assert.IsLessThanOrEqualTo(StreamCodec.Deflate(data, Compat.MaximumCompression).Length, chosen.Length);
        CollectionAssert.AreEqual(data, Inflate(chosen));
    }
}
