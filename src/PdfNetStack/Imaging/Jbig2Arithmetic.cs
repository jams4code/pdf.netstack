namespace PdfNetStack.Imaging;

/// <summary>
/// The MQ arithmetic decoder (T.88, Annex E). A context is one byte: its probability state index
/// shifted left once, then its MPS. Reading past the end of the data reads 0xFF bytes, so decoding
/// never fails; the callers' loops are bounded by the work budget instead.
/// </summary>
internal sealed class Jbig2ArithmeticDecoder
{
    // T.88, Table E.1: Qe, then the next state after an MPS and after an LPS, and whether an LPS switches the MPS.
    private static readonly ushort[] Qe =
    [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
        0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
        0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
        0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    ];

    private static readonly byte[] NextMps =
    [
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
    ];

    private static readonly byte[] NextLps =
    [
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
    ];

    private static readonly bool[] Switch = [true, false, false, false, false, false, true, false, false, false, false, false, false, false, true];

    private readonly byte[] _data;
    private readonly int _end;
    private int _position;
    private uint _c;
    private uint _a;
    private int _ct;

    /// <summary>INITDEC (T.88, E.3.5) on the bytes from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public Jbig2ArithmeticDecoder(byte[] data, int start, int end)
    {
        _data = data;
        _end = Math.Min(end, data.Length);
        _position = start;
        _c = (uint)Byte(_position) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    private int Byte(int at) => at < _end ? _data[at] : 0xFF;

    /// <summary>BYTEIN (T.88, E.3.4): a 0xFF followed by more than 0x8F is a marker, so ones are fed in.</summary>
    private void ByteIn()
    {
        if (Byte(_position) == 0xFF)
        {
            if (Byte(_position + 1) > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
            }
            else
            {
                _position++;
                _c += (uint)Byte(_position) << 9;
                _ct = 7;
            }
        }
        else
        {
            _position++;
            _c += (uint)Byte(_position) << 8;
            _ct = 8;
        }
    }

    /// <summary>DECODE (T.88, E.3.2) with the context at <paramref name="index"/>.</summary>
    public int Decode(byte[] contexts, int index)
    {
        int state = contexts[index], i = state >> 1, mps = state & 1, d;
        uint qe = Qe[i];
        _a -= qe;
        if ((_c >> 16) < qe)
        {
            // LPS_EXCHANGE: the lower sub-interval, with the conditional exchange.
            if (_a < qe)
            {
                d = mps;
                i = NextMps[i];
            }
            else
            {
                d = 1 - mps;
                if (i < Switch.Length && Switch[i]) mps = d;
                i = NextLps[i];
            }

            _a = qe;
        }
        else
        {
            _c -= qe << 16;
            if ((_a & 0x8000) != 0) return mps;

            // MPS_EXCHANGE.
            if (_a < qe)
            {
                d = 1 - mps;
                if (i < Switch.Length && Switch[i]) mps = d;
                i = NextLps[i];
            }
            else
            {
                d = mps;
                i = NextMps[i];
            }
        }

        // RENORMD (T.88, E.3.3).
        do
        {
            if (_ct == 0) ByteIn();
            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);

        contexts[index] = (byte)((i << 1) | mps);
        return d;
    }
}

/// <summary>The integer arithmetic decoding procedures (T.88, Annex A), one context set per procedure.</summary>
internal sealed class Jbig2IntegerDecoder(Jbig2ArithmeticDecoder decoder)
{
    /// <summary>The out-of-band value: no integer.</summary>
    public const long Oob = long.MinValue;

    public Jbig2ArithmeticDecoder Arithmetic { get; } = decoder;

    /// <summary>Contexts for one integer procedure (IADH, IADW and so on): PREV has 9 bits.</summary>
    public static byte[] Contexts() => new byte[512];

    /// <summary>A.2: a sign, a prefix choosing the range, then the offset in that range.</summary>
    public long Decode(byte[] contexts)
    {
        int prev = 1;
        int sign = Bit(contexts, ref prev);
        long value;
        if (Bit(contexts, ref prev) == 0) value = Bits(contexts, ref prev, 2);
        else if (Bit(contexts, ref prev) == 0) value = Bits(contexts, ref prev, 4) + 4;
        else if (Bit(contexts, ref prev) == 0) value = Bits(contexts, ref prev, 6) + 20;
        else if (Bit(contexts, ref prev) == 0) value = Bits(contexts, ref prev, 8) + 84;
        else if (Bit(contexts, ref prev) == 0) value = Bits(contexts, ref prev, 12) + 340;
        else value = Bits(contexts, ref prev, 32) + 4436;

        if (sign == 0) return value;
        return value == 0 ? Oob : -value;
    }

    /// <summary>A.2, where OOB is not allowed and the value must fit an int.</summary>
    public int DecodeInt(byte[] contexts)
    {
        long value = Decode(contexts);
        if (value is Oob or < int.MinValue or > int.MaxValue) throw new Jbig2FormatException("An integer is out of range.");
        return (int)value;
    }

    private int Bit(byte[] contexts, ref int prev)
    {
        int d = Arithmetic.Decode(contexts, prev);
        prev = prev < 256 ? (prev << 1) | d : (((prev << 1) | d) & 511) | 256;
        return d;
    }

    private long Bits(byte[] contexts, ref int prev, int count)
    {
        long value = 0;
        for (int k = 0; k < count; k++) value = (value << 1) | (uint)Bit(contexts, ref prev);
        return value;
    }

    /// <summary>IAID (T.88, A.3): <paramref name="length"/> bits, each in the context of the bits before it.</summary>
    /// <param name="contexts">At least 2^<paramref name="length"/> contexts.</param>
    public int DecodeId(byte[] contexts, int length)
    {
        int prev = 1;
        for (int k = 0; k < length; k++) prev = (prev << 1) | Arithmetic.Decode(contexts, prev);
        return prev - (1 << length);
    }
}
