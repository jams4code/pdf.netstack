namespace PdfNetStack.Imaging;

/// <summary>
/// A codeword segment of a code-block: passes coded without termination between them. Its bytes
/// stay in the tile data, one piece per packet that carries some.
/// </summary>
internal sealed class Jpeg2000Segment
{
    private byte[] _source = [];
    private int _offset;
    private List<(byte[] Source, int Offset, int Length)>? _more;

    public Jpeg2000Segment(int firstPass, int maxPasses)
    {
        FirstPass = firstPass;
        MaxPasses = maxPasses;
    }

    public int FirstPass { get; }

    public int MaxPasses { get; }

    public int Passes { get; set; }

    public int Length { get; private set; }

    public void Append(byte[] source, int offset, int count)
    {
        if (count == 0) return;
        if (Length == 0)
        {
            _source = source;
            _offset = offset;
        }
        else
        {
            (_more ??= []).Add((source, offset, count));
        }

        Length += count;
    }

    /// <summary>Copies the segment's bytes, in order, to <paramref name="destination"/>.</summary>
    public void CopyTo(byte[] destination)
    {
        if (Length == 0) return;
        int first = Length, at = 0;
        if (_more is not null)
        {
            foreach ((_, _, int length) in _more) first -= length;
        }

        Buffer.BlockCopy(_source, _offset, destination, 0, first);
        at += first;
        if (_more is null) return;
        foreach ((byte[] source, int offset, int length) in _more)
        {
            Buffer.BlockCopy(source, offset, destination, at, length);
            at += length;
        }
    }
}

/// <summary>One code-block (T.800 B.7), in its subband's coordinates.</summary>
internal sealed class Jpeg2000CodeBlock
{
    public int X0 { get; init; }

    public int Y0 { get; init; }

    public int X1 { get; init; }

    public int Y1 { get; init; }

    public bool Included { get; set; }

    public int ZeroPlanes { get; set; }

    public int Lblock { get; set; } = 3;

    public int Passes { get; set; }

    public List<Jpeg2000Segment>? Segments { get; set; }
}

/// <summary>A tag tree (T.800 B.10.2): minimum values over a grid of leaves, coded top down.</summary>
internal sealed class Jpeg2000TagTree
{
    private readonly int[] _value;
    private readonly int[] _low;
    private readonly int[] _parent;
    private readonly int[] _path = new int[32];

    public Jpeg2000TagTree(int width, int height)
    {
        // Levels of halved grids up to a single root; leaves first.
        var widths = new List<int>();
        var heights = new List<int>();
        int w = width, h = height, nodes = 0;
        while (true)
        {
            widths.Add(w);
            heights.Add(h);
            nodes += w * h;
            if (w == 1 && h == 1) break;
            w = (w + 1) >> 1;
            h = (h + 1) >> 1;
        }

        _value = new int[nodes];
        _low = new int[nodes];
        _parent = new int[nodes];
        _value.AsSpan().Fill(int.MaxValue);
        int start = 0;
        for (int level = 0; level < widths.Count; level++)
        {
            int next = start + (widths[level] * heights[level]);
            for (int y = 0; y < heights[level]; y++)
            {
                for (int x = 0; x < widths[level]; x++)
                {
                    int i = start + (y * widths[level]) + x;
                    _parent[i] = level + 1 < widths.Count ? next + ((y >> 1) * widths[level + 1]) + (x >> 1) : -1;
                }
            }

            start = next;
        }
    }

    /// <summary>Reads until the leaf's value is known to be below the threshold or not; true when it is.</summary>
    public bool Decode(Jpeg2000HeaderBits bits, int leaf, int threshold)
    {
        int depth = 0, node = leaf;
        while (_parent[node] >= 0)
        {
            _path[depth++] = node;
            node = _parent[node];
        }

        int low = 0;
        while (true)
        {
            if (low > _low[node]) _low[node] = low;
            else low = _low[node];
            while (low < threshold && low < _value[node])
            {
                if (bits.Read() != 0) _value[node] = low;
                else low++;
            }

            _low[node] = low;
            if (depth == 0) break;
            node = _path[--depth];
        }

        return _value[node] < threshold;
    }

    public int Value(int leaf) => _value[leaf];
}

/// <summary>Thrown inside tier-2 when the packet data runs out; what was read so far is kept.</summary>
internal sealed class Jpeg2000EndOfDataException : Exception
{
    public Jpeg2000EndOfDataException() : base("The packet data ends early.") { }

