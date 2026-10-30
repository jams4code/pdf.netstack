using System.Runtime.CompilerServices;

namespace PdfNetStack.Imaging;

/// <summary>
/// The MQ arithmetic decoder (T.92 Annex C, as T.800 uses it) over one codeword segment, plus the
/// raw bit reader of selective arithmetic coding bypass (D.6). It is a struct so a coding pass can
/// keep it in locals. The segment ends with two 0xFF bytes, which act as a marker: past the data
/// the decoder then feeds 1 bits, as the standard's software convention does.
/// </summary>
internal struct Jpeg2000Mq
{
    public const int Contexts = 19;
    public const int RunLength = 17;
    public const int Uniform = 18;

    // Table C.2 by context state, the state index times two plus the MPS: Qe, then the state after
    // an MPS exchange and after an LPS exchange, the LPS switching the MPS where the table says so.
    private static readonly uint[] Qe = Expand(
    [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
        0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
        0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
        0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    ]);

    private static readonly byte[] NextMps =
    [
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
    ];

    private static readonly byte[] NextLps =
    [
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21,
        22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
    ];

    private static readonly byte[] Switch =
    [
        1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static readonly byte[] AfterMps = Transitions(lps: false);
    private static readonly byte[] AfterLps = Transitions(lps: true);

    private byte[] _data;
    private int _pos;
    private uint _c;
    private uint _a;
    private int _ct;

    /// <summary>The initial states of Table D.7: uniform 46, run-length 3, the all-zero context 4.</summary>
    public static void ResetContexts(byte[] states)
    {
        Array.Clear(states, 0, Contexts);
        states[0] = 4 << 1;
        states[RunLength] = 3 << 1;
        states[Uniform] = 46 << 1;
    }

    /// <summary>INITDEC (C.3.5) on a segment; the contexts are left as they are.</summary>
    /// <param name="data">The segment from index 0, followed by two 0xFF bytes.</param>
    public void Start(byte[] data)
    {
        _data = data;
        _pos = 0;
        _c = (uint)data[0] << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>Starts raw (bypass) reading of a segment laid out as for <see cref="Start"/>.</summary>
    public void StartRaw(byte[] data)
    {
        _data = data;
        _pos = 0;
        _c = 0;
        _ct = 0;
    }

    /// <summary>
    /// BYTEIN (C.3.4): a 0xFF followed by more than 0x8F is a marker and is not consumed. The
    /// position stops at the first 0xFF past the data, so reads stay within the two padding bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ByteIn()
    {
        byte[] data = _data;
        if (data[_pos] == 0xFF)
        {
            int next = data[_pos + 1];
            if (next > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
            }
            else
            {
                _pos++;
                _c += (uint)next << 9;
                _ct = 7;
            }
        }
        else
        {
            _pos++;
            _c += (uint)data[_pos] << 8;
            _ct = 8;
        }
    }

    /// <summary>DECODE (C.3.2): the common case, an MPS without renormalization, inline.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(byte[] states, int context)
    {
        int s = states[context];
        uint qe = Qe[s];
        _a -= qe;
        if ((_c >> 16) < qe) return Exchange(states, context, s, qe, lowerHalf: true);
        _c -= qe << 16;
        if ((_a & 0x8000) != 0) return s & 1;
        return Exchange(states, context, s, qe, lowerHalf: false);
    }

    /// <summary>The conditional exchanges of C.3.3 and C.3.4, then RENORMD.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Exchange(byte[] states, int context, int s, uint qe, bool lowerHalf)
    {
        int mps = s & 1, d;
        if (lowerHalf)
        {
            // LPS_EXCHANGE: the interval keeps Qe.
            if (_a < qe)
            {
                d = mps;
                states[context] = AfterMps[s];
            }
            else
            {
                d = 1 - mps;
                states[context] = AfterLps[s];
            }

            _a = qe;
        }
        else if (_a < qe)
        {
            // MPS_EXCHANGE.
            d = 1 - mps;
            states[context] = AfterLps[s];
        }
        else
        {
            d = mps;
            states[context] = AfterMps[s];
        }

        // RENORMD, a byte's worth of shifts at a time: the shifts A needs, taking in bytes as CT runs out.
        int shifts = LeadingZeros(_a) - 16;
        do
        {
            if (_ct == 0) ByteIn();
            int k = Math.Min(shifts, _ct);
            _a <<= k;
            _c <<= k;
            _ct -= k;
            shifts -= k;
        }
        while (shifts > 0);
        return d;
    }

    /// <summary>One raw bit; after a 0xFF the stuffed 0 bit is skipped (D.6).</summary>
    public int DecodeRaw()
    {
        if (_ct == 0)
        {
            if (_c == 0xFF)
            {
                if (_data[_pos] > 0x8F)
                {
                    _ct = 8;
                }
                else
                {
                    _c = _data[_pos++];
                    _ct = 7;
                }
            }
            else
            {
                _c = _data[_pos++];
                _ct = 8;
            }
        }

        _ct--;
        return (int)(_c >> _ct) & 1;
    }

    /// <summary>Leading zero bits of a value that is not 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LeadingZeros(uint value)
    {
#if NET8_0_OR_GREATER
        return System.Numerics.BitOperations.LeadingZeroCount(value);
#else
        int n = 0;
        while ((value & 0x80000000u) == 0)
        {
            value <<= 1;
            n++;
        }

        return n;
#endif
    }

    private static uint[] Expand(ushort[] qe)
    {
        var table = new uint[qe.Length * 2];
        for (int i = 0; i < table.Length; i++) table[i] = qe[i >> 1];
        return table;
    }

    private static byte[] Transitions(bool lps)
    {
        var table = new byte[NextMps.Length * 2];
        for (int s = 0; s < table.Length; s++)
        {
            int index = s >> 1, mps = s & 1;
            table[s] = lps ? (byte)((NextLps[index] << 1) | (mps ^ Switch[index])) : (byte)((NextMps[index] << 1) | mps);
        }

        return table;
    }
}

/// <summary>
/// Tier-1 decoding of one code-block (T.800 Annex D): the significance propagation, magnitude
/// refinement and cleanup passes over stripes of four rows, with every code-block style flag.
/// Coefficients come out at twice their scale with the half-interval of the last decoded
/// bit-plane added, as OpenJPEG reconstructs them.
/// </summary>
/// <remarks>
/// The state of each column of a stripe is one 32 bit word, so a pass tells from one read
/// whether a column has anything to code, and a sample's contexts come from shifts of that word.
/// Bits 0 to 17 are the significance of the 3 by 6 samples around the column's four, row by
/// row from the row above the stripe to the row below it, each row west, centre, east: the
/// sample in row r (0 to 3) and column offset d (-1 to 1) is bit 3(r + 1) + d + 1. Bits 18 to
/// 21 mark the four samples visited in this bit-plane, bits 22 to 25 refined once, and bits 26
/// to 31 hold the signs of the column's samples from the row above to the row below.
/// </remarks>
internal sealed class Jpeg2000BlockDecoder
{
    private const int VisitedShift = 18;
    private const int RefinedShift = 22;
    private const int SignShift = 27;

    // Significance of the whole neighbourhood, and of the column's own four samples.
    private const uint Neighbourhood = (1u << 18) - 1;
    private const uint OwnSignificance = (1u << 4) | (1u << 7) | (1u << 10) | (1u << 13);
    private const uint VisitedBits = 0xFu << VisitedShift;

    // The row below the stripe, which vertically causal context formation treats as insignificant (D.7).
    private const uint BelowStripe = 7u << 15;

    // A sample's eight neighbours in a 9 bit window of the word (bit 4 is the sample), and those
    // left when the row below the stripe does not count.
    private const int Around = 0x1EF;
    private const int AroundCausal = 0x2F;

    // Zero coding contexts (Table D.1) by orientation and window: LL and LH, HL, HH.
    private static readonly byte[][] ZeroContexts = BuildZeroContexts();

    // Sign coding context (Table D.3) and the XOR bit in bit 4, from the direct neighbours.
    private static readonly byte[] SignContexts = BuildSignContexts();

    private readonly byte[] _states = new byte[Jpeg2000Mq.Contexts];
    private Jpeg2000Mq _mq;
    private byte[] _segment = new byte[64];
    private uint[] _columns = [];
    private int[] _data = [];
    private int _width;
    private int _height;
    private int _stride;

    /// <summary>Decodes the code-block's segments into coefficients, row by row, width times height.</summary>
    /// <param name="orientation">0 for LL, 1 HL, 2 LH, 3 HH.</param>
    /// <param name="planes">Magnitude bit-planes from the most significant coded one, ROI shift included.</param>
    public int[] Decode(Jpeg2000CodeBlock block, int orientation, int style, int planes)
    {
        _width = block.X1 - block.X0;
        _height = block.Y1 - block.Y0;
        _stride = _width + 2;
        int size = _stride * (((_height + 3) >> 2) + 2);
        if (_columns.Length < size) _columns = new uint[size];
        else Array.Clear(_columns, 0, size);
        if (_data.Length < _width * _height) _data = new int[_width * _height];
        else Array.Clear(_data, 0, _width * _height);

        if (block.Segments is null || planes <= 0) return _data;
        if (planes > 30) throw new Jpeg2000FormatException("A code-block has too many bit-planes.");
        byte[] zero = ZeroContexts[orientation == 1 ? 1 : orientation == 3 ? 2 : 0];
        bool causal = (style & Jpeg2000Coding.VerticallyCausal) != 0;
        Jpeg2000Mq.ResetContexts(_states);

        int plane = planes, type = 2;
        foreach (Jpeg2000Segment segment in block.Segments)
        {
            // The segment's bytes, then the two 0xFF bytes the decoder reads past its end.
            if (_segment.Length < segment.Length + 2) _segment = new byte[Math.Max(segment.Length + 2, _segment.Length * 2)];
            segment.CopyTo(_segment);
            _segment[segment.Length] = 0xFF;
            _segment[segment.Length + 1] = 0xFF;

            bool raw = (style & Jpeg2000Coding.Bypass) != 0 && segment.FirstPass >= 10 && (segment.FirstPass - 10) % 3 != 2;
            if (raw) _mq.StartRaw(_segment);
            else _mq.Start(_segment);
            for (int k = 0; k < segment.Passes; k++)
            {
                if (plane < 1) return _data;
                switch (type)
                {
                    case 0:
                        SignificancePass(plane, zero, causal, raw);
                        break;
                    case 1:
                        RefinementPass(plane, causal, raw);
                        break;
                    default:
                        CleanupPass(plane, zero, causal);
                        if ((style & Jpeg2000Coding.Segmentation) != 0)
                        {
                            // The segmentation symbol 1010 (D.5); errors are not concealed.
                            for (int i = 0; i < 4; i++) _mq.Decode(_states, Jpeg2000Mq.Uniform);
                        }

                        break;
                }

                if ((style & Jpeg2000Coding.Reset) != 0) Jpeg2000Mq.ResetContexts(_states);
                if (++type == 3)
                {
                    type = 0;
                    plane--;
                }
            }
        }

        return _data;
    }

    /// <summary>The window of sample row <paramref name="r"/>'s neighbours.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Window(uint word, int r, bool causal) => (int)(word >> (3 * r)) & (causal && r == 3 ? AroundCausal : Around);

    /// <summary>Marks the sample in row <paramref name="r"/> of column word <paramref name="c"/> significant, in its word and its neighbours'.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetSignificant(uint[] columns, int c, int stride, int r, bool negative)
    {
        int bit = 3 * (r + 1);
        columns[c] |= (1u << (bit + 1)) | (negative ? 1u << (SignShift + r) : 0);
        columns[c - 1] |= 1u << (bit + 2);
        columns[c + 1] |= 1u << bit;
        if (r == 0)
        {
            // The row below the stripe above.
            int above = c - stride;
            columns[above - 1] |= 1u << 17;
            columns[above] |= (1u << 16) | (negative ? 1u << (SignShift + 4) : 0);
            columns[above + 1] |= 1u << 15;
        }
        else if (r == 3)
        {
            // The row above the stripe below.
            int below = c + stride;
            columns[below - 1] |= 1u << 2;
            columns[below] |= (1u << 1) | (negative ? 1u << (SignShift - 1) : 0);
            columns[below + 1] |= 1u;
        }
    }

    /// <summary>The sign of sample row <paramref name="r"/> (D.3.2), from its direct neighbours' significance and signs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DecodeSign(ref Jpeg2000Mq mq, byte[] states, uint[] columns, int c, int r, bool causal)
    {
        uint word = columns[c];
        int bit = 3 * r;
        int index = (int)((word >> (bit + 1)) & 1) | (int)((word >> (bit + 1)) & 4) | (int)((word >> (bit + 2)) & 8)
            | (int)(((word >> (SignShift - 1 + r)) & 1) << 4) | (int)(((columns[c - 1] >> (SignShift + r)) & 1) << 6) | (int)(((columns[c + 1] >> (SignShift + r)) & 1) << 7);
        if (!(causal && r == 3)) index |= (int)((word >> (bit + 6)) & 2) | (int)(((word >> (SignShift + 1 + r)) & 1) << 5);
        byte sc = SignContexts[index];
        return (mq.Decode(states, sc & 0xF) ^ (sc >> 4)) != 0;
    }

    /// <summary>Significance propagation (D.3.1): insignificant samples with a significant neighbour.</summary>
    private void SignificancePass(int plane, byte[] zero, bool causal, bool raw)
    {
        int one = 1 << plane, value = one | (one >> 1);
        uint[] columns = _columns;
        int[] data = _data;
        byte[] states = _states;
        int stride = _stride, width = _width, height = _height;
        Jpeg2000Mq mq = _mq;
        for (int y0 = 0; y0 < height; y0 += 4)
        {
            int rows = Math.Min(4, height - y0);
            int c = (((y0 >> 2) + 1) * stride) + 1;
            for (int x = 0; x < width; x++, c++)
            {
                // Nothing to code in a column without a significant sample around it.
                if ((columns[c] & Neighbourhood) == 0) continue;
                for (int r = 0, i = (y0 * width) + x; r < rows; r++, i += width)
                {
                    uint word = columns[c];
                    if ((word & (1u << ((3 * r) + 4))) != 0) continue;
                    int window = Window(word, r, causal);
                    if (window == 0) continue;
                    int bit = raw ? mq.DecodeRaw() : mq.Decode(states, zero[window]);
                    if (bit != 0)
                    {
                        bool negative = raw ? mq.DecodeRaw() != 0 : DecodeSign(ref mq, states, columns, c, r, causal);
                        data[i] = negative ? -value : value;
                        SetSignificant(columns, c, stride, r, negative);
                    }

                    columns[c] |= 1u << (VisitedShift + r);
                }
            }
        }

        _mq = mq;
    }

    /// <summary>Magnitude refinement (D.3.3): samples significant before this bit-plane.</summary>
    private void RefinementPass(int plane, bool causal, bool raw)
    {
        int half = (1 << plane) >> 1;
        uint[] columns = _columns;
        int[] data = _data;
        byte[] states = _states;
        int stride = _stride, width = _width, height = _height;
        Jpeg2000Mq mq = _mq;
        for (int y0 = 0; y0 < height; y0 += 4)
        {
            int rows = Math.Min(4, height - y0);
            int c = (((y0 >> 2) + 1) * stride) + 1;
            for (int x = 0; x < width; x++, c++)
            {
                uint word = columns[c];
                if ((word & OwnSignificance) == 0) continue;
                for (int r = 0, i = (y0 * width) + x; r < rows; r++, i += width)
                {
                    // Significant and not visited by this bit-plane's significance pass.
                    if ((word & ((1u << ((3 * r) + 4)) | (1u << (VisitedShift + r)))) != 1u << ((3 * r) + 4)) continue;
                    int bit;
                    if (raw) bit = mq.DecodeRaw();
                    else bit = mq.Decode(states, (word & (1u << (RefinedShift + r))) != 0 ? 16 : Window(word, r, causal) != 0 ? 15 : 14);
                    data[i] += (bit != 0) ^ (data[i] < 0) ? half : -half;
                    word |= 1u << (RefinedShift + r);
                }

                columns[c] = word;
            }
        }

        _mq = mq;
    }

    /// <summary>
    /// Cleanup (D.3.4): the remaining samples, with run-length coding of empty columns. The pass
    /// ends the bit-plane, so it also forgets which samples the significance pass visited.
    /// </summary>
    private void CleanupPass(int plane, byte[] zero, bool causal)
    {
        int one = 1 << plane, value = one | (one >> 1);
        uint[] columns = _columns;
        int[] data = _data;
        byte[] states = _states;
        int stride = _stride, width = _width, height = _height;
        uint busy = Neighbourhood | VisitedBits;
        if (causal) busy &= ~BelowStripe;
        Jpeg2000Mq mq = _mq;
        for (int y0 = 0; y0 < height; y0 += 4)
        {
            int rows = Math.Min(4, height - y0);
            int c = (((y0 >> 2) + 1) * stride) + 1;
            for (int x = 0; x < width; x++, c++)
            {
                int i0 = (y0 * width) + x;
                int start = 0;
                if (rows == 4 && (columns[c] & busy) == 0)
                {
                    // Four samples with nothing significant around them: a run, or the first to become significant.
                    if (mq.Decode(states, Jpeg2000Mq.RunLength) == 0) continue;
                    start = mq.Decode(states, Jpeg2000Mq.Uniform) << 1;
                    start |= mq.Decode(states, Jpeg2000Mq.Uniform);
                    bool negative = DecodeSign(ref mq, states, columns, c, start, causal);
                    data[i0 + (start * width)] = negative ? -value : value;
                    SetSignificant(columns, c, stride, start, negative);
                    start++;
                }

                for (int r = start, i = i0 + (start * width); r < rows; r++, i += width)
                {
                    uint word = columns[c];
                    if ((word & ((1u << ((3 * r) + 4)) | (1u << (VisitedShift + r)))) != 0) continue;
                    if (mq.Decode(states, zero[Window(word, r, causal)]) == 0) continue;
                    bool negative = DecodeSign(ref mq, states, columns, c, r, causal);
                    data[i] = negative ? -value : value;
                    SetSignificant(columns, c, stride, r, negative);
                }

                columns[c] &= ~VisitedBits;
            }
        }

        _mq = mq;
    }

    /// <summary>Zero coding contexts by the 9 bit window: NW, N, NE, W, the sample, E, SW, S, SE from bit 0.</summary>
    private static byte[][] BuildZeroContexts()
    {
        var tables = new byte[3][];
        for (int t = 0; t < 3; t++)
        {
            tables[t] = new byte[512];
            for (int n = 0; n < 512; n++)
            {
                int h = ((n >> 3) & 1) + ((n >> 5) & 1);
                int v = ((n >> 1) & 1) + ((n >> 7) & 1);
                int d = (n & 1) + ((n >> 2) & 1) + ((n >> 6) & 1) + ((n >> 8) & 1);
                tables[t][n] = t switch
                {
                    0 => ZeroContext(h, v, d),
                    1 => ZeroContext(v, h, d),
                    _ => DiagonalContext(h + v, d),
                };
            }
        }

        return tables;
    }

    /// <summary>Table D.1 for LL and LH bands; HL swaps the horizontal and vertical sums.</summary>
    private static byte ZeroContext(int h, int v, int d) => h switch
    {
        2 => 8,
        1 => v >= 1 ? (byte)7 : d >= 1 ? (byte)6 : (byte)5,
        _ => v == 2 ? (byte)4 : v == 1 ? (byte)3 : d >= 2 ? (byte)2 : (byte)d,
    };

    /// <summary>Table D.1 for HH bands.</summary>
    private static byte DiagonalContext(int hv, int d) => d switch
    {
        >= 3 => 8,
        2 => hv >= 1 ? (byte)7 : (byte)6,
        1 => hv >= 2 ? (byte)5 : hv == 1 ? (byte)4 : (byte)3,
        _ => hv >= 2 ? (byte)2 : (byte)hv,
    };

    private static byte[] BuildSignContexts()
    {
        var table = new byte[256];
        for (int n = 0; n < 256; n++)
        {
            // Low nibble: significance of N, S, W, E; high nibble: their signs.
            int Contribution(int sig, int sign) => (n & sig) == 0 ? 0 : (n & sign) != 0 ? -1 : 1;
            int h = Math.Max(-1, Math.Min(1, Contribution(4, 64) + Contribution(8, 128)));
            int v = Math.Max(-1, Math.Min(1, Contribution(1, 16) + Contribution(2, 32)));
            int context, xor;
            if (h == 0)
            {
                context = v == 0 ? 9 : 10;
                xor = v == -1 ? 1 : 0;
            }
            else
            {
                context = 12 + (h * v);
                xor = h == -1 ? 1 : 0;
            }

            table[n] = (byte)(context | (xor << 4));
        }

        return table;
    }
}
