namespace PdfNetStack.Imaging;

/// <summary>
/// The MQ arithmetic decoder (T.800 Annex C) over one codeword segment, plus the raw bit reader of
/// selective arithmetic coding bypass (D.6). Past the end both read 0xFF bytes, which act as a
/// marker: the decoder then feeds 1 bits, as the standard's software convention does.
/// </summary>
internal sealed class Jpeg2000Mq
{
    public const int Contexts = 19;
    public const int RunLength = 17;
    public const int Uniform = 18;

    // Table C.2: Qe, the next state after an MPS and after an LPS, and whether an LPS switches the MPS.
    private static readonly ushort[] Qe =
    [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
        0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
        0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
        0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    ];

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

    // Per context: state index times two plus the MPS.
    private readonly int[] _states = new int[Contexts];
    private byte[] _data = [];
    private int _pos;
    private int _end;
    private uint _c;
    private uint _a;
    private int _ct;

    /// <summary>The initial states of Table D.7: uniform 46, run-length 3, the all-zero context 4.</summary>
    public void ResetContexts()
    {
        Array.Clear(_states, 0, Contexts);
        _states[0] = 4 << 1;
        _states[RunLength] = 3 << 1;
        _states[Uniform] = 46 << 1;
    }

    /// <summary>INITDEC (C.3.5) on a segment; the contexts are left as they are.</summary>
    public void Start(byte[] data, int offset, int length)
    {
        _data = data;
        _pos = offset;
        _end = offset + length;
        _c = (uint)Byte(_pos) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>Starts raw (bypass) reading of a segment.</summary>
    public void StartRaw(byte[] data, int offset, int length)
    {
        _data = data;
        _pos = offset;
        _end = offset + length;
        _c = 0;
        _ct = 0;
    }

    private int Byte(int at) => at < _end ? _data[at] : 0xFF;

    /// <summary>BYTEIN (C.3.4): a 0xFF followed by more than 0x8F is a marker and is not consumed.</summary>
    private void ByteIn()
    {
        int next = Byte(_pos + 1);
        if (Byte(_pos) == 0xFF)
        {
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
            _c += (uint)next << 8;
            _ct = 8;
        }
    }

    /// <summary>DECODE (C.3.2) with the conditional exchanges of C.3.3 and C.3.4.</summary>
    public int Decode(int context)
    {
        int s = _states[context];
        int index = s >> 1, mps = s & 1;
        uint qe = Qe[index];
        int d;
        _a -= qe;
        if ((_c >> 16) < qe)
        {
            if (_a < qe)
            {
                d = mps;
                _states[context] = (NextMps[index] << 1) | mps;
            }
            else
            {
                d = 1 - mps;
                _states[context] = (NextLps[index] << 1) | (mps ^ Switch[index]);
            }

            _a = qe;
        }
        else
        {
            _c -= qe << 16;
            if ((_a & 0x8000) != 0) return mps;
            if (_a < qe)
            {
                d = 1 - mps;
                _states[context] = (NextLps[index] << 1) | (mps ^ Switch[index]);
            }
            else
            {
                d = mps;
                _states[context] = (NextMps[index] << 1) | mps;
            }
        }

        // RENORMD (C.3.3).
        do
        {
            if (_ct == 0) ByteIn();
            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);
        return d;
    }

    /// <summary>One raw bit; after a 0xFF the stuffed 0 bit is skipped (D.6).</summary>
    public int DecodeRaw()
    {
        if (_ct == 0)
        {
            if (_c == 0xFF)
            {
                if (Byte(_pos) > 0x8F)
                {
                    _ct = 8;
                }
                else
                {
                    _c = (uint)Byte(_pos++);
                    _ct = 7;
                }
            }
            else
            {
                _c = (uint)Byte(_pos++);
                _ct = 8;
            }
        }

        _ct--;
        return (int)(_c >> _ct) & 1;
    }
}

/// <summary>
/// Tier-1 decoding of one code-block (T.800 Annex D): the significance propagation, magnitude
/// refinement and cleanup passes over stripes of four rows, with every code-block style flag.
/// Coefficients come out at twice their scale with the half-interval of the last decoded
/// bit-plane added, as OpenJPEG reconstructs them.
/// </summary>
internal sealed class Jpeg2000BlockDecoder
{
    // Per sample flags: significance of the eight neighbours, the signs of the four direct ones,
    // then the sample's own state.
    private const int SigN = 1, SigS = 2, SigW = 4, SigE = 8, SigNW = 16, SigNE = 32, SigSW = 64, SigSE = 128;
    private const int SgnN = 256, SgnS = 512, SgnW = 1024, SgnE = 2048;
    private const int Neighbours = 0xFF;
    private const int Significant = 1 << 12;
    private const int Visited = 1 << 13;
    private const int Refined = 1 << 14;

