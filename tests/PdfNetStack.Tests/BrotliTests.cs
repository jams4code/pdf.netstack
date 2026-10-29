using System.IO.Compression;
using PdfNetStack.Fonts.OpenType;

namespace PdfNetStack.Tests;

/// <summary>
/// The managed Brotli decoder (RFC 7932) that the .NET Standard 2.0 build uses for WOFF2. Stored
/// vectors from tools/make_brotli_fixtures.py run everywhere; on .NET 8 and later the decoder is
/// also checked against fresh streams from System.IO.Compression.BrotliEncoder.
/// </summary>
[TestClass]
public sealed class BrotliTests
{
    private static string FixturePath(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    private static readonly Lazy<byte[]> SharedDictionary = new(() =>
    {
        using FileStream file = File.OpenRead(FixturePath("Brotli", "BrotliDictionary.deflate"));
        return ManagedBrotli.Inflate(file);
    });

    internal static byte[] Dictionary => SharedDictionary.Value;

    private static byte[] Decode(byte[] compressed, int length) => new ManagedBrotli(compressed, length, Dictionary).Decompress();

    /// <summary>The CRC-32 of RFC 7932 Appendix C.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            uint c = (crc ^ b) & 0xFF;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            crc = c ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>Bytes that do not compress, the generator of tools/make_brotli_fixtures.py.</summary>
    internal static byte[] Noise(int length, uint seed = 20261029)
    {
        var bytes = new byte[length];
        uint state = seed;
        for (int i = 0; i < length; i++)
        {
            state = (state * 1103515245 + 12345) & 0x7FFFFFFF;
            bytes[i] = (byte)(state >> 16);
        }

        return bytes;
    }

    /// <summary>A Brotli stream of uncompressed meta-blocks (RFC 7932 section 11.1), for crafting inputs.</summary>
    internal static byte[] Stored(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>();
        ulong bits = 0;
        int count = 0;
        void Write(int value, int n)
        {
            bits |= (ulong)value << count;
            count += n;
            while (count >= 8)
            {
                output.Add((byte)bits);
                bits >>= 8;
                count -= 8;
            }
        }

        void Align()
        {
            if (count > 0) Write(0, 8 - count);
        }

        Write(0, 1); // WBITS 16
        for (int start = 0; start < data.Length; start += 65536)
        {
            int length = Math.Min(65536, data.Length - start);
            Write(0, 1); // ISLAST
            Write(0, 2); // MNIBBLES 4
            Write(length - 1, 16);
            Write(1, 1); // ISUNCOMPRESSED
            Align();
            for (int i = 0; i < length; i++) output.Add(data[start + i]);
        }

        Write(1, 1); // ISLAST
        Write(1, 1); // ISLASTEMPTY
        Align();
        return [.. output];
    }

    [TestMethod]
    public void DictionaryMatchesTheRfc()
    {
        byte[] dictionary = Dictionary;
        Assert.HasCount(122784, dictionary);

        // RFC 7932 Appendix A gives the CRC-32; google/brotli's dictionary.bin has this SHA-256.
        Assert.AreEqual(0x5136CB04u, Crc32(dictionary));
        Assert.AreEqual("20E42EB1B511C21806D4D227D07E5DD06877D8CE7B3A817F378F313653F35C70", Compat.ToHex(Compat.Sha256(dictionary)));
#if !NET
        // The .NET Standard 2.0 library embeds its own copy.
        CollectionAssert.AreEqual(dictionary, ManagedBrotli.Dictionary);
#endif
    }

    [TestMethod]
    public void TablesMatchTheRfc()
    {
        // Appendix B: prefix, a zero, the transform number, suffix, a zero, for all 121 transforms.
        var bytes = new List<byte>();
        foreach ((string prefix, int type, string suffix) in ManagedBrotli.Transforms)
        {
            bytes.AddRange(prefix.Select(c => (byte)c));
            bytes.Add(0);
            bytes.Add((byte)type);
            bytes.AddRange(suffix.Select(c => (byte)c));
            bytes.Add(0);
        }

        Assert.HasCount(121, ManagedBrotli.Transforms);
        Assert.HasCount(648, bytes);
        Assert.AreEqual(0x3D965F81u, Crc32([.. bytes]));

        // Section 7.1: the context lookup tables.
        Assert.AreEqual(0x8E91EFB7u, Crc32(ManagedBrotli.Lut0));
        Assert.AreEqual(0xD01A32F4u, Crc32(ManagedBrotli.Lut1));
        Assert.AreEqual(0x0DD7A0D6u, Crc32([.. Enumerable.Range(0, 256).Select(b => (byte)ManagedBrotli.ContextSigned(b))]));
    }

    private static IEnumerable<(string Name, byte[] Expected)> StoredVectors()
    {
        byte[] sample = File.ReadAllBytes(FixturePath("Brotli", "sample.html"));
        foreach (string path in Directory.GetFiles(FixturePath("Brotli"), "*.br").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            byte[] expected = name.StartsWith("sample.html", StringComparison.Ordinal) ? sample
                : name.StartsWith("Test-Arabic.ttf", StringComparison.Ordinal) ? File.ReadAllBytes(FixturePath("Fonts", "Test-Arabic.ttf"))
                : Noise(20000);
            yield return (name, expected);
        }
    }

    [TestMethod]
    public void StoredVectorsDecode()
    {
        int vectors = 0;
        foreach ((string name, byte[] expected) in StoredVectors())
        {
            byte[] compressed = File.ReadAllBytes(FixturePath("Brotli", name));
            var decoder = new ManagedBrotli(compressed, expected.Length, Dictionary);
            CollectionAssert.AreEqual(expected, decoder.Decompress(), name);
            CollectionAssert.AreEqual(expected, Brotli.Decompress(compressed, expected.Length), name);

            // English text at the higher qualities uses dictionary words, transformed ones too;
            // noise goes into stored meta-blocks.
            if (name is "sample.html.q11w22.br" or "sample.html.q5w18.br")
            {
                Assert.IsGreaterThan(0, decoder.DictionaryWords, name);
                Assert.IsGreaterThan(0, decoder.TransformedWords, name);
            }

            if (name.StartsWith("random", StringComparison.Ordinal)) Assert.IsGreaterThan(0, decoder.StoredBlocks, name);
            vectors++;
        }

        Assert.AreEqual(7, vectors);
    }

    [TestMethod]
    public void StoredBlocksAndEmptyStreamsDecode()
    {
        byte[] data = Noise(150000, 7);
        CollectionAssert.AreEqual(data, Decode(Stored(data), data.Length));
        Assert.IsEmpty(Decode(Stored([]), 0));

        // An empty stream from the reference encoder: WBITS, then ISLAST and ISLASTEMPTY.
        Assert.IsEmpty(Decode([0x06], 0));
    }

    [TestMethod]
    public void WrongLengthsAreRejected()
    {
        byte[] sample = File.ReadAllBytes(FixturePath("Brotli", "sample.html"));
        byte[] compressed = File.ReadAllBytes(FixturePath("Brotli", "sample.html.q11w22.br"));
        Assert.ThrowsExactly<InvalidDataException>(() => Decode(compressed, sample.Length - 1));
        Assert.ThrowsExactly<InvalidDataException>(() => Decode(compressed, sample.Length + 1));
        Assert.ThrowsExactly<InvalidDataException>(() => Brotli.Decompress(compressed, sample.Length - 1));
        Assert.ThrowsExactly<InvalidDataException>(() => Brotli.Decompress(compressed, sample.Length + 1));
    }

    [TestMethod]
    public void TruncatedStreamsAreRejected()
    {
        foreach ((string name, byte[] expected) in StoredVectors())
        {
            byte[] compressed = File.ReadAllBytes(FixturePath("Brotli", name));
            for (int length = 0; length < compressed.Length; length += 1 + length / 64)
            {
                byte[] cut = compressed.AsSpan(0, length).ToArray();
                Assert.ThrowsExactly<InvalidDataException>(() => Decode(cut, expected.Length), $"{name} cut to {length}");
            }
        }
    }

    [TestMethod]
    public void CorruptedStreamsFailCleanly()
    {
        var random = new Random(20261029);
        foreach ((string name, byte[] expected) in StoredVectors())
        {
            byte[] original = File.ReadAllBytes(FixturePath("Brotli", name));
            for (int round = 0; round < 400; round++)
            {
                byte[] data = (byte[])original.Clone();
                int flips = 1 + random.Next(4);
                for (int k = 0; k < flips; k++) data[random.Next(Math.Min(data.Length, 64 + random.Next(data.Length)))] ^= (byte)(1 << random.Next(8));
                try
                {
                    Assert.HasCount(expected.Length, Decode(data, expected.Length));
                }
                catch (InvalidDataException)
                {
                }
                catch (Exception e) when (e is not AssertFailedException)
                {
                    Assert.Fail($"{name} round {round}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                }
            }
        }
    }

#if NET
    private static IEnumerable<(string Name, byte[] Data)> EncoderInputs()
    {
        yield return ("sample", File.ReadAllBytes(FixturePath("Brotli", "sample.html")));
        yield return ("font", File.ReadAllBytes(FixturePath("Fonts", "Test-Arabic.ttf")));
        yield return ("noise", Noise(70000));
        yield return ("empty", []);
        yield return ("one byte", [42]);
        yield return ("runs", [.. Enumerable.Range(0, 200000).Select(i => (byte)(i / 5000 % 3 == 0 ? 'a' : i % 251))]);
    }

    private static byte[] Encode(byte[] data, int quality, int window)
    {
        var output = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length) + 16];
        Assert.IsTrue(BrotliEncoder.TryCompress(data, output, out int written, quality, window));
        return output.AsSpan(0, written).ToArray();
    }

    [TestMethod]
    public void DecodesEveryQualityAndWindowOfTheBuiltInEncoder()
    {
        int words = 0;
        foreach ((string name, byte[] data) in EncoderInputs())
        {
            for (int quality = 0; quality <= 11; quality++)
            {
                foreach (int window in new[] { 10, 16, 22, 24 })
                {
                    var decoder = new ManagedBrotli(Encode(data, quality, window), data.Length, Dictionary);
                    CollectionAssert.AreEqual(data, decoder.Decompress(), $"{name} q{quality} w{window}");
                    words += decoder.DictionaryWords;
                }
            }
        }

        Assert.IsGreaterThan(0, words);
    }

    [TestMethod]
    public void DecodesALargeText()
    {
        // The Unicode line breaking test file: several hundred kilobytes of text and numbers.
        using var gzip = new GZipStream(File.OpenRead(FixturePath("Unicode", "LineBreakTest.txt.gz")), CompressionMode.Decompress);
        using var text = new MemoryStream();
        gzip.CopyTo(text);
        byte[] data = text.ToArray();
        foreach ((int quality, int window) in new[] { (1, 18), (6, 22), (11, 24) })
        {
            CollectionAssert.AreEqual(data, Decode(Encode(data, quality, window), data.Length), $"q{quality} w{window}");
        }
    }
#endif
}
