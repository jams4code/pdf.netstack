using System.IO.Compression;
#if NET
using System.Buffers;
#endif

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Brotli decompression (RFC 7932) for WOFF2. Modern .NET has a decoder built in; .NET Standard
/// 2.0 has none and uses <see cref="ManagedBrotli"/>.
/// </summary>
internal static class Brotli
{
    /// <summary>
    /// First output buffer for a stream: the declared length is only a claim, so a few bytes of
    /// damaged input do not get a large buffer. It grows with what the stream really produces.
    /// </summary>
    internal static int InitialCapacity(int inputLength, int length) => (int)Math.Min(length, 4L * inputLength + 65536);

    /// <summary>Decompresses a stream that must expand to exactly <paramref name="length"/> bytes.</summary>
    /// <exception cref="InvalidDataException">The data is damaged, truncated or of another length.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int length)
    {
#if NET
        var output = new byte[InitialCapacity(input.Length, length)];
        using var decoder = new BrotliDecoder();
        int consumed = 0, written = 0;
        Span<byte> extra = stackalloc byte[1];
        while (true)
        {
            OperationStatus status = decoder.Decompress(input.Slice(consumed), output.AsSpan(written), out int read, out int wrote);
            consumed += read;
            written += wrote;
            if (status == OperationStatus.Done) break;
            if (status != OperationStatus.DestinationTooSmall) throw new InvalidDataException("Brotli data is damaged or truncated.");
            if (written < length)
            {
                if (output.Length == length) throw new InvalidDataException("Brotli data is damaged.");
                Array.Resize(ref output, (int)Math.Min(length, 2L * output.Length));
                continue;
            }

            // The output is full, but the stream may still end with an empty last meta-block.
            status = decoder.Decompress(input.Slice(consumed), extra, out _, out int more);
            if (more != 0) throw new InvalidDataException("Brotli data is longer than declared.");
            if (status != OperationStatus.Done) throw new InvalidDataException("Brotli data is damaged or truncated.");
            break;
        }

        if (written != length) throw new InvalidDataException("Brotli data is shorter than declared.");
        return output;
#else
        return new ManagedBrotli(input.ToArray(), length, ManagedBrotli.Dictionary).Decompress();
#endif
    }
}

/// <summary>
/// A Brotli decoder written from RFC 7932. The whole output is one buffer, so backward references
/// index it directly and never need a ring buffer. Every count, length and distance from the
/// stream is checked; damaged data raises <see cref="InvalidDataException"/>.
/// </summary>
/// <remarks>
/// The static dictionary is the data of RFC 7932 Appendix A, byte for byte the
/// c/common/dictionary.bin of google/brotli (MIT license). It ships raw deflate compressed as
/// BrotliDictionary.deflate, embedded in the .NET Standard 2.0 build only. The transform and
/// context tables below are transcribed from the RFC; tests check them against its CRC-32 values.
/// </remarks>
internal sealed class ManagedBrotli
{
    /// <summary>Size of the static dictionary (RFC 7932 Appendix A).</summary>
    public const int DictionarySize = 122784;

    // RFC 7932 section 3.5: order of the code length code lengths.
    private static readonly byte[] CodeLengthOrder = [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // Section 5: insert and copy length codes, base and extra bits.
    private static readonly int[] InsertBase = [0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98, 130, 194, 322, 578, 1090, 2114, 6210, 22594];
    private static readonly byte[] InsertExtra = [0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 12, 14, 24];
    private static readonly int[] CopyBase = [2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 18, 22, 30, 38, 54, 70, 102, 134, 198, 326, 582, 1094, 2118];
    private static readonly byte[] CopyExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24];

    // Section 5: the insert and copy code ranges of each 64 symbol cell of the command alphabet.
    private static readonly byte[] CellInsert = [0, 0, 0, 0, 8, 8, 0, 16, 8, 16, 16];
    private static readonly byte[] CellCopy = [0, 8, 0, 8, 0, 8, 16, 0, 16, 8, 16];

