namespace PdfNetStack.Imaging;

/// <summary>The CCITTFaxDecode parameters (ISO 32000-1, Table 11).</summary>
internal sealed record CcittParameters
{
    /// <summary>Negative: Group 4. Zero: Group 3 one dimensional. Positive: Group 3 mixed.</summary>
    public int K { get; init; }

    public bool EndOfLine { get; init; }

    public bool EncodedByteAlign { get; init; }

    public int Columns { get; init; } = 1728;

    /// <summary>Rows in the image, or 0 when unknown.</summary>
    public int Rows { get; init; }

    public bool EndOfBlock { get; init; } = true;

    public bool BlackIs1 { get; init; }
}

/// <summary>
/// Decodes CCITT fax data (ITU-T T.4 and T.6) to rows of 1 bit samples, packed as PDF image
/// samples are: 0 for black unless BlackIs1. Group 3 one and two dimensional, Group 4, EOL codes,
/// fill bits and byte aligned rows are read. Damaged data keeps the rows decoded before the damage
/// and leaves the rest white, as viewers show it.
/// </summary>
internal static class CcittDecoder
{
    public const int MaxColumns = 1 << 20;

    // A run longer than any row: a run length table entry never exceeds 2560 per code.
    private const int MaxRun = MaxColumns;

    private const int Eol = -2;
    private const int Invalid = -1;

    // ITU-T T.4, Table 2: terminating codes for runs 0 to 63, by run length.
    private static readonly string[] WhiteTerminating =
    [
        "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111", "10011", "10100", "00111", "01000", "001000", "000011", "110100", "110101",
        "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100", "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010",
        "00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111", "00101000", "00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
        "00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101", "01011000", "01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
    ];

    private static readonly string[] BlackTerminating =
    [
        "0000110111", "010", "11", "10", "011", "0011", "0010", "00011", "000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111", "000011000",
        "0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000", "00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001",
        "000001101010", "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111", "000001101100", "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
        "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111", "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111",
    ];

    // ITU-T T.4, Table 3: make-up codes for runs 64 to 1728 in steps of 64.
    private static readonly string[] WhiteMakeup =
    [
        "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101", "01101000", "01100111", "011001100", "011001101", "011010010", "011010011",
        "011010100", "011010101", "011010110", "011010111", "011011000", "011011001", "011011010", "011011011", "010011000", "010011001", "010011010", "011000", "010011011",
    ];

    private static readonly string[] BlackMakeup =
    [
        "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100", "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010",
        "0000001110011", "0000001110100", "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010", "0000001011011", "0000001100100", "0000001100101",
    ];

    // Extended make-up codes for runs 1792 to 2560, shared by both colors.
    private static readonly string[] ExtendedMakeup =
    [
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101", "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
    ];

    private static readonly Dictionary<int, int> White = BuildTable(WhiteTerminating, WhiteMakeup);
    private static readonly Dictionary<int, int> Black = BuildTable(BlackTerminating, BlackMakeup);

    /// <summary>Every code of one color as (bits, run length), for the tests' prefix check.</summary>
    internal static IEnumerable<(string Code, int Run)> Codes(bool white)
    {
        string[] terminating = white ? WhiteTerminating : BlackTerminating, makeup = white ? WhiteMakeup : BlackMakeup;
        for (int i = 0; i < terminating.Length; i++) yield return (terminating[i], i);
        for (int i = 0; i < makeup.Length; i++) yield return (makeup[i], 64 * (i + 1));
        for (int i = 0; i < ExtendedMakeup.Length; i++) yield return (ExtendedMakeup[i], 1792 + (64 * i));
        yield return ("000000000001", Eol);
    }

    private static Dictionary<int, int> BuildTable(string[] terminating, string[] makeup)
    {
        var table = new Dictionary<int, int>();
        void Add(string code, int run) => table.Add((code.Length << 16) | System.Convert.ToInt32(code, 2), run);
        for (int i = 0; i < terminating.Length; i++) Add(terminating[i], i);
        for (int i = 0; i < makeup.Length; i++) Add(makeup[i], 64 * (i + 1));
        for (int i = 0; i < ExtendedMakeup.Length; i++) Add(ExtendedMakeup[i], 1792 + (64 * i));
        Add("000000000001", Eol);
        return table;
    }