    public Jpeg2000EndOfDataException(string message) : base(message) { }

    public Jpeg2000EndOfDataException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Packet header bits (T.800 B.10.1): after a 0xFF byte the next byte has seven.</summary>
internal sealed class Jpeg2000HeaderBits
{
    private byte[] _data = [];
    private int _end;
    private int _buffer;
    private int _count;

    public int Position { get; set; }

    public void Start(byte[] data, int position, int end)
    {
        _data = data;
        Position = position;
        _end = end;
        _buffer = 0;
        _count = 0;
    }

    public int Read()
    {
        if (_count == 0)
        {
            if (Position >= _end) throw new Jpeg2000EndOfDataException();
            _count = _buffer == 0xFF ? 7 : 8;
            _buffer = _data[Position++];
        }

        _count--;
        return (_buffer >> _count) & 1;
    }

    public int Read(int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++) v = (v << 1) | Read();
        return v;
    }

    /// <summary>Ends the header on a byte boundary; a final 0xFF is followed by a stuffed byte.</summary>
    public void Align()
    {
        if (_buffer == 0xFF && _count < 8)
        {
            if (Position >= _end) throw new Jpeg2000EndOfDataException();
            Position++;
        }

        _buffer = 0;
        _count = 0;
    }
}

/// <summary>A subband of one resolution (T.800 B.5).</summary>
internal sealed class Jpeg2000Band
{
    /// <summary>0 LL, 1 HL, 2 LH, 3 HH.</summary>
    public int Orientation { get; init; }

    public int X0 { get; init; }

    public int Y0 { get; init; }

    public int X1 { get; init; }

    public int Y1 { get; init; }

    /// <summary>Mb (E-2): magnitude bit-planes, guard bits included.</summary>
    public int Planes { get; init; }

    /// <summary>The quantization step size (E-3), 1 for reversible coding.</summary>
    public float Step { get; init; }
}

/// <summary>The code-blocks of one subband inside one precinct, with their tag trees.</summary>
internal sealed class Jpeg2000PrecinctBand
{
    public Jpeg2000PrecinctBand(Jpeg2000CodeBlock[] blocks, int across, int down)
    {
        Blocks = blocks;
        Inclusion = new Jpeg2000TagTree(across, down);
        ZeroPlanes = new Jpeg2000TagTree(across, down);
    }

    public Jpeg2000CodeBlock[] Blocks { get; }

    public Jpeg2000TagTree Inclusion { get; }

    public Jpeg2000TagTree ZeroPlanes { get; }
}

/// <summary>One resolution level of a tile-component (T.800 B.5, B.6).</summary>
internal sealed class Jpeg2000Resolution
{
    public int X0 { get; init; }

    public int Y0 { get; init; }

    public int X1 { get; init; }

    public int Y1 { get; init; }

    public int PrecinctWidth { get; init; }

    public int PrecinctHeight { get; init; }

    public int PrecinctsAcross { get; init; }

    public int PrecinctsDown { get; init; }

    /// <summary>Code-block size exponents after the precinct limit (B.7).</summary>
    public int BlockWidth { get; init; }

    public int BlockHeight { get; init; }

    public Jpeg2000Band[] Bands { get; init; } = [];

    /// <summary>Precincts made when their first packet arrives.</summary>
    public Jpeg2000PrecinctBand[]?[] Precincts { get; init; } = [];

    /// <summary>The next layer each precinct expects.</summary>
    public int[] NextLayer { get; init; } = [];
}

/// <summary>A tile-component: its resolutions and coding parameters.</summary>
internal sealed class Jpeg2000TileComponent
{
    public int X0 { get; init; }

    public int Y0 { get; init; }

    public int X1 { get; init; }

    public int Y1 { get; init; }

    public Jpeg2000Coding Coding { get; init; } = new();

    public int RoiShift { get; init; }

    public Jpeg2000Resolution[] Resolutions { get; init; } = [];

    public int Width => X1 - X0;

    public int Height => Y1 - Y0;
}

/// <summary>
/// Tier-2 decoding of one tile (T.800 Annex B): the tile-component, resolution, subband, precinct
/// and code-block structure, the progression orders and progression changes, and packet headers
/// with their tag trees, read from the tile data or from PPM and PPT.
/// </summary>
internal sealed class Jpeg2000Packets
{
    private readonly Jpeg2000HeaderBits _bits = new();
    private readonly List<(Jpeg2000Segment Segment, int Length)> _pending = [];
    private readonly byte[] _body;
    private readonly int _bodyEnd;
    private readonly byte[]? _headers;
    private readonly bool _sop;
    private readonly bool _eph;
    private readonly int _layers;
    private int _bodyPos;
    private int _headerPos;
    private long _work;
    private readonly long _workLimit;
    private long _blocks;
    private readonly long _blockLimit;