    // Section 6: block count codes, base and extra bits.
    private static readonly int[] BlockCountBase =
        [1, 5, 9, 13, 17, 25, 33, 41, 49, 65, 81, 97, 113, 145, 177, 209, 241, 305, 369, 497, 753, 1265, 2289, 4337, 8433, 16625];
    private static readonly byte[] BlockCountExtra = [2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 7, 8, 9, 10, 11, 12, 13, 24];

    // Section 7.1: context ID lookup for the UTF8 mode. Signed mode uses ContextSigned.
    internal static readonly byte[] Lut0 =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 0, 0, 4, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        8, 12, 16, 12, 12, 20, 12, 16, 24, 28, 12, 12, 32, 12, 36, 12,
        44, 44, 44, 44, 44, 44, 44, 44, 44, 44, 32, 32, 24, 40, 28, 12,
        12, 48, 52, 52, 52, 48, 52, 52, 52, 48, 52, 52, 52, 52, 52, 48,
        52, 52, 52, 52, 52, 48, 52, 52, 52, 52, 52, 24, 12, 28, 12, 12,
        12, 56, 60, 60, 60, 56, 60, 60, 60, 56, 60, 60, 60, 60, 60, 56,
        60, 60, 60, 60, 60, 56, 60, 60, 60, 60, 60, 24, 12, 28, 12, 0,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
        2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
        2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
        2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
    ];

    internal static readonly byte[] Lut1 =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1,
        1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 1, 1, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
    ];

    // Section 8: log2 of the number of dictionary words of each length.
    private static readonly byte[] DictionaryBits = [0, 0, 0, 0, 10, 10, 11, 11, 10, 10, 10, 10, 10, 9, 9, 8, 7, 7, 8, 7, 7, 6, 6, 5, 5];
    private static readonly int[] DictionaryOffsets = BuildDictionaryOffsets();

    /// <summary>
    /// Appendix B: the 121 word transforms as prefix, elementary transform and suffix. Transform
    /// numbers follow the appendix: 0 Identity, 1 FermentFirst, 2 FermentAll, 3 to 11 OmitFirst1
    /// to 9, 12 to 20 OmitLast1 to 9. Strings hold one byte per char.
    /// </summary>
    internal static readonly (string Prefix, int Type, string Suffix)[] Transforms =
    [
        ("", 0, ""), ("", 0, " "), (" ", 0, " "), ("", 3, ""), ("", 1, " "), ("", 0, " the "), (" ", 0, ""), ("s ", 0, " "),
        ("", 0, " of "), ("", 1, ""), ("", 0, " and "), ("", 4, ""), ("", 12, ""), (", ", 0, " "), ("", 0, ", "), (" ", 1, " "),
        ("", 0, " in "), ("", 0, " to "), ("e ", 0, " "), ("", 0, "\""), ("", 0, "."), ("", 0, "\">"), ("", 0, "\n"), ("", 14, ""),
        ("", 0, "]"), ("", 0, " for "), ("", 5, ""), ("", 13, ""), ("", 0, " a "), ("", 0, " that "), (" ", 1, ""), ("", 0, ". "),
        (".", 0, ""), (" ", 0, ", "), ("", 6, ""), ("", 0, " with "), ("", 0, "'"), ("", 0, " from "), ("", 0, " by "), ("", 7, ""),
        ("", 8, ""), (" the ", 0, ""), ("", 15, ""), ("", 0, ". The "), ("", 2, ""), ("", 0, " on "), ("", 0, " as "), ("", 0, " is "),
        ("", 18, ""), ("", 12, "ing "), ("", 0, "\n\t"), ("", 0, ":"), (" ", 0, ". "), ("", 0, "ed "), ("", 11, ""), ("", 9, ""),
        ("", 17, ""), ("", 0, "("), ("", 1, ", "), ("", 19, ""), ("", 0, " at "), ("", 0, "ly "), (" the ", 0, " of "), ("", 16, ""),
        ("", 20, ""), (" ", 1, ", "), ("", 1, "\""), (".", 0, "("), ("", 2, " "), ("", 1, "\">"), ("", 0, "=\""), (" ", 0, "."),
        (".com/", 0, ""), (" the ", 0, " of the "), ("", 1, "'"), ("", 0, ". This "), ("", 0, ","), (".", 0, " "), ("", 1, "("), ("", 1, "."),
        ("", 0, " not "), (" ", 0, "=\""), ("", 0, "er "), (" ", 2, " "), ("", 0, "al "), (" ", 2, ""), ("", 0, "='"), ("", 2, "\""),
        ("", 1, ". "), (" ", 0, "("), ("", 0, "ful "), (" ", 1, ". "), ("", 0, "ive "), ("", 0, "less "), ("", 2, "'"), ("", 0, "est "),
        (" ", 1, "."), ("", 2, "\">"), (" ", 0, "='"), ("", 1, ","), ("", 0, "ize "), ("", 2, "."), ("Â ", 0, ""), (" ", 0, ","),
        ("", 1, "=\""), ("", 2, "=\""), ("", 0, "ous "), ("", 2, ", "), ("", 1, "='"), (" ", 1, ","), (" ", 2, "=\""), (" ", 2, ", "),
        ("", 2, ","), ("", 2, "("), ("", 2, ". "), (" ", 2, "."), ("", 2, "='"), (" ", 2, ". "), (" ", 1, "=\""), (" ", 2, "='"),
        (" ", 1, "='"),
    ];

    private readonly byte[] _input;
    private readonly int _length;
    private readonly byte[] _dictionary;
    private byte[] _output;
    private readonly int[] _distances = [4, 11, 15, 16];
    private int _inputPosition;
    private ulong _bits;
    private int _bitCount;
    private int _position;

    /// <param name="input">The compressed stream.</param>
    /// <param name="length">The exact decompressed length.</param>
    /// <param name="dictionary">The static dictionary, <see cref="DictionarySize"/> bytes.</param>
    public ManagedBrotli(byte[] input, int length, byte[] dictionary)
    {
        if (dictionary.Length != DictionarySize) throw new ArgumentException("The Brotli dictionary has the wrong size.", nameof(dictionary));
        _input = input;
        _length = length;
        _output = new byte[Brotli.InitialCapacity(input.Length, length)];
        _dictionary = dictionary;
    }

    /// <summary>Grows the output for <paramref name="count"/> more bytes; callers have checked them against the length.</summary>
    private void Reserve(int count)
    {
        if (count <= _output.Length - _position) return;
        Array.Resize(ref _output, (int)Math.Min(_length, Math.Max(2L * _output.Length, (long)_position + count)));
    }