    // With vertically causal context formation the next stripe counts as insignificant (D.7).
    private const int CausalMask = ~(SigS | SigSW | SigSE | SgnS);

    // Zero coding contexts (Table D.1) by orientation: LL and LH, HL, HH.
    private static readonly byte[][] ZeroContexts = BuildZeroContexts();

    // Sign coding context (Table D.3) and the XOR bit in bit 4, from the direct neighbours.
    private static readonly byte[] SignContexts = BuildSignContexts();

    private readonly Jpeg2000Mq _mq = new();
    private int[] _flags = [];
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
        int size = _stride * (_height + 2);
        if (_flags.Length < size) _flags = new int[size];
        else Array.Clear(_flags, 0, size);
        if (_data.Length < _width * _height) _data = new int[_width * _height];
        else Array.Clear(_data, 0, _width * _height);

        if (block.Segments is null || planes <= 0) return _data;
        if (planes > 30) throw new Jpeg2000FormatException("A code-block has too many bit-planes.");
        byte[] zero = ZeroContexts[orientation == 1 ? 1 : orientation == 3 ? 2 : 0];
        bool causal = (style & Jpeg2000Coding.VerticallyCausal) != 0;
        _mq.ResetContexts();

        int plane = planes, type = 2, pass = 0;
        foreach (Jpeg2000Segment segment in block.Segments)
        {
            bool raw = (style & Jpeg2000Coding.Bypass) != 0 && segment.FirstPass >= 10 && (segment.FirstPass - 10) % 3 != 2;
            if (raw) _mq.StartRaw(segment.Data, 0, segment.Length);
            else _mq.Start(segment.Data, 0, segment.Length);
            for (int k = 0; k < segment.Passes; k++, pass++)
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
                            for (int i = 0; i < 4; i++) _mq.Decode(Jpeg2000Mq.Uniform);
                        }

                        break;
                }