    /// <param name="body">Holds the tile's data from <paramref name="bodyStart"/> to <paramref name="bodyEnd"/>.</param>
    public Jpeg2000Packets(Jpeg2000TileComponent[] components, byte[] body, int bodyStart, int bodyEnd, byte[]? headers, int layers, bool sop, bool eph, long blockLimit)
    {
        Components = components;
        _body = body;
        _bodyPos = bodyStart;
        _bodyEnd = bodyEnd;
        _headers = headers;
        _layers = layers;
        _sop = sop;
        _eph = eph;

        // Every packet costs a byte or more, so iterations beyond a multiple of the data are hostile.
        _workLimit = (64L * (bodyEnd - bodyStart + (headers?.Length ?? 0))) + 65536;
        _blockLimit = blockLimit;
    }

    public Jpeg2000TileComponent[] Components { get; }

    /// <summary>Reads the packets in progression order until every one is read or the data ends.</summary>
    public void Read(List<Jpeg2000Progression> progressions)
    {
        try
        {
            foreach (Jpeg2000Progression volume in progressions) ReadVolume(volume);
        }
        catch (Jpeg2000EndOfDataException)
        {
            // A truncated tile keeps the packets before the cut, as viewers draw them.
        }
    }

    private void ReadVolume(Jpeg2000Progression v)
    {
        int layers = Math.Min(v.LayerEnd, _layers);
        int cEnd = Math.Min(v.ComponentEnd, Components.Length);
        int rEnd = v.ResolutionEnd;
        switch (v.Order)
        {
            case 0:
                // LRCP
                for (int l = 0; l < layers; l++)
                {
                    for (int r = v.ResolutionStart; r < rEnd; r++)
                    {
                        for (int c = v.ComponentStart; c < cEnd; c++) AllPrecincts(c, r, l);
                    }
                }

                break;
            case 1:
                // RLCP
                for (int r = v.ResolutionStart; r < rEnd; r++)
                {
                    for (int l = 0; l < layers; l++)
                    {
                        for (int c = v.ComponentStart; c < cEnd; c++) AllPrecincts(c, r, l);
                    }
                }

                break;
            default:
                Positional(v, layers, cEnd, rEnd);
                break;
        }
    }

    private void AllPrecincts(int c, int r, int layer)
    {
        Work(1);
        Jpeg2000TileComponent tc = Components[c];
        if (r > tc.Coding.Levels) return;
        Jpeg2000Resolution res = tc.Resolutions[r];
        int count = res.PrecinctsAcross * res.PrecinctsDown;
        for (int k = 0; k < count; k++) Packet(tc, res, k, layer);
    }

