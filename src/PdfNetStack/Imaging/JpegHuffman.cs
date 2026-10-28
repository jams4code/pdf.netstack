namespace PdfNetStack.Imaging;

/// <summary>
/// A Huffman table as defined by a DHT segment: code counts per length 1..16 and the symbols in
/// code order (ITU T.81 Annex C). Decoding uses a 9 bit lookahead with a per length fallback.
/// </summary>
internal sealed class JpegHuffmanTable
{
    public const int LookaheadBits = 9;

    private readonly byte[] _lookLength = new byte[1 << LookaheadBits];
    private readonly byte[] _lookSymbol = new byte[1 << LookaheadBits];
    private readonly int[] _maxCode = new int[18];
    private readonly int[] _valueOffset = new int[18];
    private readonly byte[] _symbols;

    /// <param name="counts">Number of codes of each length, index 0 for length 1.</param>
    /// <param name="symbols">Symbols in order of increasing code.</param>
    public JpegHuffmanTable(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
    {
        _symbols = symbols.ToArray();
        int code = 0;
        int index = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = counts[length - 1];
            _valueOffset[length] = index - code;
            for (int i = 0; i < count; i++, index++, code++)
            {
                // Checked before the code is used: an oversubscribed table would index past the lookahead.
                if (code >= 1 << length) throw new JpegFormatException("Huffman table defines more codes than fit.");
                if (length <= LookaheadBits)
                {
                    int shift = LookaheadBits - length;
                    int first = code << shift;
                    for (int j = 0; j < 1 << shift; j++)
                    {
                        _lookLength[first + j] = (byte)length;
                        _lookSymbol[first + j] = symbols[index];
                    }
                }
            }

            _maxCode[length] = count == 0 ? -1 : code - 1;
            code <<= 1;
        }

        _maxCode[17] = int.MaxValue;
    }

    /// <summary>Decodes one symbol from the top 16 bits of <paramref name="peek"/>; returns its length in bits.</summary>
    public int Decode(int peek, out int symbol)
    {
        int look = peek >> (16 - LookaheadBits);
        int length = _lookLength[look];
        if (length != 0)
        {
            symbol = _lookSymbol[look];
            return length;
        }

        for (length = LookaheadBits + 1; length <= 16; length++)
        {
            int code = peek >> (16 - length);
            if (code <= _maxCode[length])
            {
                symbol = _symbols[_valueOffset[length] + code];
                return length;
            }
        }

        throw new JpegFormatException("Invalid Huffman code.");
    }
}

/// <summary>Huffman code lengths built from symbol frequencies, and the codes they imply.</summary>
internal sealed class JpegHuffmanCode
{
    private JpegHuffmanCode(byte[] counts, byte[] symbols)
    {
        Counts = counts;
        Symbols = symbols;
        int code = 0;
        int index = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < counts[length - 1]; i++, index++, code++)
            {
                Codes[symbols[index]] = (ushort)code;
                Lengths[symbols[index]] = (byte)length;
            }

            code <<= 1;
        }
    }

    /// <summary>Number of codes per length 1..16, as written in DHT.</summary>
    public byte[] Counts { get; }

    /// <summary>Symbols in code order, as written in DHT.</summary>
    public byte[] Symbols { get; }

    public ushort[] Codes { get; } = new ushort[256];

    public byte[] Lengths { get; } = new byte[256];

    /// <summary>
    /// Builds the optimal length limited code for the given frequencies, following ITU T.81
    /// Annex K.2. A reserved pseudo symbol keeps the all ones code unused, which JPEG requires.
    /// Ties resolve to the larger symbol, so the result is deterministic.
    /// </summary>
    public static JpegHuffmanCode Optimal(ReadOnlySpan<long> frequencies)
    {
        const int Symbols = 257;
        bool used = false;
        foreach (long f in frequencies) used |= f != 0;
        if (frequencies.Length != 256 || !used)
        {
            throw new ArgumentException("A Huffman code needs 256 frequencies, at least one of them used.", nameof(frequencies));
        }

        var freq = new long[Symbols];
        frequencies.CopyTo(freq);
        freq[256] = 1;
        var codeSize = new int[Symbols];
        var others = new int[Symbols];
        for (int i = 0; i < Symbols; i++) others[i] = -1;

        while (true)
        {
            int c1 = -1;
            long v = long.MaxValue;
            for (int i = 0; i < Symbols; i++)
            {
                if (freq[i] != 0 && freq[i] <= v)
                {
                    v = freq[i];
                    c1 = i;
                }
            }

            int c2 = -1;
            v = long.MaxValue;
            for (int i = 0; i < Symbols; i++)
            {
                if (freq[i] != 0 && freq[i] <= v && i != c1)
                {
                    v = freq[i];
                    c2 = i;
                }
            }

            if (c2 < 0) break;

            freq[c1] += freq[c2];
            freq[c2] = 0;
            codeSize[c1]++;
            while (others[c1] >= 0)
            {
                c1 = others[c1];
                codeSize[c1]++;
            }

            others[c1] = c2;
            codeSize[c2]++;
            while (others[c2] >= 0)
            {
                c2 = others[c2];
                codeSize[c2]++;
            }
        }

        // 257 symbols cannot produce a code longer than 256 bits.
        var bits = new int[Symbols + 1];
        int longest = 0;
        for (int i = 0; i < Symbols; i++)
        {
            if (codeSize[i] == 0) continue;
            bits[codeSize[i]]++;
            longest = Math.Max(longest, codeSize[i]);
        }

        // Annex K.3: move pairs of overlong codes up the tree until nothing exceeds 16 bits.
        for (int i = longest; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0) j--;
                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }

        // Drop the reserved symbol, which holds the longest code.
        int last = 16;
        while (bits[last] == 0) last--;
        bits[last]--;

        var counts = new byte[16];
        for (int i = 1; i <= 16; i++) counts[i - 1] = (byte)bits[i];

        // Symbols by original code size; lengths changed by limiting keep this order, as K.4 allows.
        var symbols = new List<byte>();
        for (int size = 1; size <= longest; size++)
        {
            for (int s = 0; s < 256; s++)
            {
                if (codeSize[s] == size) symbols.Add((byte)s);
            }
        }

        return new JpegHuffmanCode(counts, [.. symbols]);
    }
}
