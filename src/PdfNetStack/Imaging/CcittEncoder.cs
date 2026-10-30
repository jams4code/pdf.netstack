namespace PdfNetStack.Imaging;

/// <summary>
/// Encodes 1 bit images as CCITT Group 4 (ITU-T T.6). Every row is coded against the row above,
/// the first against an imaginary white row, choosing pass, vertical and horizontal modes by the
/// coding procedure of T.4, 4.2.1.3, the way libtiff does. EOFB ends the data, which is padded with
/// zero bits to a whole byte. The output reads back with K -1 and the Columns and BlackIs1 it was
/// written with.
/// </summary>
internal static class CcittEncoder
{
    private const int Eol = 0b000000000001;

    private static readonly (int Code, int Length)[] WhiteTerminating = Table(CcittDecoder.WhiteTerminating);
    private static readonly (int Code, int Length)[] BlackTerminating = Table(CcittDecoder.BlackTerminating);

    // Make-up codes by run / 64, for runs 64 to 2560: the color's own up to 1728, then the shared ones.
    private static readonly (int Code, int Length)[] WhiteMakeup = Table(["", .. CcittDecoder.WhiteMakeup, .. CcittDecoder.ExtendedMakeup]);
    private static readonly (int Code, int Length)[] BlackMakeup = Table(["", .. CcittDecoder.BlackMakeup, .. CcittDecoder.ExtendedMakeup]);

    // Leading zero bits of each byte value, 8 for zero.
    private static readonly byte[] LeadingZeros = BuildLeadingZeros();

    private static (int Code, int Length)[] Table(string[] codes) =>
        [.. codes.Select(c => c.Length == 0 ? (0, 0) : (System.Convert.ToInt32(c, 2), c.Length))];

    private static byte[] BuildLeadingZeros()
    {
        var table = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            int n = 0;
            while (n < 8 && (v & (0x80 >> n)) == 0) n++;
            table[v] = (byte)n;
        }

