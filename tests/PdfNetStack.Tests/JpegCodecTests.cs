using PdfNetStack.Imaging;

namespace PdfNetStack.Tests;

/// <summary>
/// The coefficient codec against libjpeg-turbo output (tools/make_jpeg_fixtures.py). Fixtures in
/// one group hold the same coefficients in different entropy codings, so agreement between them
/// checks the decoder against libjpeg rather than against itself.
/// </summary>
[TestClass]
public sealed class JpegCodecTests
{
    private const long Limit = 64 << 20;

    private static readonly string[][] Groups =
    [
        ["ycc420-baseline.jpg", "ycc420-optimized.jpg", "ycc420-progressive.jpg", "ycc420-restart.jpg"],
        ["ycc422-baseline.jpg", "ycc422-optimized.jpg", "ycc422-progressive.jpg", "ycc422-restart.jpg"],
        ["ycc444-baseline.jpg", "ycc444-optimized.jpg", "ycc444-progressive.jpg", "ycc444-restart.jpg"],
        ["gray-baseline.jpg", "gray-progressive.jpg"],
        ["cmyk-baseline.jpg", "cmyk-progressive.jpg"],
    ];

    private static readonly string[] All = [.. Groups.SelectMany(g => g), "rgb-baseline.jpg", "photo-q95.jpg"];

    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpeg", name));

    private static byte[] Lossless(byte[] jpeg, int components)
    {
        byte[]? output = JpegTranscoder.TryTranscode(jpeg, null, null, components, Limit, out bool requantized);
        Assert.IsNotNull(output);
        Assert.IsFalse(requantized);
        return output;
    }

    private static int Components(byte[] jpeg)
    {
        using JpegImage image = JpegReader.Read(jpeg, Limit);
        return image.Components.Length;
    }

    [TestMethod]
    public void EveryEncodingOfOneImageDecodesToTheSameCoefficients()
    {
        foreach (string[] group in Groups)
        {
            byte[] reference = Fixture(group[0]);
            foreach (string other in group.Skip(1))
            {
                Assert.IsNull(JpegEquivalence.FindDifference(reference, Fixture(other), allowRequantization: false, Limit), other);
            }
        }
    }

    [TestMethod]
    public void ReaderReportsFrameLayout()
    {
        using (JpegImage image = JpegReader.Read(Fixture("ycc420-progressive.jpg"), Limit))
        {
            Assert.AreEqual(JpegCoding.Progressive, image.Coding);
            Assert.AreEqual(61, image.Width);
            Assert.AreEqual(45, image.Height);
            Assert.HasCount(3, image.Components);
            Assert.AreEqual(2, image.Components[0].Horizontal);
            Assert.AreEqual(2, image.Components[0].Vertical);
            Assert.AreEqual(8, image.Components[0].BlocksPerLine);
            Assert.AreEqual(6, image.Components[0].BlocksPerColumn);
            Assert.AreEqual(4, image.Components[1].BlocksPerLine);
            Assert.AreEqual(3, image.Components[1].BlocksPerColumn);
            Assert.IsTrue(image.HasJfif);
            Assert.IsNull(image.AdobeTransform);
        }

        using (JpegImage cmyk = JpegReader.Read(Fixture("cmyk-baseline.jpg"), Limit))
        {
            Assert.HasCount(4, cmyk.Components);
            Assert.AreEqual(0, cmyk.AdobeTransform);
        }
    }

    [TestMethod]
    public void ScaledTablesMatchLibjpeg()
    {
        // Pillow passes quality straight to libjpeg's jpeg_set_quality.
        using JpegImage q75 = JpegReader.Read(Fixture("ycc420-baseline.jpg"), Limit);
        CollectionAssert.AreEqual(JpegQuantization.ScaledTable(75, chrominance: false), q75.Components[0].Quantization);
        CollectionAssert.AreEqual(JpegQuantization.ScaledTable(75, chrominance: true), q75.Components[1].Quantization);

        using JpegImage q80 = JpegReader.Read(Fixture("gray-baseline.jpg"), Limit);
        CollectionAssert.AreEqual(JpegQuantization.ScaledTable(80, chrominance: false), q80.Components[0].Quantization);
    }

    [TestMethod]
    public void LosslessTranscodeKeepsEveryCoefficient()
    {
        foreach (string name in All)
        {
            byte[] source = Fixture(name);
            byte[] output = Lossless(source, Components(source));
            Assert.IsNull(JpegEquivalence.FindDifference(source, output, allowRequantization: false, Limit), name);
            using JpegImage written = JpegReader.Read(output, Limit);
            Assert.AreEqual(JpegCoding.Baseline, written.Coding, name);
        }
    }