    /// <param name="data">The encoded data.</param>
    /// <param name="p">The decode parameters.</param>
    /// <param name="height">Rows to produce when <see cref="CcittParameters.Rows"/> is 0: the image's height.</param>
    /// <exception cref="ArgumentOutOfRangeException">Columns or rows are out of range.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> data, CcittParameters p, int height)
    {
        int columns = p.Columns, rows = p.Rows > 0 ? p.Rows : height;
        if (columns is <= 0 or > MaxColumns) throw new ArgumentOutOfRangeException(nameof(p), "Columns are out of range.");
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Rows are out of range.");
        int rowBytes = (columns + 7) / 8;
        var output = new byte[(long)rowBytes * rows];

        // White everywhere until a row says otherwise.
        byte white = p.BlackIs1 ? (byte)0x00 : (byte)0xFF;
        output.AsSpan().Fill(white);

        var bits = new BitReader(data);
        var reference = new int[columns + 2];
        var coding = new int[columns + 2];
        int referenceCount = 0;
        for (int row = 0; row < rows; row++)
        {
            if (p.EncodedByteAlign && !(p.K >= 0 && p.EndOfLine)) bits.Align();

            // Group 3 rows may start with EOL, after fill bits; two in a row end the data (RTC).
            bool twoDimensional = p.K < 0;
            if (p.K >= 0)
            {
                if (SkipEol(ref bits))
                {
                    // In mixed mode each EOL carries a tag bit, so RTC is EOL 1 EOL 1 and so on.
                    if ((p.K > 0 && bits.Peek(13) == 0b1_000000000001) || (p.K == 0 && bits.Peek(12) == 1)) break;
                }

                if (p.K > 0) twoDimensional = bits.Read(1) == 0;
            }

            if (bits.AtEnd) break;
            int count = twoDimensional
                ? ReadRow2D(ref bits, reference, referenceCount, coding, columns)
                : ReadRow1D(ref bits, coding, columns);
            if (count < 0) break;

            Fill(output.AsSpan(row * rowBytes, rowBytes), coding, count, columns, p.BlackIs1);
            (reference, coding) = (coding, reference);
            referenceCount = count;
        }

        return output;
    }

    /// <summary>Skips fill bits and one EOL (eleven or more zeros, then a one). False, consuming nothing, when none is there.</summary>
    private static bool SkipEol(ref BitReader bits)
    {
        int zeros = bits.CountZeros(4096);
        if (zeros < 11 || bits.AtEndAfter(zeros)) return false;
        bits.Skip(zeros + 1);
        return true;
    }

    /// <summary>A one dimensional (Modified Huffman) row: alternating white and black runs. Returns the changing elements, or -1.</summary>
    private static int ReadRow1D(ref BitReader bits, int[] changes, int columns)
    {
        int count = 0, position = 0;
        bool white = true;
        while (position < columns)
        {
            int run = ReadRun(ref bits, white);
            if (run < 0) return -1;
            position = Math.Min(columns, position + run);
            if (position < columns) changes[count++] = position;
            white = !white;
            if (count >= columns) break;
        }

        return count;
    }

    /// <summary>
    /// A two dimensional row (ITU-T T.4, 4.2; T.6): each changing element coded against the
    /// reference row above as pass, horizontal or vertical. Returns the changing elements, or -1.
    /// </summary>
    private static int ReadRow2D(ref BitReader bits, int[] reference, int referenceCount, int[] coding, int columns)
    {
        // Sentinels: past the last change the reference row has changes at the right edge.
        reference[referenceCount] = columns;
        reference[referenceCount + 1] = columns;
        int count = 0, a0 = -1, color = 0, search = 0;
        while (a0 < columns)
        {
            // b1: the first change on the reference row right of a0 whose color is opposite a0's.
            // Changes alternate starting with white to black, so their parity gives their color.
            while (search > 0 && reference[search - 1] > a0) search--;
            int i = search;
            while (i < referenceCount && (reference[i] <= a0 || (i & 1) != color)) i++;
            search = i;
            int b1 = reference[i], b2 = reference[Math.Min(i + 1, referenceCount + 1)];

            int mode = ReadMode(ref bits);
            switch (mode)
            {
                case Pass:
                    if (b2 <= a0) return -1;
                    a0 = b2;
                    break;
                case Horizontal:
                {
                    int start = Math.Max(a0, 0);
                    int first = ReadRun(ref bits, color == 0), second = first < 0 ? -1 : ReadRun(ref bits, color != 0);
                    if (second < 0) return -1;
                    int a1 = Math.Min(columns, start + first), a2 = Math.Min(columns, a1 + second);
                    if (count + 2 > columns) return -1;
                    coding[count++] = a1;
                    coding[count++] = a2;
                    a0 = a2;
                    break;
                }

                case >= -3 and <= 3:
                {
                    int a1 = b1 + mode;
                    if (a1 < Math.Max(a0, 0) || a1 > columns || count + 1 > columns) return -1;
                    coding[count++] = a1;
                    a0 = a1;
                    color ^= 1;
                    break;
                }

                default:
                    return -1;
            }
        }

        // A change at the right edge is no change.
        while (count > 0 && coding[count - 1] >= columns) count--;
        return count;
    }