    /// <summary>
    /// RPCL, PCRL and CPRL (T.800 B.12.1.3 to B.12.1.5). Each precinct is placed where the
    /// standard's walk over the reference grid meets it, then the precincts are sorted.
    /// </summary>
    private void Positional(Jpeg2000Progression v, int layers, int cEnd, int rEnd)
    {
        var entries = new List<(int C, int R, int K, long Y, long X)>();
        for (int c = v.ComponentStart; c < cEnd; c++)
        {
            Jpeg2000TileComponent tc = Components[c];
            int dx = ComponentDx[c], dy = ComponentDy[c];
            for (int r = v.ResolutionStart; r < rEnd && r <= tc.Coding.Levels; r++)
            {
                Jpeg2000Resolution res = tc.Resolutions[r];
                int n = tc.Coding.Levels - r;
                for (int py = 0; py < res.PrecinctsDown; py++)
                {
                    long y = Origin(res.Y0, res.PrecinctHeight, py, dy, n, TileY0);
                    for (int px = 0; px < res.PrecinctsAcross; px++)
                    {
                        Work(1);
                        long x = Origin(res.X0, res.PrecinctWidth, px, dx, n, TileX0);
                        entries.Add((c, r, (py * res.PrecinctsAcross) + px, y, x));
                    }
                }
            }
        }

        Comparison<(int C, int R, int K, long Y, long X)> order = v.Order switch
        {
            2 => (a, b) => a.R != b.R ? a.R.CompareTo(b.R) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.C.CompareTo(b.C),
            3 => (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.C != b.C ? a.C.CompareTo(b.C) : a.R.CompareTo(b.R),
            _ => (a, b) => a.C != b.C ? a.C.CompareTo(b.C) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.R.CompareTo(b.R),
        };
        entries.Sort(order);
        foreach ((int c, int r, int k, _, _) in entries)
        {
            Jpeg2000TileComponent tc = Components[c];
            Jpeg2000Resolution res = tc.Resolutions[r];
            for (int l = res.NextLayer[k]; l < layers; l++) Packet(tc, res, k, l);
        }
    }

    /// <summary>
    /// The reference grid coordinate at which the walk of B.12.1.3 reaches a precinct row or
    /// column: the tile's edge for a first precinct that is cut by it, else the precinct's corner.
    /// </summary>
    private static long Origin(int start, int exponent, int index, int separation, int levels, int tileStart)
    {
        if (index == 0 && (start & ((1 << exponent) - 1)) != 0) return tileStart;
        long corner = index == 0 ? start : ((long)(start >> exponent) + index) << exponent;
        return (corner << levels) * separation;
    }

    public int[] ComponentDx { get; init; } = [];

    public int[] ComponentDy { get; init; } = [];

    public int TileX0 { get; init; }

    public int TileY0 { get; init; }

    private void Work(long amount)
    {
        _work += amount;
        if (_work > _workLimit) throw new Jpeg2000FormatException("The packet structure exceeds the decoding work limit.");
    }

    /// <summary>One packet (T.800 B.9, B.10) if it is the precinct's next.</summary>
    private void Packet(Jpeg2000TileComponent tc, Jpeg2000Resolution res, int k, int layer)
    {
        Work(1);
        if (res.NextLayer[k] != layer) return;
        res.NextLayer[k]++;
        Jpeg2000PrecinctBand[] precinct = res.Precincts[k] ??= MakePrecinct(tc, res, k);

        // SOP may precede each packet in the tile data (A.8.1).
        if (_sop && _bodyPos + 6 <= _bodyEnd && _body[_bodyPos] == 0xFF && _body[_bodyPos + 1] == 0x91) _bodyPos += 6;

        byte[] source = _headers ?? _body;
        int sourceEnd = _headers?.Length ?? _bodyEnd;
        _bits.Start(source, _headers is null ? _bodyPos : _headerPos, sourceEnd);
        _pending.Clear();
        if (_bits.Read() != 0)
        {
            foreach (Jpeg2000PrecinctBand band in precinct)
            {
                for (int i = 0; i < band.Blocks.Length; i++) BlockHeader(band, i, layer, tc);
            }
        }

        _bits.Align();
        int pos = _bits.Position;

        // EPH ends the header when the coding style says it is used (A.8.2).
        if (_eph && pos + 2 <= sourceEnd && source[pos] == 0xFF && source[pos + 1] == 0x92) pos += 2;
        if (_headers is null) _bodyPos = pos;
        else _headerPos = pos;

        foreach ((Jpeg2000Segment segment, int length) in _pending)
        {
            int take = Math.Min(length, _bodyEnd - _bodyPos);
            segment.Append(_body, _bodyPos, take);
            _bodyPos += take;
            if (take < length) throw new Jpeg2000EndOfDataException();
        }
    }

    private void BlockHeader(Jpeg2000PrecinctBand band, int i, int layer, Jpeg2000TileComponent tc)
    {
        Jpeg2000CodeBlock block = band.Blocks[i];
        bool included = block.Included ? _bits.Read() != 0 : band.Inclusion.Decode(_bits, i, layer + 1);
        if (!included) return;
        if (!block.Included)
        {
            // Zero bit-planes: the smallest threshold the value is below, less one.
            int planes = 0;
            while (!band.ZeroPlanes.Decode(_bits, i, planes + 1))
            {
                if (++planes > 74) throw new Jpeg2000FormatException("A code-block has too many zero bit-planes.");
            }

            block.ZeroPlanes = planes;
            block.Included = true;
            block.Segments = [];
        }

        int passes = PassCount();
        while (_bits.Read() != 0)
        {
            if (++block.Lblock > 32) throw new Jpeg2000FormatException("A code-block length indicator is too large.");
        }

        if (block.Passes + passes > 3 * 40) throw new Jpeg2000FormatException("A code-block has too many coding passes.");
        int style = tc.Coding.Style;
        List<Jpeg2000Segment> segments = block.Segments!;
        Jpeg2000Segment? current = segments.Count > 0 && segments[^1].Passes < segments[^1].MaxPasses ? segments[^1] : null;
        int pass = block.Passes;
        while (passes > 0)
        {
            if (current is null)
            {
                current = new Jpeg2000Segment(pass, MaxPasses(pass, style));
                segments.Add(current);
            }

            int take = Math.Min(passes, current.MaxPasses - current.Passes);
            int bits = block.Lblock + FloorLog2(take);
            if (bits > 31) throw new Jpeg2000FormatException("A code-block segment length is too large.");
            int length = _bits.Read(bits);
            current.Passes += take;
            pass += take;
            passes -= take;
            _pending.Add((current, length));
            current = null;
        }

        block.Passes = pass;
    }

    /// <summary>Passes a segment holds from its first pass (D.4.1, Table D.9).</summary>
    private static int MaxPasses(int first, int style)
    {
        if ((style & Jpeg2000Coding.TerminateAll) != 0) return 1;
        if ((style & Jpeg2000Coding.Bypass) == 0) return int.MaxValue;
        if (first < 10) return 10 - first;
        return (first - 10) % 3 == 0 ? 2 : 1;
    }

    private static int FloorLog2(int v)
    {
        int n = 0;
        while ((v >>= 1) != 0) n++;
        return n;
    }

    /// <summary>The number of coding passes (Table B.4).</summary>
    private int PassCount()
    {
        if (_bits.Read() == 0) return 1;
        if (_bits.Read() == 0) return 2;
        int n = _bits.Read(2);
        if (n < 3) return 3 + n;
        n = _bits.Read(5);
        if (n < 31) return 6 + n;
        return 37 + _bits.Read(7);
    }

    /// <summary>The code-blocks of a precinct, band by band (B.6, B.7).</summary>
    private Jpeg2000PrecinctBand[] MakePrecinct(Jpeg2000TileComponent tc, Jpeg2000Resolution res, int k)
    {
        int px = k % res.PrecinctsAcross, py = k / res.PrecinctsAcross;
        long left = ((long)(res.X0 >> res.PrecinctWidth) + px) << res.PrecinctWidth;
        long top = ((long)(res.Y0 >> res.PrecinctHeight) + py) << res.PrecinctHeight;
        long right = left + (1L << res.PrecinctWidth), bottom = top + (1L << res.PrecinctHeight);
        bool low = res.Bands.Length == 1;
        var bands = new Jpeg2000PrecinctBand[res.Bands.Length];
        for (int b = 0; b < res.Bands.Length; b++)
        {
            Jpeg2000Band band = res.Bands[b];

            // Above resolution 0 a precinct covers half as much of each subband.
            long x0 = Math.Max(band.X0, low ? left : left >> 1), x1 = Math.Min(band.X1, low ? right : right >> 1);
            long y0 = Math.Max(band.Y0, low ? top : top >> 1), y1 = Math.Min(band.Y1, low ? bottom : bottom >> 1);
            if (x0 >= x1 || y0 >= y1)
            {
                bands[b] = new Jpeg2000PrecinctBand([], 1, 1);
                continue;
            }

            int cw = res.BlockWidth, ch = res.BlockHeight;
            long bx0 = x0 >> cw, by0 = y0 >> ch, bx1 = (x1 + (1L << cw) - 1) >> cw, by1 = (y1 + (1L << ch) - 1) >> ch;
            long across = bx1 - bx0, down = by1 - by0;
            _blocks += across * down;
            if (_blocks > _blockLimit) throw new Jpeg2000FormatException("The image has too many code-blocks.");
            var blocks = new Jpeg2000CodeBlock[across * down];
            for (long y = 0; y < down; y++)
            {
                for (long x = 0; x < across; x++)
                {
                    blocks[(y * across) + x] = new Jpeg2000CodeBlock
                    {
                        X0 = (int)Math.Max(x0, (bx0 + x) << cw),
                        Y0 = (int)Math.Max(y0, (by0 + y) << ch),
                        X1 = (int)Math.Min(x1, (bx0 + x + 1) << cw),
                        Y1 = (int)Math.Min(y1, (by0 + y + 1) << ch),
                    };
                }
            }

            bands[b] = new Jpeg2000PrecinctBand(blocks, (int)across, (int)down);
        }

        return bands;
    }
}