    [TestMethod]
    public void LosslessTranscodeShrinksStandardTables()
    {
        foreach (string name in new[] { "ycc420-baseline.jpg", "ycc444-restart.jpg", "gray-baseline.jpg", "cmyk-baseline.jpg", "photo-q95.jpg" })
        {
            byte[] source = Fixture(name);
            Assert.IsLessThan(source.Length, Lossless(source, Components(source)).Length, name);
        }
    }

    [TestMethod]
    public void TranscodingIsDeterministicAndIdempotent()
    {
        byte[] source = Fixture("ycc422-progressive.jpg");
        byte[] once = Lossless(source, 3);
        CollectionAssert.AreEqual(once, Lossless(source, 3));
        CollectionAssert.AreEqual(once, Lossless(once, 3));
    }

    [TestMethod]
    public void ColorSignallingIsCarriedOver()
    {
        byte[] cmyk = Lossless(Fixture("cmyk-progressive.jpg"), 4);
        using (JpegImage image = JpegReader.Read(cmyk, Limit))
        {
            Assert.AreEqual(0, image.AdobeTransform);
            Assert.HasCount(4, image.Components);
        }

        byte[] rgb = Lossless(Fixture("rgb-baseline.jpg"), 3);
        using (JpegImage image = JpegReader.Read(rgb, Limit))
        {
            Assert.AreEqual(0, image.AdobeTransform);
            CollectionAssert.AreEqual(new[] { (int)'R', 'G', 'B' }, image.Components.Select(c => c.Id).ToArray());
        }
    }

    [TestMethod]
    public void ChromaFollowsTheColorTransform()
    {
        using (JpegImage ycc = JpegReader.Read(Fixture("ycc444-baseline.jpg"), Limit))
        {
            CollectionAssert.AreEqual(new[] { false, true, true }, ycc.ChromaComponents(null));
            CollectionAssert.AreEqual(new[] { false, false, false }, ycc.ChromaComponents(0), "PDF /ColorTransform 0 wins");
        }

        using (JpegImage rgb = JpegReader.Read(Fixture("rgb-baseline.jpg"), Limit))
        {
            CollectionAssert.AreEqual(new[] { false, false, false }, rgb.ChromaComponents(null));
        }

        using (JpegImage cmyk = JpegReader.Read(Fixture("cmyk-baseline.jpg"), Limit))
        {
            CollectionAssert.AreEqual(new[] { false, false, false, false }, cmyk.ChromaComponents(null));
        }
    }

    [TestMethod]
    public void RequantizationShrinksAndIsProvable()
    {
        byte[] source = Fixture("photo-q95.jpg");
        byte[]? output = JpegTranscoder.TryTranscode(source, 50, null, 3, Limit, out bool requantized);

        Assert.IsNotNull(output);
        Assert.IsTrue(requantized);
        Assert.IsLessThan(source.Length * 6 / 10, output.Length);
        Assert.IsNull(JpegEquivalence.FindDifference(source, output, allowRequantization: true, Limit));
        Assert.AreEqual("JPEG quantization changed", JpegEquivalence.FindDifference(source, output, allowRequantization: false, Limit));

        using JpegImage a = JpegReader.Read(source, Limit);
        using JpegImage b = JpegReader.Read(output, Limit);
        for (int i = 0; i < 3; i++)
        {
            ushort[] target = JpegQuantization.ScaledTable(50, chrominance: i > 0);
            for (int k = 0; k < 64; k++)
            {
                Assert.AreEqual(Math.Max(a.Components[i].Quantization[k], target[k]), b.Components[i].Quantization[k]);
            }
        }
    }