#if !NET
    private static readonly Lazy<byte[]> SharedDictionary = new(LoadDictionary);

    /// <summary>The static dictionary, from the embedded resource.</summary>
    public static byte[] Dictionary => SharedDictionary.Value;

    /// <summary>Reads the embedded copy of the dictionary, stored deflate compressed.</summary>
    public static byte[] LoadDictionary()
    {
        using Stream? stream = typeof(ManagedBrotli).Assembly.GetManifestResourceStream("PdfNetStack.BrotliDictionary.deflate")
            ?? throw new InvalidOperationException("The Brotli dictionary resource is missing.");
        return Inflate(stream);
    }
#endif

    /// <summary>Inflates a raw deflate copy of the dictionary.</summary>
    public static byte[] Inflate(Stream deflated)
    {
        using var inflate = new DeflateStream(deflated, CompressionMode.Decompress);
        var dictionary = new byte[DictionarySize];
        int read = 0;
        while (read < dictionary.Length)
        {
            int n = inflate.Read(dictionary, read, dictionary.Length - read);
            if (n == 0) throw new InvalidOperationException("The Brotli dictionary resource is truncated.");
            read += n;
        }

        return dictionary;
    }

    /// <summary>Static dictionary references decoded, for tests.</summary>
    public int DictionaryWords { get; private set; }

    /// <summary>Static dictionary references with a transform other than identity, for tests.</summary>
    public int TransformedWords { get; private set; }

    /// <summary>Uncompressed meta-blocks decoded, for tests.</summary>
    public int StoredBlocks { get; private set; }

    /// <summary>Decodes the whole stream (section 10).</summary>
    /// <exception cref="InvalidDataException">The data is damaged, truncated or of another length.</exception>
    public byte[] Decompress()
    {
        int windowBits = ReadWindowBits();
        int maxBackward = (1 << windowBits) - 16;
        while (true)
        {
            bool last = Bits(1) == 1;
            if (last && Bits(1) == 1) break;
            int nibbles = Bits(2);
            if (nibbles == 3)
            {
                SkipMetadata();
                if (last) break;
                continue;
            }

            nibbles += 4;
            int length = 0;
            for (int i = 0; i < nibbles; i++)
            {
                int nibble = Bits(4);
                if (nibble == 0 && i == nibbles - 1 && nibbles > 4) throw Damaged();
                length |= nibble << (4 * i);
            }

            length++;
            if (length > _length - _position) throw new InvalidDataException("Brotli data is longer than declared.");
            if (!last && Bits(1) == 1)
            {
                AlignToByte();
                if (length > _bitCount / 8 + _input.Length - _inputPosition) throw new InvalidDataException("Brotli data is truncated.");
                Reserve(length);
                ReadBytes(_output, _position, length);
                _position += length;
                StoredBlocks++;
                continue;
            }

            DecodeMetaBlock(_position + length, maxBackward);
            if (last) break;
        }

        AlignToByte();
        if (_position != _length) throw new InvalidDataException("Brotli data is shorter than declared.");
        return _output;
    }

    private static InvalidDataException Damaged() => new("Brotli data is damaged.");

    private void Fill()
    {
        while (_bitCount <= 56 && _inputPosition < _input.Length)
        {
            _bits |= (ulong)_input[_inputPosition++] << _bitCount;
            _bitCount += 8;
        }
    }

    /// <summary>The next <paramref name="n"/> bits, least significant first (section 1.5.1). At most 24.</summary>
    private int Bits(int n)
    {
        if (_bitCount < n)
        {
            Fill();
            if (_bitCount < n) throw new InvalidDataException("Brotli data is truncated.");
        }

        int value = (int)(_bits & ((1UL << n) - 1));
        _bits >>= n;
        _bitCount -= n;
        return value;
    }

    /// <summary>Skips to the next byte boundary. The skipped bits must be zero.</summary>
    private void AlignToByte()
    {
        if (Bits(_bitCount & 7) != 0) throw Damaged();
    }

    /// <summary>Copies whole bytes after <see cref="AlignToByte"/>: first the buffered ones, then the input.</summary>
    private void ReadBytes(byte[]? destination, int offset, int count)
    {
        while (count > 0 && _bitCount >= 8)
        {
            if (destination is not null) destination[offset] = (byte)_bits;
            offset++;
            count--;
            _bits >>= 8;
            _bitCount -= 8;
        }

        if (count > _input.Length - _inputPosition) throw new InvalidDataException("Brotli data is truncated.");
        if (destination is not null) Buffer.BlockCopy(_input, _inputPosition, destination, offset, count);
        _inputPosition += count;
    }

    /// <summary>Section 9.1: WBITS, 10 to 24.</summary>
    private int ReadWindowBits()
    {
        if (Bits(1) == 0) return 16;
        int n = Bits(3);
        if (n != 0) return 17 + n;
        n = Bits(3);
        if (n == 1) throw Damaged();
        return n != 0 ? 8 + n : 17;
    }

    /// <summary>Section 9.2: a metadata block, whose bytes are not part of the output.</summary>
    private void SkipMetadata()
    {
        if (Bits(1) != 0) throw Damaged();
        int bytes = Bits(2), length = 0;
        for (int i = 0; i < bytes; i++)
        {
            int b = Bits(8);
            if (b == 0 && i == bytes - 1 && bytes > 1) throw Damaged();
            length |= b << (8 * i);
        }

        if (bytes > 0) length++;
        AlignToByte();
        ReadBytes(null, 0, length);
    }

    /// <summary>A canonical prefix code (section 3.2), decoded through an 8 bit table with a bitwise fallback.</summary>
    private sealed class PrefixCode
    {
        private const int RootBits = 8;

        // Entry: symbol << 4 | length, or 0 when the code is longer than RootBits.
        private readonly int[] _root = new int[1 << RootBits];
        private readonly int[] _counts = new int[16];
        private readonly int[] _symbols;
        private readonly int _single = -1;

        public PrefixCode(byte[] lengths)
        {
            int used = 0;
            foreach (byte length in lengths)
            {
                _counts[length]++;
                if (length != 0) used++;
            }

            _counts[0] = 0;
            _symbols = new int[used];
            if (used == 1)
            {
                // One symbol: its code has no bits (sections 3.4 and 3.5).
                _single = Array.FindIndex(lengths, l => l != 0);
                return;
            }

            var offsets = new int[16];
            for (int length = 1; length < 15; length++) offsets[length + 1] = offsets[length] + _counts[length];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] != 0) _symbols[offsets[lengths[symbol]]++] = symbol;
            }

            // Short codes go into the root table under their bit reversed form: the stream holds
            // the first code bit in its least significant position.
            int code = 0, index = 0;
            for (int length = 1; length <= RootBits; length++)
            {
                for (int k = 0; k < _counts[length]; k++)
                {
                    int reversed = 0;
                    for (int b = 0; b < length; b++) reversed |= ((code >> b) & 1) << (length - 1 - b);
                    for (int r = reversed; r < 1 << RootBits; r += 1 << length) _root[r] = _symbols[index] << 4 | length;
                    code++;
                    index++;
                }

                code <<= 1;
            }
        }

        public int Read(ManagedBrotli reader)
        {
            if (_single >= 0) return _single;
            if (reader._bitCount < 15) reader.Fill();
            if (reader._bitCount >= RootBits)
            {
                int entry = _root[(int)(reader._bits & ((1 << RootBits) - 1))];
                if (entry != 0)
                {
                    int length = entry & 15;
                    reader._bits >>= length;
                    reader._bitCount -= length;
                    return entry >> 4;
                }
            }

            // Bit by bit, as in a canonical decoder: codes of each length follow the shorter ones.
            int code = 0, first = 0, index = 0;
            for (int length = 1; length <= 15; length++)
            {
                code |= reader.Bits(1);
                int count = _counts[length];
                if (code >= first && code - first < count) return _symbols[index + code - first];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }

            throw Damaged();
        }
    }

    /// <summary>Sections 3.4 and 3.5: a simple or complex prefix code over <paramref name="alphabet"/> symbols.</summary>
    private PrefixCode ReadPrefixCode(int alphabet)
    {
        var lengths = new byte[alphabet];
        int skip = Bits(2);
        if (skip == 1)
        {
            int alphabetBits = 0;
            while (1 << alphabetBits < alphabet) alphabetBits++;
            int count = Bits(2) + 1;
            var symbols = new int[count];
            for (int i = 0; i < count; i++)
            {
                symbols[i] = Bits(alphabetBits);
                if (symbols[i] >= alphabet || Array.IndexOf(symbols, symbols[i], 0, i) >= 0) throw Damaged();
            }

            ReadOnlySpan<byte> pattern = count switch
            {
                1 => [1],
                2 => [1, 1],
                3 => [1, 2, 2],
                _ => Bits(1) == 0 ? [2, 2, 2, 2] : [1, 2, 3, 3],
            };
            for (int i = 0; i < count; i++) lengths[symbols[i]] = pattern[i];
            return new PrefixCode(lengths);
        }

        var codeLengths = new byte[18];
        int space = 32, used = 0;
        for (int i = skip; i < 18 && space > 0; i++)
        {
            int length = ReadCodeLengthCodeLength();
            codeLengths[CodeLengthOrder[i]] = (byte)length;
            if (length == 0) continue;
            space -= 32 >> length;
            used++;
        }

        if (used == 0 || (used > 1 && space != 0)) throw Damaged();
        var codeLengthCode = new PrefixCode(codeLengths);

        int symbol = 0, previous = 8, repeat = 0, repeatLength = 0;
        space = 32768;
        while (symbol < alphabet && space > 0)
        {
            int code = codeLengthCode.Read(this);
            if (code < 16)
            {
                repeat = 0;
                lengths[symbol++] = (byte)code;
                if (code == 0) continue;
                previous = code;
                space -= 32768 >> code;
                continue;
            }

            // 16 repeats the previous non-zero length, 17 repeats zero; a repeat right after
            // another of the same kind extends it.
            int extraBits = code == 16 ? 2 : 3;
            int newLength = code == 16 ? previous : 0;
            if (repeatLength != newLength)
            {
                repeat = 0;
                repeatLength = newLength;
            }

            int old = repeat;
            if (repeat > 0) repeat = (repeat - 2) << extraBits;
            repeat += Bits(extraBits) + 3;
            int delta = repeat - old;
            if (delta > alphabet - symbol) throw Damaged();
            for (int k = 0; k < delta; k++) lengths[symbol++] = (byte)repeatLength;
            if (repeatLength != 0) space -= delta << (15 - repeatLength);
        }

        if (space != 0) throw Damaged();
        return new PrefixCode(lengths);
    }

    /// <summary>Section 3.5: the fixed code for code length code lengths, 0 to 5.</summary>
    private int ReadCodeLengthCodeLength()
    {
        if (Bits(1) == 0) return Bits(1) == 0 ? 0 : 3;
        if (Bits(1) == 0) return 4;
        if (Bits(1) == 0) return 2;
        return Bits(1) == 0 ? 1 : 5;
    }

    /// <summary>One block category (section 6): literals, insert-and-copy commands or distances.</summary>
    private sealed class BlockCategory
    {
        public int Types;
        public PrefixCode? TypeCode;
        public PrefixCode? CountCode;
        public int Type;
        public int Previous = 1;
        public int Remaining = 1 << 24;
    }

    /// <summary>NBLTYPES and NTREES (section 9.2): 1 to 256.</summary>
    private int ReadCount256()
    {
        if (Bits(1) == 0) return 1;
        int n = Bits(3);
        return (1 << n) + 1 + Bits(n);
    }

    private int ReadBlockCount(PrefixCode code)
    {
        int symbol = code.Read(this);
        return BlockCountBase[symbol] + Bits(BlockCountExtra[symbol]);
    }

    private BlockCategory ReadBlockCategory()
    {
        var category = new BlockCategory { Types = ReadCount256() };
        if (category.Types < 2) return category;
        category.TypeCode = ReadPrefixCode(category.Types + 2);
        category.CountCode = ReadPrefixCode(26);
        category.Remaining = ReadBlockCount(category.CountCode);
        return category;
    }

    /// <summary>Section 6: the next block type and count of a category whose count ran out.</summary>
    private void SwitchBlock(BlockCategory category)
    {
        if (category.TypeCode is null || category.CountCode is null)
        {
            // One block type: the count is unbounded.
            category.Remaining = 1 << 24;
            return;
        }

        int symbol = category.TypeCode.Read(this);
        int type = symbol == 0 ? category.Previous : symbol == 1 ? category.Type + 1 : symbol - 2;
        if (type >= category.Types) type -= category.Types;
        category.Previous = category.Type;
        category.Type = type;
        category.Remaining = ReadBlockCount(category.CountCode);
    }

    /// <summary>Section 7.3: a context map of <paramref name="size"/> entries, each below <paramref name="trees"/>.</summary>
    private byte[] ReadContextMap(int size, int trees)
    {
        var map = new byte[size];
        if (trees < 2) return map;
        int runLengthMax = Bits(1) == 1 ? Bits(4) + 1 : 0;
        PrefixCode code = ReadPrefixCode(trees + runLengthMax);
        for (int i = 0; i < size;)
        {
            int symbol = code.Read(this);
            if (symbol == 0)
            {
                map[i++] = 0;
            }
            else if (symbol <= runLengthMax)
            {
                int run = (1 << symbol) + Bits(symbol);
                if (run > size - i) throw Damaged();
                i += run;
            }
            else
            {
                map[i++] = (byte)(symbol - runLengthMax);
            }
        }

        if (Bits(1) == 1)
        {
            // Inverse move-to-front transform.
            var mtf = new byte[256];
            for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
            for (int i = 0; i < size; i++)
            {
                int index = map[i];
                byte value = mtf[index];
                map[i] = value;
                for (; index > 0; index--) mtf[index] = mtf[index - 1];
                mtf[0] = value;
            }
        }

        foreach (byte value in map)
        {
            if (value >= trees) throw Damaged();
        }

        return map;
    }

    /// <summary>Sections 9.2 and 9.3: a compressed meta-block that ends at output position <paramref name="end"/>.</summary>
    private void DecodeMetaBlock(int end, int maxBackward)
    {
        BlockCategory literals = ReadBlockCategory(), commands = ReadBlockCategory(), distances = ReadBlockCategory();
        int postfix = Bits(2);
        int direct = Bits(4) << postfix;
        var modes = new byte[literals.Types];
        for (int i = 0; i < modes.Length; i++) modes[i] = (byte)Bits(2);

        int literalTrees = ReadCount256();
        byte[] literalMap = ReadContextMap(64 * literals.Types, literalTrees);
        int distanceTrees = ReadCount256();
        byte[] distanceMap = ReadContextMap(4 * distances.Types, distanceTrees);

        var literalCodes = new PrefixCode[literalTrees];
        for (int i = 0; i < literalTrees; i++) literalCodes[i] = ReadPrefixCode(256);
        var commandCodes = new PrefixCode[commands.Types];
        for (int i = 0; i < commands.Types; i++) commandCodes[i] = ReadPrefixCode(704);
        var distanceCodes = new PrefixCode[distanceTrees];
        for (int i = 0; i < distanceTrees; i++) distanceCodes[i] = ReadPrefixCode(16 + direct + (48 << postfix));

        while (_position < end)
        {
            if (commands.Remaining == 0) SwitchBlock(commands);
            commands.Remaining--;

            // Section 5: one symbol gives the insert and copy length codes.
            int command = commandCodes[commands.Type].Read(this);
            int cell = command >> 6;
            int insertCode = CellInsert[cell] + ((command >> 3) & 7);
            int copyCode = CellCopy[cell] + (command & 7);
            int insert = InsertBase[insertCode] + Bits(InsertExtra[insertCode]);
            int copy = CopyBase[copyCode] + Bits(CopyExtra[copyCode]);
            if (insert > end - _position) throw Damaged();
            Reserve(insert);
            byte[] output = _output;
            for (int k = 0; k < insert; k++)
            {
                if (literals.Remaining == 0) SwitchBlock(literals);
                literals.Remaining--;
                int p1 = _position > 0 ? output[_position - 1] : 0;
                int p2 = _position > 1 ? output[_position - 2] : 0;
                int context = modes[literals.Type] switch
                {
                    0 => p1 & 0x3F,
                    1 => p1 >> 2,
                    2 => Lut0[p1] | Lut1[p2],
                    _ => ContextSigned(p1) << 3 | ContextSigned(p2),
                };
                output[_position++] = (byte)literalCodes[literalMap[64 * literals.Type + context]].Read(this);
            }

            // The copy of the last command is ignored when the inserted literals end the meta-block.
            if (_position == end) break;

            int distance, distanceCode = 0;
            if (cell < 2)
            {
                distance = _distances[0];
            }
            else
            {
                if (distances.Remaining == 0) SwitchBlock(distances);
                distances.Remaining--;
                int context = copy > 4 ? 3 : copy - 2;
                distanceCode = distanceCodes[distanceMap[4 * distances.Type + context]].Read(this);
                distance = Distance(distanceCode, postfix, direct);
            }

            int maxDistance = Math.Min(maxBackward, _position);
            if (distance > maxDistance)
            {
                CopyDictionaryWord(copy, distance - maxDistance - 1, end);
                continue;
            }

            if (distanceCode != 0)
            {
                _distances[3] = _distances[2];
                _distances[2] = _distances[1];
                _distances[1] = _distances[0];
                _distances[0] = distance;
            }

            if (copy > end - _position) throw Damaged();
            Reserve(copy);
            output = _output;
            for (int k = 0; k < copy; k++, _position++) output[_position] = output[_position - distance];
        }
    }

    /// <summary>Section 7.1, the Signed context mode lookup (Lut2).</summary>
    internal static int ContextSigned(int b) => b switch
    {
        0 => 0,
        < 16 => 1,
        < 64 => 2,
        < 128 => 3,
        < 192 => 4,
        < 255 => b < 240 ? 5 : 6,
        _ => 7,
    };

    /// <summary>Section 4: a distance from its code and extra bits.</summary>
    private int Distance(int code, int postfix, int direct)
    {
        if (code < 16)
        {
            int distance = code switch
            {
                < 4 => _distances[code],
                < 10 => _distances[0] + ((code & 1) == 0 ? -1 : 1) * ((code - 2) >> 1),
                _ => _distances[1] + ((code & 1) == 0 ? -1 : 1) * ((code - 8) >> 1),
            };
            if (distance <= 0) throw Damaged();
            return distance;
        }

        if (code < 16 + direct) return code - 15;
        int c = code - direct - 16;
        int extraBits = 1 + (c >> (postfix + 1));
        int high = c >> postfix, low = c & ((1 << postfix) - 1);
        int offset = ((2 + (high & 1)) << extraBits) - 4;
        return ((offset + Bits(extraBits)) << postfix) + low + direct + 1;
    }

    /// <summary>Section 8: a static dictionary word of <paramref name="length"/> bytes, transformed.</summary>
    private void CopyDictionaryWord(int length, int wordId, int end)
    {
        if (length is < 4 or > 24) throw Damaged();
        int bits = DictionaryBits[length];
        int index = wordId & ((1 << bits) - 1);
        int transform = wordId >> bits;
        if (transform >= Transforms.Length) throw Damaged();
        (string prefix, int type, string suffix) = Transforms[transform];

        // At most 13 bytes of prefix and suffix around a 24 byte word.
        Span<byte> word = stackalloc byte[40];
        int n = 0;
        foreach (char c in prefix) word[n++] = (byte)c;
        int start = DictionaryOffsets[length] + index * length, count = length;
        if (type is >= 3 and <= 11)
        {
            int omit = Math.Min(type - 2, count);
            start += omit;
            count -= omit;
        }
        else if (type >= 12)
        {
            count -= Math.Min(type - 11, count);
        }

        _dictionary.AsSpan(start, count).CopyTo(word.Slice(n));
        if (type == 1 && count > 0)
        {
            Ferment(word.Slice(n, count), 0);
        }
        else if (type == 2)
        {
            for (int i = 0; i < count;) i += Ferment(word.Slice(n, count), i);
        }

        n += count;
        foreach (char c in suffix) word[n++] = (byte)c;
        if (n > end - _position) throw Damaged();
        Reserve(n);
        word.Slice(0, n).CopyTo(_output.AsSpan(_position));
        _position += n;
        DictionaryWords++;
        if (transform != 0) TransformedWords++;
    }

    /// <summary>Section 8: upper cases one UTF-8 character of a word; returns its length.</summary>
    private static int Ferment(Span<byte> word, int i)
    {
        if (word[i] < 192)
        {
            if (word[i] is >= 97 and <= 122) word[i] ^= 32;
            return 1;
        }

        if (word[i] < 224)
        {
            if (i + 1 < word.Length) word[i + 1] ^= 32;
            return 2;
        }

        if (i + 2 < word.Length) word[i + 2] ^= 5;
        return 3;
    }

    private static int[] BuildDictionaryOffsets()
    {
        var offsets = new int[25];
        for (int length = 4; length < 24; length++) offsets[length + 1] = offsets[length] + (length << DictionaryBits[length]);
        return offsets;
    }
}
