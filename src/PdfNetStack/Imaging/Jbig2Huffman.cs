namespace PdfNetStack.Imaging;

/// <summary>Reads bits most significant first from a segment's data; reading past its end is an error.</summary>
internal sealed class Jbig2BitReader(byte[] data, int start, int end)
{
    private long _bit = (long)start * 8;

    public byte[] Data { get; } = data;

    public int End { get; } = end;

    /// <summary>The byte holding the next bit.</summary>
    public int BytePosition => (int)(_bit >> 3);

    public int ReadBit()
    {
        if (_bit >= (long)End * 8) throw new Jbig2FormatException("Huffman coded data ends early.");
        int value = (Data[_bit >> 3] >> (7 - (int)(_bit & 7))) & 1;
        _bit++;
        return value;
    }

    /// <summary>Up to 32 bits as an unsigned value.</summary>
    public long ReadBits(int count)
    {
        long value = 0;
        for (int i = 0; i < count; i++) value = (value << 1) | (uint)ReadBit();
        return value;
    }

    /// <summary>Skips the bits left in the current byte.</summary>
    public void Align() => _bit = (_bit + 7) & ~7L;

    /// <summary>Moves to a byte, which must lie within the data.</summary>
    public void Seek(long position)
    {
        if (position < BytePosition || position > End) throw new Jbig2FormatException("Huffman coded data ends early.");
        _bit = position * 8;
    }
}

/// <summary>
/// A Huffman table (T.88, Annex B): lines of a prefix length and a range, codes assigned
/// canonically (B.3). Lower and upper range lines read a 32 bit offset; an OOB line has no value.
/// </summary>
internal sealed class Jbig2HuffmanTable
{
    private const int MaxPrefix = 32;
    private const int MaxLines = 1 << 16;

    private const byte Normal = 0;
    private const byte Lower = 1;
    private const byte Upper = 2;
    private const byte OutOfBand = 3;

    private readonly long[] _low;
    private readonly int[] _rangeLength;
    private readonly byte[] _kind;

    // Lines sorted by prefix length in table order, with the first code and line of each length.
    private readonly int[] _byLength;
    private readonly long[] _firstCode = new long[MaxPrefix + 2];
    private readonly int[] _count = new int[MaxPrefix + 2];
    private readonly int[] _start = new int[MaxPrefix + 2];
    private readonly int _maxLength;