    [TestMethod]
    public void QualityAtOrAboveTheSourceChangesNothing()
    {
        foreach (int quality in new[] { 75, 90, 100 })
        {
            byte[] source = Fixture("ycc420-baseline.jpg");
            byte[]? output = JpegTranscoder.TryTranscode(source, quality, null, 3, Limit, out bool requantized);
            Assert.IsNotNull(output);
            Assert.IsFalse(requantized, quality.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsNull(JpegEquivalence.FindDifference(source, output, allowRequantization: false, Limit));
        }
    }

    [TestMethod]
    public void TamperedCoefficientIsDetected()
    {
        byte[] source = Fixture("ycc420-baseline.jpg");
        using JpegImage image = JpegReader.Read(source, Limit);
        image.Components[1].Block(1, 1)[5] += 1;
        byte[]? tampered = JpegWriter.TryWrite(image, source.Length);
        Assert.IsNotNull(tampered);
        StringAssert.StartsWith(JpegEquivalence.FindDifference(source, tampered, allowRequantization: true, Limit), "JPEG coefficient differs in component 1");
    }

    [TestMethod]
    public void CoefficientsBeyondEightBitCodingAreRefused()
    {
        byte[] source = Fixture("gray-baseline.jpg");
        using JpegImage image = JpegReader.Read(source, Limit);
        image.Components[0].Block(0, 0)[1] = 1024;
        Assert.IsNull(JpegWriter.TryWrite(image, source.Length));
    }

    [TestMethod]
    public void RequantizeRoundsHalfAwayFromZero()
    {
        Assert.AreEqual(4, JpegQuantization.Requantize(7, 10, 20));
        Assert.AreEqual(-4, JpegQuantization.Requantize(-7, 10, 20));
        Assert.AreEqual(3, JpegQuantization.Requantize(5, 10, 20));
        Assert.AreEqual(0, JpegQuantization.Requantize(1, 4, 9));
        Assert.AreEqual(9, JpegQuantization.Requantize(9, 7, 7));
    }

    [TestMethod]
    public void UnsupportedCodingProcessesAreLeftAlone()
    {
        byte[] source = Fixture("gray-baseline.jpg");
        int sof = IndexOfMarker(source, 0xC0);

        byte[] arithmetic = (byte[])source.Clone();
        arithmetic[sof + 1] = 0xC9;
        Assert.IsNull(JpegTranscoder.TryTranscode(arithmetic, null, null, 1, Limit, out _));

        byte[] twelveBit = (byte[])source.Clone();
        twelveBit[sof + 4] = 12;
        Assert.IsNull(JpegTranscoder.TryTranscode(twelveBit, null, null, 1, Limit, out _));

        Assert.IsNull(JpegTranscoder.TryTranscode(source, null, null, 3, Limit, out _), "color space mismatch");
        Assert.IsNull(JpegTranscoder.TryTranscode(source, null, null, 1, 1024, out _), "decode limit");
        Assert.IsNull(JpegTranscoder.TryTranscode("not a jpeg"u8.ToArray(), null, null, 1, Limit, out _));
    }

    [TestMethod]
    public void TruncatedEntropyDataIsRefused()
    {
        byte[] source = Fixture("ycc420-baseline.jpg");
        int sos = IndexOfMarker(source, 0xDA);
        for (int cut = sos + 20; cut < source.Length - 4; cut += 7)
        {
            Assert.IsNull(JpegTranscoder.TryTranscode(source.AsSpan(0, cut), null, null, 3, Limit, out _), cut.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public void MutatedInputNeverThrowsAndAnyOutputIsEquivalent()
    {
        var random = new Random(20261028);
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("PDFNETSTACK_FUZZ_ITERATIONS"), out int n) ? n : 1500;
        foreach (string name in All.Where(n => !n.StartsWith("photo", StringComparison.Ordinal)))
        {
            byte[] source = Fixture(name);
            int components = Components(source);
            for (int i = 0; i < iterations / All.Length + 1; i++)
            {
                byte[] mutated = (byte[])source.Clone();
                for (int flips = random.Next(1, 4); flips > 0; flips--) mutated[random.Next(2, mutated.Length)] = (byte)random.Next(256);
                byte[]? output = JpegTranscoder.TryTranscode(mutated, random.Next(2) == 0 ? null : random.Next(1, 101), null, components, Limit, out _);
                if (output is not null) Assert.IsNull(JpegEquivalence.FindDifference(mutated, output, allowRequantization: true, Limit));
            }
        }
    }

    [TestMethod]
    public void OptimalHuffmanCodesAreLengthLimitedAndComplete()
    {
        // Fibonacci frequencies force a 30 level tree that must be cut down to 16 bits.
        var frequencies = new long[256];
        long a = 1, b = 1;
        for (int i = 0; i < 30; i++)
        {
            frequencies[i] = a;
            (a, b) = (b, a + b);
        }

        JpegHuffmanCode code = JpegHuffmanCode.Optimal(frequencies);
        Assert.HasCount(30, code.Symbols);
        double kraft = 0;
        for (int length = 1; length <= 16; length++) kraft += code.Counts[length - 1] / Math.Pow(2, length);
        Assert.IsLessThan(1.0, kraft, "the all ones code stays unused");
        Assert.IsGreaterThan(1.0 - Math.Pow(2, -16) - 1e-12, kraft);

        // The codes decode back through the table built from the same DHT data.
        var table = new JpegHuffmanTable(code.Counts, code.Symbols);
        foreach (byte symbol in code.Symbols)
        {
            int peek = code.Codes[symbol] << (16 - code.Lengths[symbol]);
            Assert.AreEqual(code.Lengths[symbol], table.Decode(peek, out int decoded));
            Assert.AreEqual(symbol, decoded);
        }

        var single = new long[256];
        single[7] = 42;
        JpegHuffmanCode one = JpegHuffmanCode.Optimal(single);
        Assert.AreEqual(1, one.Lengths[7]);
    }

    internal static int IndexOfMarker(byte[] data, byte marker)
    {
        for (int i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == marker) return i;
        }

        throw new InvalidOperationException("Marker not found.");
    }
}