                if ((style & Jpeg2000Coding.Reset) != 0) _mq.ResetContexts();
                if (++type == 3)
                {
                    type = 0;
                    plane--;
                }
            }
        }

        return _data;
    }

    private void SetSignificant(int f, bool negative)
    {
        int[] flags = _flags;
        int s = _stride;
        flags[f] |= Significant;
        flags[f - s] |= SigS | (negative ? SgnS : 0);
        flags[f + s] |= SigN | (negative ? SgnN : 0);
        flags[f - 1] |= SigE | (negative ? SgnE : 0);
        flags[f + 1] |= SigW | (negative ? SgnW : 0);
        flags[f - s - 1] |= SigSE;
        flags[f - s + 1] |= SigSW;
        flags[f + s - 1] |= SigNE;
        flags[f + s + 1] |= SigNW;
    }

    private bool DecodeSign(int context)
    {
        byte sc = SignContexts[(context & 0xF) | ((context >> 4) & 0xF0)];
        return (_mq.Decode(sc & 0xF) ^ (sc >> 4)) != 0;
    }

    /// <summary>Significance propagation (D.3.1): insignificant samples with a significant neighbour.</summary>
    private void SignificancePass(int plane, byte[] zero, bool causal, bool raw)
    {
        int one = 1 << plane, value = one | (one >> 1);
        int[] flags = _flags, data = _data;
        for (int y0 = 0; y0 < _height; y0 += 4)
        {
            int rows = Math.Min(4, _height - y0);
            for (int x = 0; x < _width; x++)
            {
                for (int dy = 0; dy < rows; dy++)
                {
                    int f = ((y0 + dy + 1) * _stride) + x + 1;
                    int state = flags[f];
                    if (causal && dy == 3) state &= CausalMask;
                    if ((state & Significant) != 0 || (state & Neighbours) == 0) continue;
                    int bit = raw ? _mq.DecodeRaw() : _mq.Decode(zero[state & Neighbours]);
                    if (bit != 0)
                    {
                        bool negative = raw ? _mq.DecodeRaw() != 0 : DecodeSign(state);
                        data[((y0 + dy) * _width) + x] = negative ? -value : value;
                        SetSignificant(f, negative);
                    }

                    flags[f] |= Visited;
                }
            }
        }
    }

    /// <summary>Magnitude refinement (D.3.3): samples significant before this bit-plane.</summary>
    private void RefinementPass(int plane, bool causal, bool raw)
    {
        int half = (1 << plane) >> 1;
        int[] flags = _flags, data = _data;
        for (int y0 = 0; y0 < _height; y0 += 4)
        {
            int rows = Math.Min(4, _height - y0);
            for (int x = 0; x < _width; x++)
            {
                for (int dy = 0; dy < rows; dy++)
                {
                    int f = ((y0 + dy + 1) * _stride) + x + 1;
                    int state = flags[f];
                    if ((state & (Significant | Visited)) != Significant) continue;
                    if (causal && dy == 3) state &= CausalMask;
                    int bit;
                    if (raw) bit = _mq.DecodeRaw();
                    else bit = _mq.Decode((state & Refined) != 0 ? 16 : (state & Neighbours) != 0 ? 15 : 14);
                    int i = ((y0 + dy) * _width) + x;
                    data[i] += (bit != 0) ^ (data[i] < 0) ? half : -half;
                    flags[f] |= Refined;
                }
            }
        }
    }

    /// <summary>Cleanup (D.3.4): the remaining samples, with run-length coding of empty columns.</summary>
    private void CleanupPass(int plane, byte[] zero, bool causal)
    {
        int one = 1 << plane, value = one | (one >> 1);
        int[] flags = _flags, data = _data;
        int stride = _stride;
        for (int y0 = 0; y0 < _height; y0 += 4)
        {
            int rows = Math.Min(4, _height - y0);
            for (int x = 0; x < _width; x++)
            {
                int f0 = ((y0 + 1) * stride) + x + 1;
                int start = 0;
                if (rows == 4)
                {
                    int last = flags[f0 + (3 * stride)];
                    if (causal) last &= CausalMask;
                    const int Busy = Significant | Visited | Neighbours;
                    if (((flags[f0] | flags[f0 + stride] | flags[f0 + (2 * stride)] | last) & Busy) == 0)
                    {
                        if (_mq.Decode(Jpeg2000Mq.RunLength) == 0) continue;
                        start = _mq.Decode(Jpeg2000Mq.Uniform) << 1;
                        start |= _mq.Decode(Jpeg2000Mq.Uniform);
                        int f = f0 + (start * stride);
                        int state = flags[f];
                        if (causal && start == 3) state &= CausalMask;
                        bool negative = DecodeSign(state);
                        data[((y0 + start) * _width) + x] = negative ? -value : value;
                        SetSignificant(f, negative);
                        start++;
                    }
                }

                for (int dy = start; dy < rows; dy++)
                {
                    int f = f0 + (dy * stride);
                    int state = flags[f];
                    if ((state & (Significant | Visited)) != 0) continue;
                    if (causal && dy == 3) state &= CausalMask;
                    if (_mq.Decode(zero[state & Neighbours]) == 0) continue;
                    bool negative = DecodeSign(state);
                    data[((y0 + dy) * _width) + x] = negative ? -value : value;
                    SetSignificant(f, negative);
                }
            }

            // The pass ends the bit-plane: forget which samples it visited.
            for (int dy = 0; dy < rows; dy++)
            {
                int row = (y0 + dy + 1) * stride;
                for (int x = 1; x <= _width; x++) flags[row + x] &= ~Visited;
            }
        }
    }

    private static byte[][] BuildZeroContexts()
    {
        var tables = new byte[3][];
        for (int t = 0; t < 3; t++)
        {
            tables[t] = new byte[256];
            for (int n = 0; n < 256; n++)
            {
                int h = ((n & SigW) != 0 ? 1 : 0) + ((n & SigE) != 0 ? 1 : 0);
                int v = ((n & SigN) != 0 ? 1 : 0) + ((n & SigS) != 0 ? 1 : 0);
                int d = ((n & SigNW) != 0 ? 1 : 0) + ((n & SigNE) != 0 ? 1 : 0) + ((n & SigSW) != 0 ? 1 : 0) + ((n & SigSE) != 0 ? 1 : 0);
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