    private Jbig2HuffmanTable(List<(long Low, int Prefix, int Range, byte Kind)> lines)
    {
        _low = [.. lines.Select(l => l.Low)];
        _rangeLength = [.. lines.Select(l => l.Range)];
        _kind = [.. lines.Select(l => l.Kind)];
        foreach ((_, int prefix, _, _) in lines)
        {
            if (prefix > MaxPrefix) throw new Jbig2FormatException("A Huffman code is too long.");
            if (prefix > 0) _count[prefix]++;
            _maxLength = Math.Max(_maxLength, prefix);
        }

        // B.3: FIRSTCODE[n] = (FIRSTCODE[n - 1] + LENCOUNT[n - 1]) * 2, LENCOUNT[0] being 0.
        int coded = 0;
        for (int length = 1; length <= _maxLength; length++)
        {
            _firstCode[length] = (_firstCode[length - 1] + (length == 1 ? 0 : _count[length - 1])) << 1;
            _start[length] = coded;
            coded += _count[length];
        }

        // Lines of one length keep their table order: codes rise with it.
        _byLength = new int[coded];
        var next = (int[])_start.Clone();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Prefix > 0) _byLength[next[lines[i].Prefix]++] = i;
        }
    }

    /// <summary>True when some line codes OOB.</summary>
    public bool HasOutOfBand => _kind.Contains(OutOfBand);

    /// <summary>A value, or <see cref="Jbig2IntegerDecoder.Oob"/>.</summary>
    public long Decode(Jbig2BitReader bits)
    {
        long code = 0;
        for (int length = 1; length <= _maxLength; length++)
        {
            code = (code << 1) | (uint)bits.ReadBit();
            long n = code - _firstCode[length];
            if (_count[length] == 0 || n < 0 || n >= _count[length]) continue;
            int line = _byLength[_start[length] + (int)n];
            return _kind[line] switch
            {
                OutOfBand => Jbig2IntegerDecoder.Oob,
                Lower => _low[line] - bits.ReadBits(32),
                Upper => _low[line] + bits.ReadBits(32),
                _ => _low[line] + bits.ReadBits(_rangeLength[line]),
            };
        }

        throw new Jbig2FormatException("A Huffman code is not in its table.");
    }

    /// <summary>A value that must be in range for an int and not OOB.</summary>
    public int DecodeInt(Jbig2BitReader bits)
    {
        long value = Decode(bits);
        if (value is Jbig2IntegerDecoder.Oob or < int.MinValue or > int.MaxValue) throw new Jbig2FormatException("A Huffman coded value is out of range.");
        return (int)value;
    }

    /// <summary>A table whose line N codes the value N with the given prefix length: symbol ID and run code tables (7.4.3.1.7).</summary>
    public static Jbig2HuffmanTable FromLengths(IReadOnlyList<int> lengths)
    {
        var lines = new List<(long, int, int, byte)>(lengths.Count);
        for (int i = 0; i < lengths.Count; i++) lines.Add((i, lengths[i], 0, Normal));
        return new Jbig2HuffmanTable(lines);
    }

    /// <summary>
    /// A table as B.2 describes it: from <paramref name="low"/>, ranges of 2^RANGELEN values,
    /// then the lower range line, the upper range line and the OOB line (prefix length 0 for none).
    /// </summary>
    private static Jbig2HuffmanTable Build(long low, IReadOnlyList<(int Prefix, int Range)> ranges, int lowerPrefix, int upperPrefix, int oobPrefix)
    {
        var lines = new List<(long, int, int, byte)>(ranges.Count + 3);
        long current = low;
        foreach ((int prefix, int range) in ranges)
        {
            lines.Add((current, prefix, range, Normal));
            current += 1L << range;
        }

        lines.Add((low - 1, lowerPrefix, 32, Lower));
        lines.Add((current, upperPrefix, 32, Upper));
        lines.Add((0, oobPrefix, 0, OutOfBand));
        return new Jbig2HuffmanTable(lines);
    }

    /// <summary>A code table segment (T.88, 7.4.13 and B.2).</summary>
    public static Jbig2HuffmanTable Read(byte[] data, int start, int end)
    {
        if (end - start < 9) throw new Jbig2FormatException("A code table segment is too short.");
        int flags = data[start];
        int prefixBits = ((flags >> 1) & 7) + 1, rangeBits = ((flags >> 4) & 7) + 1;
        long low = Jbig2Decoder.Int32(data, start + 1), high = Jbig2Decoder.Int32(data, start + 5);
        if (low >= high) throw new Jbig2FormatException("A code table's range is empty.");
        var bits = new Jbig2BitReader(data, start + 9, end);
        var ranges = new List<(int, int)>();
        for (long current = low; current < high; )
        {
            if (ranges.Count >= MaxLines) throw new Jbig2FormatException("A code table has too many lines.");
            int prefix = (int)bits.ReadBits(prefixBits), range = (int)bits.ReadBits(rangeBits);
            if (range > 32) throw new Jbig2FormatException("A code table range is too wide.");
            ranges.Add((prefix, range));
            current += 1L << range;
        }

        int lower = (int)bits.ReadBits(prefixBits), upper = (int)bits.ReadBits(prefixBits);
        int oob = (flags & 1) != 0 ? (int)bits.ReadBits(prefixBits) : 0;
        return Build(low, ranges, lower, upper, oob);
    }

    private static readonly Jbig2HuffmanTable?[] StandardTables = new Jbig2HuffmanTable?[16];
    private static readonly object StandardGate = new();

    /// <summary>Standard table B.<paramref name="number"/> (T.88, B.5).</summary>
    public static Jbig2HuffmanTable Standard(int number)
    {
        lock (StandardGate)
        {
            return StandardTables[number] ??= number switch
            {
                1 => Build(0, [(1, 4), (2, 8), (3, 16)], 0, 3, 0),
                2 => Build(0, [(1, 0), (2, 0), (3, 0), (4, 3), (5, 6)], 0, 6, 6),
                3 => Build(-256, [(8, 8), (1, 0), (2, 0), (3, 0), (4, 3), (5, 6)], 8, 7, 6),
                4 => Build(1, [(1, 0), (2, 0), (3, 0), (4, 3), (5, 6)], 0, 5, 0),
                5 => Build(-255, [(7, 8), (1, 0), (2, 0), (3, 0), (4, 3), (5, 6)], 7, 6, 0),
                6 => Build(-2048, [(5, 10), (4, 9), (4, 8), (4, 7), (5, 6), (5, 5), (4, 5), (2, 7), (3, 7), (3, 8), (4, 9), (4, 10)], 6, 6, 0),
                7 => Build(-1024, [(4, 9), (3, 8), (4, 7), (5, 6), (5, 5), (4, 5), (4, 5), (5, 5), (5, 6), (4, 7), (3, 8), (3, 9), (3, 10)], 5, 5, 0),
                8 => Build(-15, [(8, 3), (9, 1), (8, 1), (9, 0), (7, 0), (4, 0), (2, 1), (5, 0), (6, 0), (3, 4), (6, 1), (4, 4), (4, 5), (5, 6), (5, 7), (6, 7), (7, 8), (6, 10)], 9, 9, 2),
                9 => Build(-31, [(8, 4), (9, 2), (8, 2), (9, 1), (7, 1), (4, 1), (3, 1), (3, 1), (5, 1), (6, 1), (3, 5), (6, 2), (4, 5), (4, 6), (5, 7), (5, 8), (6, 8), (7, 9), (6, 11)], 9, 9, 2),
                10 => Build(-21, [(7, 4), (8, 0), (7, 0), (5, 0), (2, 2), (5, 0), (6, 0), (7, 0), (8, 0), (2, 6), (5, 5), (6, 5), (6, 6), (6, 7), (6, 8), (6, 9), (6, 10), (7, 11)], 8, 8, 2),
                11 => Build(1, [(1, 0), (2, 1), (4, 0), (4, 1), (5, 1), (5, 2), (6, 2), (7, 2), (7, 3), (7, 4), (7, 5), (7, 6)], 0, 7, 0),
                12 => Build(1, [(1, 0), (2, 0), (3, 1), (5, 0), (5, 1), (6, 1), (7, 0), (7, 1), (7, 2), (7, 3), (7, 4), (8, 5)], 0, 8, 0),
                13 => Build(1, [(1, 0), (3, 0), (4, 0), (5, 0), (4, 1), (3, 3), (6, 1), (6, 2), (6, 3), (6, 4), (6, 5), (7, 6)], 0, 7, 0),
                14 => Build(-2, [(3, 0), (3, 0), (1, 0), (3, 0), (3, 0)], 0, 0, 0),
                15 => Build(-24, [(7, 4), (6, 2), (5, 1), (4, 0), (3, 0), (1, 0), (3, 0), (4, 0), (5, 1), (6, 2), (7, 4)], 7, 7, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(number)),
            };
        }
    }

    /// <summary>The prefix code of each line in table order, for the tests: (value or range low, code, length).</summary>
    internal IEnumerable<(long Low, byte Kind, long Code, int Length)> Codes()
    {
        for (int length = 1; length <= _maxLength; length++)
        {
            for (int n = 0; n < _count[length]; n++)
            {
                int line = _byLength[_start[length] + n];
                yield return (_low[line], _kind[line], _firstCode[length] + n, length);
            }
        }
    }
}