        return table;
    }

    /// <summary>
    /// Encodes <paramref name="height"/> packed rows of <paramref name="columns"/> pixels, each row
    /// padded to a whole byte. Black pixels are the 1 bits when <paramref name="blackIs1"/>, else
    /// the 0 bits; padding bits are ignored.
    /// </summary>
    /// <param name="maxBytes">Gives up, returning null, once the output would grow past this many bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is out of range or the rows are too short.</exception>
    public static byte[]? EncodeGroup4(ReadOnlySpan<byte> rows, int columns, int height, bool blackIs1, int maxBytes)
    {
        if (columns is <= 0 or > CcittDecoder.MaxColumns || height <= 0) throw new ArgumentOutOfRangeException(nameof(columns), "The size is out of range.");
        int rowBytes = (columns + 7) / 8;
        if (rows.Length < (long)rowBytes * height) throw new ArgumentOutOfRangeException(nameof(rows), "The rows are shorter than the size.");

        var writer = new BitWriter(Math.Max(0, maxBytes));
        var reference = new int[columns + 2];
        var coding = new int[columns + 2];

        // The imaginary row above the first is white: no changes.
        int referenceCount = 0;
        byte flip = blackIs1 ? (byte)0 : (byte)0xFF;
        for (int y = 0; y < height; y++)
        {
            int codingCount = Changes(rows.Slice(y * rowBytes, rowBytes), columns, flip, coding);
            if (!EncodeRow(ref writer, reference, referenceCount, coding, codingCount, columns)) return null;
            (reference, coding) = (coding, reference);
            referenceCount = codingCount;
        }

        writer.Write(Eol, 12);
        writer.Write(Eol, 12);
        return writer.Finish();
    }

    /// <summary>
    /// The changing elements of a row: where the color first differs from the pixel before it,
    /// starting from white, so even indexes turn black and odd indexes turn white.
    /// </summary>
    private static int Changes(ReadOnlySpan<byte> row, int columns, byte flip, int[] changes)
    {
        int count = 0, x = 0;
        bool black = false;
        while (true)
        {
            // Bits are turned so that 1 is black, then so that 1 is the color sought.
            byte seek = black ? (byte)~flip : flip;
            int i = x >> 3;
            int v = (row[i] ^ seek) & (0xFF >> (x & 7));
            while (v == 0)
            {
                if (++i >= row.Length) return count;
                v = row[i] ^ seek;
            }

            x = (i << 3) + LeadingZeros[v];
            if (x >= columns) return count;
            changes[count++] = x;
            black = !black;
        }
    }

    /// <summary>One row in two dimensional coding. False when the writer ran out of room.</summary>
    private static bool EncodeRow(ref BitWriter writer, int[] reference, int referenceCount, int[] coding, int codingCount, int columns)
    {
        // Sentinels: past its last change each row has changes at the right edge.
        reference[referenceCount] = columns;
        reference[referenceCount + 1] = columns;
        coding[codingCount] = columns;
        coding[codingCount + 1] = columns;

        int a0 = -1, color = 0, next = 0, search = 0;
        while (a0 < columns)
        {
            // b1: the first change on the reference row right of a0 whose color is opposite a0's,
            // found exactly as the decoder finds it.
            while (search > 0 && reference[search - 1] > a0) search--;
            int i = search;
            while (i < referenceCount && (reference[i] <= a0 || (i & 1) != color)) i++;
            search = i;
            int b1 = reference[i], b2 = reference[Math.Min(i + 1, referenceCount + 1)];

            // a1: the next change on the coding row, whose parity always matches the color.
            int a1 = coding[next];
            if (b2 < a1)
            {
                writer.Write(0b0001, 4);
                a0 = b2;
            }
            else if (Math.Abs(a1 - b1) <= 3)
            {
                Vertical(ref writer, a1 - b1);
                a0 = a1;
                color ^= 1;
                next++;
            }
            else
            {
                int a2 = coding[next + 1];
                writer.Write(0b001, 3);
                Run(ref writer, a1 - Math.Max(a0, 0), color == 0);
                Run(ref writer, a2 - a1, color != 0);
                a0 = a2;
                next += 2;
            }

            if (writer.Overflowed) return false;
        }

        return true;
    }

    private static void Vertical(ref BitWriter writer, int offset)
    {
        switch (offset)
        {
            case 0: writer.Write(0b1, 1); break;
            case 1: writer.Write(0b011, 3); break;
            case -1: writer.Write(0b010, 3); break;
            case 2: writer.Write(0b000011, 6); break;
            case -2: writer.Write(0b000010, 6); break;
            case 3: writer.Write(0b0000011, 7); break;
            default: writer.Write(0b0000010, 7); break;
        }
    }

    /// <summary>A run of one color: make-up codes of 2560 while more is left than one more make-up and a terminating code can hold, then one make-up and a terminating code.</summary>
    private static void Run(ref BitWriter writer, int run, bool white)
    {
        (int Code, int Length)[] makeup = white ? WhiteMakeup : BlackMakeup;
        while (run >= 2560 + 64)
        {
            writer.Write(makeup[40].Code, makeup[40].Length);
            run -= 2560;
        }

        if (run >= 64)
        {
            writer.Write(makeup[run >> 6].Code, makeup[run >> 6].Length);
            run &= 63;
        }

        (int code, int length) = (white ? WhiteTerminating : BlackTerminating)[run];
        writer.Write(code, length);
    }

    /// <summary>Most significant bit first, into a buffer that never grows past a ceiling.</summary>
    private ref struct BitWriter
    {
        private readonly int _limit;
        private byte[] _buffer;
        private int _length;
        private ulong _bits;
        private int _count;

        public BitWriter(int limit)
        {
            _limit = limit;
            _buffer = new byte[Math.Min(limit, 4096)];
        }

        public bool Overflowed { get; private set; }

        public void Write(int code, int length)
        {
            _bits = (_bits << length) | (uint)code;
            _count += length;
            while (_count >= 8)
            {
                _count -= 8;
                Put((byte)(_bits >> _count));
            }
        }

        public byte[]? Finish()
        {
            if (_count > 0) Put((byte)(_bits << (8 - _count)));
            _count = 0;
            return Overflowed ? null : _buffer.AsSpan(0, _length).ToArray();
        }

        private void Put(byte value)
        {
            if (_length == _buffer.Length)
            {
                if (_length >= _limit)
                {
                    Overflowed = true;
                    return;
                }

                Array.Resize(ref _buffer, (int)Math.Min(_limit, Math.Max(4096L, (long)_buffer.Length * 2)));
            }

            _buffer[_length++] = value;
        }
    }
}