    private const int Pass = 10;
    private const int Horizontal = 11;

    /// <summary>The 2D mode codes (ITU-T T.4, Table 4): vertical offsets -3 to 3, pass, horizontal.</summary>
    private static int ReadMode(ref BitReader bits)
    {
        int peek = bits.Peek(7);
        if ((peek & 0b1000000) != 0) return Consume(ref bits, 1, 0);
        if ((peek & 0b1110000) == 0b0110000) return Consume(ref bits, 3, 1);
        if ((peek & 0b1110000) == 0b0100000) return Consume(ref bits, 3, -1);
        if ((peek & 0b1110000) == 0b0010000) return Consume(ref bits, 3, Horizontal);
        if ((peek & 0b1111000) == 0b0001000) return Consume(ref bits, 4, Pass);
        if ((peek & 0b1111110) == 0b0000110) return Consume(ref bits, 6, 2);
        if ((peek & 0b1111110) == 0b0000100) return Consume(ref bits, 6, -2);
        if (peek == 0b0000011) return Consume(ref bits, 7, 3);
        if (peek == 0b0000010) return Consume(ref bits, 7, -3);

        // EOL or EOFB, or an extension (uncompressed mode), which is not supported: the row ends here.
        return Invalid;
    }

    private static int Consume(ref BitReader bits, int length, int mode)
    {
        bits.Skip(length);
        return mode;
    }

    /// <summary>A run of one color: make-up codes, then a terminating code.</summary>
    private static int ReadRun(ref BitReader bits, bool white)
    {
        Dictionary<int, int> table = white ? White : Black;
        int total = 0;
        while (true)
        {
            int run = Invalid, ahead = bits.Peek(13);
            for (int length = 2; length <= 13; length++)
            {
                if (table.TryGetValue((length << 16) | (ahead >> (13 - length)), out run))
                {
                    bits.Skip(length);
                    break;
                }

                run = Invalid;
            }

            if (run < 0) return Invalid;
            total += run;
            if (total > MaxRun) return Invalid;
            if (run < 64) return total;
        }
    }

    /// <summary>Paints a row: white, then black between every other pair of changing elements.</summary>
    private static void Fill(Span<byte> row, int[] changes, int count, int columns, bool blackIs1)
    {
        for (int i = 0; i < count; i += 2)
        {
            int from = changes[i], to = i + 1 < count ? changes[i + 1] : columns;
            for (int x = from; x < to; x++)
            {
                if (blackIs1) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                else row[x >> 3] &= (byte)~(0x80 >> (x & 7));
            }
        }
    }

    /// <summary>Most significant bit first; reading past the end gives zeros.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _position;

        public BitReader(ReadOnlySpan<byte> data) => _data = data;

        public readonly bool AtEnd => _position >= (long)_data.Length * 8;

        public readonly bool AtEndAfter(int bits) => _position + bits >= (long)_data.Length * 8;

        /// <summary>The next <paramref name="count"/> bits, up to 24, without consuming them.</summary>
        public readonly int Peek(int count)
        {
            long at = _position >> 3;
            uint window = 0;
            for (int k = 0; k < 4; k++) window = (window << 8) | (at + k < _data.Length ? _data[(int)(at + k)] : 0u);
            return (int)((window << (int)(_position & 7)) >> (32 - count));
        }

        public int Read(int count)
        {
            int value = Peek(count);
            _position += count;
            return value;
        }

        public void Skip(int count) => _position += count;

        public void Align() => _position = (_position + 7) & ~7L;

        /// <summary>Zero bits ahead, up to <paramref name="limit"/>, without consuming them.</summary>
        public readonly int CountZeros(int limit)
        {
            int n = 0;
            long end = (long)_data.Length * 8;
            while (n < limit && _position + n < end && Peek1(_position + n) == 0) n++;
            return n;
        }

        private readonly int Peek1(long bit) => (_data[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1;
    }
}
