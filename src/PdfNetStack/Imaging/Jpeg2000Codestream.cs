namespace PdfNetStack.Imaging;

/// <summary>Thrown for JPEG 2000 data that is malformed, too large or uses a feature this codec does not support.</summary>
internal sealed class Jpeg2000FormatException : Exception
{
    public Jpeg2000FormatException(string message) : base(message) { }

    public Jpeg2000FormatException(string message, Exception inner) : base(message, inner) { }

    public Jpeg2000FormatException() { }
}

/// <summary>One component of the image as SIZ describes it (T.800 A.5.1).</summary>
internal sealed class Jpeg2000Component
{
    public int Depth { get; init; }

    public bool Signed { get; init; }

    /// <summary>XRsiz: horizontal sample separation on the reference grid.</summary>
    public int Dx { get; init; }

    /// <summary>YRsiz: vertical sample separation on the reference grid.</summary>
    public int Dy { get; init; }
}

/// <summary>The per component part of COD and COC (T.800 A.6.1, A.6.2).</summary>
internal sealed class Jpeg2000Coding
{
    public const int Bypass = 0x01;
    public const int Reset = 0x02;
    public const int TerminateAll = 0x04;
    public const int VerticallyCausal = 0x08;
    public const int Predictable = 0x10;
    public const int Segmentation = 0x20;

    public int Levels { get; init; }

    /// <summary>xcb: log2 of the nominal code-block width.</summary>
    public int BlockWidth { get; init; }

    /// <summary>ycb: log2 of the nominal code-block height.</summary>
    public int BlockHeight { get; init; }

    public int Style { get; init; }

    /// <summary>True for the 5/3 reversible wavelet, false for the 9/7 irreversible one.</summary>
    public bool Reversible { get; init; }

    /// <summary>PPx in the low nibble and PPy in the high one per resolution, or null for the maximum (15).</summary>
    public byte[]? Precincts { get; init; }

    public int PrecinctWidth(int resolution) => Precincts is null ? 15 : Precincts[resolution] & 15;

    public int PrecinctHeight(int resolution) => Precincts is null ? 15 : Precincts[resolution] >> 4;
}

/// <summary>QCD or QCC (T.800 A.6.4, A.6.5): guard bits and the step size of each subband.</summary>
internal sealed class Jpeg2000Quantization
{
    public int Style { get; init; }

    public int Guard { get; init; }

    public int[] Exponents { get; init; } = [];

    public int[] Mantissas { get; init; } = [];

    /// <summary>
    /// Exponent and mantissa of a subband: index 0 is the LL band, then HL, LH, HH per resolution.
    /// Derived quantization scales the LL values by the band's level (T.800 E.1.1.2).
    /// </summary>
    public (int Exponent, int Mantissa) Band(int index, int levels, int decompositions)
    {
        if (Style == 1) return (Exponents[0] - levels + decompositions, Mantissas[0]);
        if (index >= Exponents.Length) throw new Jpeg2000FormatException("Quantization does not cover every subband.");
        return (Exponents[index], Mantissas[index]);
    }
}

/// <summary>One progression order change (T.800 A.6.6): a volume of layers, resolutions and components.</summary>
internal readonly record struct Jpeg2000Progression(int ResolutionStart, int ComponentStart, int LayerEnd, int ResolutionEnd, int ComponentEnd, int Order);

/// <summary>Coding parameters of the main header, or of one tile on top of them.</summary>
internal sealed class Jpeg2000Parameters
{
    public Jpeg2000Parameters(int components)
    {
        ComponentCoding = new Jpeg2000Coding?[components];
        ComponentQuantization = new Jpeg2000Quantization?[components];
        RoiShift = new int?[components];
    }

    /// <summary>True when this level has a COD: its general part (order, layers, MCT, SOP, EPH) applies.</summary>
    public bool HasCod { get; set; }

    public int Order { get; set; }

    public int Layers { get; set; }

    public bool Mct { get; set; }

    public bool Sop { get; set; }

    public bool Eph { get; set; }

    public Jpeg2000Coding? Coding { get; set; }

    public Jpeg2000Coding?[] ComponentCoding { get; }

    public Jpeg2000Quantization? Quantization { get; set; }

    public Jpeg2000Quantization?[] ComponentQuantization { get; }

    public int?[] RoiShift { get; }

    public List<Jpeg2000Progression> Progressions { get; } = [];
}

/// <summary>What the codestream holds for one tile: its parameters, data and packed packet headers.</summary>
internal sealed class Jpeg2000TileParts
{
    public Jpeg2000TileParts(int components) => Parameters = new Jpeg2000Parameters(components);

    public Jpeg2000Parameters Parameters { get; }

    /// <summary>The bodies of the tile-parts in codestream order, as offsets and lengths.</summary>
    public List<(int Offset, int Length)> Bodies { get; } = [];

    /// <summary>PPT segments by Zppt.</summary>
    public SortedList<int, byte[]>? Ppt { get; set; }

    /// <summary>The PPM chunks of this tile's tile-parts, in order.</summary>
    public List<byte[]>? Ppm { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// A JPEG 2000 codestream (T.800 Annex A) split into the main header's parameters and the data of
/// each tile. Markers that only help random access (TLM, PLM, PLT) and comments are skipped.
/// </summary>
internal sealed class Jpeg2000Codestream
{
    private const int MaxTiles = 65535;
    private const int MaxComponents = 64;
    private const int MaxProgressions = 256;

    private Jpeg2000Codestream(byte[] data) => Data = data;

    public byte[] Data { get; }

    public int X0 { get; private set; }

    public int Y0 { get; private set; }

    public int X1 { get; private set; }

    public int Y1 { get; private set; }

    public int TileWidth { get; private set; }

    public int TileHeight { get; private set; }

    public int TileX0 { get; private set; }

    public int TileY0 { get; private set; }

    public int TilesAcross { get; private set; }

    public int TilesDown { get; private set; }

    public Jpeg2000Component[] Components { get; private set; } = [];

    public Jpeg2000Parameters Main { get; private set; } = new(0);

    public Jpeg2000TileParts?[] Tiles { get; private set; } = [];

    /// <summary>Reads the codestream at <paramref name="start"/> up to <paramref name="end"/>.</summary>
    /// <param name="maxPixels">Ceiling for the image area on the reference grid.</param>
    public static Jpeg2000Codestream Read(byte[] data, int start, int end, long maxPixels)
    {
        var stream = new Jpeg2000Codestream(data);
        stream.Parse(start, end, maxPixels);
        return stream;
    }

    private void Parse(int start, int end, long maxPixels)
    {
        int pos = start;
        if (end - pos < 4 || U16(pos) != 0xFF4F || U16(pos + 2) != 0xFF51) throw new Jpeg2000FormatException("Not a JPEG 2000 codestream.");
        pos += 2;

        // Main header (T.800 A.4.1): SIZ first, then anything up to the first SOT.
        bool siz = false;
        SortedList<int, byte[]>? ppm = null;
        while (true)
        {
            if (pos + 2 > end) throw new Jpeg2000FormatException("The main header is truncated.");
            int marker = U16(pos);
            if (marker == 0xFF90) break;
            if (marker == 0xFFD9) throw new Jpeg2000FormatException("The codestream has no tiles.");
            (int body, int length) = Segment(pos, end);
            pos = body + length;
            switch (marker)
            {
                case 0xFF51:
                    if (siz) throw new Jpeg2000FormatException("SIZ appears twice.");
                    ReadSiz(body, length, maxPixels);
                    siz = true;
                    break;
                case 0xFF52 or 0xFF53 or 0xFF5C or 0xFF5D or 0xFF5E or 0xFF5F:
                    if (!siz) throw new Jpeg2000FormatException("A marker precedes SIZ.");
                    ReadParameter(marker, body, length, Main);
                    break;
                case 0xFF60:
                    if (!siz) throw new Jpeg2000FormatException("A marker precedes SIZ.");
                    ppm ??= [];
                    if (length < 1) throw new Jpeg2000FormatException("PPM is too short.");
                    ppm[Data[body]] = Data.AsSpan(body + 1, length - 1).ToArray();
                    break;
            }
        }

        if (!siz || Main.Coding is null || Main.Quantization is null) throw new Jpeg2000FormatException("The main header lacks SIZ, COD or QCD.");
        Queue<byte[]>? ppmChunks = ppm is null ? null : PpmChunks(ppm);

        // Tile-parts (T.800 A.4.2): SOT, its header, SOD, then the data to the length Psot gives.
        Tiles = new Jpeg2000TileParts?[TilesAcross * TilesDown];
        while (pos + 2 <= end && U16(pos) == 0xFF90)
        {
            int sot = pos;
            (int body, int length) = Segment(pos, end);
            if (length < 8) throw new Jpeg2000FormatException("SOT is too short.");
            int tile = U16(body);
            long psot = U32(body + 2);
            if (tile >= Tiles.Length) throw new Jpeg2000FormatException("A tile-part names a tile that does not exist.");
            Jpeg2000TileParts parts = Tiles[tile] ??= new Jpeg2000TileParts(Components.Length);
            if (++parts.Count > 255) throw new Jpeg2000FormatException("A tile has too many tile-parts.");
            pos = body + length;

            int partEnd = psot == 0 ? end : (int)Math.Min(end, sot + psot);
            if (psot != 0 && psot < 14) throw new Jpeg2000FormatException("Psot is too small.");
            while (true)
            {
                if (pos + 2 > partEnd) throw new Jpeg2000FormatException("A tile-part header is truncated.");
                int marker = U16(pos);
                if (marker == 0xFF93)
                {
                    pos += 2;
                    break;
                }

                (int b, int l) = Segment(pos, partEnd);
                pos = b + l;
                switch (marker)
                {
                    case 0xFF52 or 0xFF53 or 0xFF5C or 0xFF5D or 0xFF5E or 0xFF5F:
                        ReadParameter(marker, b, l, parts.Parameters);
                        break;
                    case 0xFF61:
                        if (l < 1) throw new Jpeg2000FormatException("PPT is too short.");
                        parts.Ppt ??= [];
                        parts.Ppt[Data[b]] = Data.AsSpan(b + 1, l - 1).ToArray();
                        break;
                }
            }

            // With Psot 0 the data runs to EOC, the last two bytes when present.
            if (psot == 0 && partEnd - pos >= 2 && U16(partEnd - 2) == 0xFFD9) partEnd -= 2;
            parts.Bodies.Add((pos, Math.Max(0, partEnd - pos)));
            if (ppmChunks is not null)
            {
                if (ppmChunks.Count == 0) throw new Jpeg2000FormatException("PPM has fewer chunks than there are tile-parts.");
                (parts.Ppm ??= []).Add(ppmChunks.Dequeue());
            }

            pos = partEnd;
        }

        if (Array.TrueForAll(Tiles, t => t is null)) throw new Jpeg2000FormatException("The codestream has no tile data.");
    }

    /// <summary>A marker segment's body and length, checked against the end (T.800 A.1.4).</summary>
    private (int Body, int Length) Segment(int pos, int end)
    {
        if (pos + 4 > end) throw new Jpeg2000FormatException("A marker segment is truncated.");
        int marker = U16(pos);
        if ((marker >> 8) != 0xFF) throw new Jpeg2000FormatException("A marker was expected.");

        // Markers 0xFF30 to 0xFF3F have no segment.
        if (marker is >= 0xFF30 and <= 0xFF3F) return (pos + 2, 0);
        int length = U16(pos + 2);
        if (length < 2 || pos + 2 + length > end) throw new Jpeg2000FormatException("A marker segment is truncated.");
        return (pos + 4, length - 2);
    }

    private void ReadSiz(int p, int length, long maxPixels)
    {
        if (length < 36) throw new Jpeg2000FormatException("SIZ is too short.");
        long x1 = U32(p + 2), y1 = U32(p + 6), x0 = U32(p + 10), y0 = U32(p + 14);
        long tw = U32(p + 18), th = U32(p + 22), tx0 = U32(p + 26), ty0 = U32(p + 30);
        int count = U16(p + 34);
        if (x1 > int.MaxValue || y1 > int.MaxValue || x0 >= x1 || y0 >= y1) throw new Jpeg2000FormatException("The image area is invalid.");
        if (tw == 0 || th == 0 || tx0 > x0 || ty0 > y0 || tx0 + tw <= x0 || ty0 + th <= y0) throw new Jpeg2000FormatException("The tile grid is invalid.");
        if ((x1 - x0) * (y1 - y0) > maxPixels) throw new Jpeg2000FormatException("The image is too large.");
        if (count is < 1 or > MaxComponents || length < 36 + (3 * count)) throw new Jpeg2000FormatException("The component count is invalid or unsupported.");

        long across = (x1 - tx0 + tw - 1) / tw, down = (y1 - ty0 + th - 1) / th;
        if (across * down > MaxTiles) throw new Jpeg2000FormatException("The image has too many tiles.");

        var components = new Jpeg2000Component[count];
        for (int c = 0; c < count; c++)
        {
            int s = Data[p + 36 + (3 * c)], dx = Data[p + 37 + (3 * c)], dy = Data[p + 38 + (3 * c)];
            int depth = (s & 0x7F) + 1;
            if (depth > 16) throw new Jpeg2000FormatException($"Components of {depth} bits are not supported.");
            if (dx == 0 || dy == 0) throw new Jpeg2000FormatException("A component's subsampling is zero.");
            components[c] = new Jpeg2000Component { Depth = depth, Signed = (s & 0x80) != 0, Dx = dx, Dy = dy };
        }

        X0 = (int)x0;
        Y0 = (int)y0;
        X1 = (int)x1;
        Y1 = (int)y1;
        TileWidth = (int)Math.Min(tw, int.MaxValue);
        TileHeight = (int)Math.Min(th, int.MaxValue);
        TileX0 = (int)tx0;
        TileY0 = (int)ty0;
        TilesAcross = (int)across;
        TilesDown = (int)down;
        Components = components;
        Main = new Jpeg2000Parameters(count);
    }

    /// <summary>COD, COC, QCD, QCC, RGN or POC into the main header's or a tile's parameters.</summary>
    private void ReadParameter(int marker, int p, int length, Jpeg2000Parameters target)
    {
        int end = p + length;
        int componentBytes = Components.Length < 257 ? 1 : 2;
        switch (marker)
        {
            case 0xFF52:
            {
                // COD (A.6.1): Scod, then progression order, layers, MCT, then the SPcod coding style.
                if (length < 10) throw new Jpeg2000FormatException("COD is too short.");
                int scod = Data[p];
                target.HasCod = true;
                target.Order = Data[p + 1];
                target.Layers = U16(p + 2);
                target.Mct = Data[p + 4] == 1;
                target.Sop = (scod & 2) != 0;
                target.Eph = (scod & 4) != 0;
                if (target.Order > 4) throw new Jpeg2000FormatException("The progression order is invalid.");
                if (target.Layers == 0) throw new Jpeg2000FormatException("COD has no layers.");
                target.Coding = ReadCoding(p + 5, end, (scod & 1) != 0);
                break;
            }

            case 0xFF53:
            {
                int c = Component(p, end, componentBytes);
                if (p + componentBytes >= end) throw new Jpeg2000FormatException("COC is too short.");
                target.ComponentCoding[c] = ReadCoding(p + componentBytes + 1, end, (Data[p + componentBytes] & 1) != 0);
                break;
            }

            case 0xFF5C:
                target.Quantization = ReadQuantization(p, end);
                break;
            case 0xFF5D:
            {
                int c = Component(p, end, componentBytes);
                target.ComponentQuantization[c] = ReadQuantization(p + componentBytes, end);
                break;
            }

            case 0xFF5E:
            {
                // RGN (A.6.3): only the implicit (maximum shift) method exists in part 1.
                int c = Component(p, end, componentBytes);
                if (p + componentBytes + 2 > end) throw new Jpeg2000FormatException("RGN is too short.");
                if (Data[p + componentBytes] != 0) throw new Jpeg2000FormatException("The ROI style is not supported.");
                target.RoiShift[c] = Data[p + componentBytes + 1];
                break;
            }

            case 0xFF5F:
            {
                // POC (A.6.6): one or more progression volumes.
                int entry = 5 + (2 * componentBytes);
                if (length < entry || length % entry != 0) throw new Jpeg2000FormatException("POC has an invalid length.");
                for (int q = p; q < end; q += entry)
                {
                    if (target.Progressions.Count >= MaxProgressions) throw new Jpeg2000FormatException("There are too many progression changes.");
                    int rs = Data[q];
                    int cs = componentBytes == 1 ? Data[q + 1] : U16(q + 1);
                    int at = q + 1 + componentBytes;
                    int lye = U16(at), re = Data[at + 2];
                    int ce = componentBytes == 1 ? Data[at + 3] : U16(at + 3);
                    int order = Data[at + 3 + componentBytes];
                    if (ce == 0) ce = componentBytes == 1 ? 256 : 16384;
                    if (order > 4) throw new Jpeg2000FormatException("The progression order is invalid.");
                    target.Progressions.Add(new Jpeg2000Progression(rs, cs, lye, Math.Min(re, 33), Math.Min(ce, Components.Length), order));
                }

                break;
            }
        }
    }

    private int Component(int p, int end, int bytes)
    {
        if (p + bytes > end) throw new Jpeg2000FormatException("A marker segment is truncated.");
        int c = bytes == 1 ? Data[p] : U16(p);
        if (c >= Components.Length) throw new Jpeg2000FormatException("A marker names a component that does not exist.");
        return c;
    }

    /// <summary>SPcod or SPcoc (T.800 Table A.15).</summary>
    private Jpeg2000Coding ReadCoding(int p, int end, bool precincts)
    {
        if (p + 5 > end) throw new Jpeg2000FormatException("A coding style segment is truncated.");
        int levels = Data[p], xcb = (Data[p + 1] & 0xF) + 2, ycb = (Data[p + 2] & 0xF) + 2, style = Data[p + 3], transform = Data[p + 4];
        if (levels > 32) throw new Jpeg2000FormatException("There are more than 32 decomposition levels.");
        if (xcb > 10 || ycb > 10 || xcb + ycb > 12) throw new Jpeg2000FormatException("The code-block size is invalid.");
        if ((style & 0xC0) != 0) throw new Jpeg2000FormatException("High throughput or mixed code-blocks are not supported.");
        if (transform > 1) throw new Jpeg2000FormatException("Only the 5/3 and 9/7 wavelets are supported.");
        byte[]? sizes = null;
        if (precincts)
        {
            if (p + 5 + levels + 1 > end) throw new Jpeg2000FormatException("The precinct sizes are truncated.");
            sizes = Data.AsSpan(p + 5, levels + 1).ToArray();
            for (int r = 1; r <= levels; r++)
            {
                if ((sizes[r] & 15) == 0 || (sizes[r] >> 4) == 0) throw new Jpeg2000FormatException("A precinct size is invalid.");
            }
        }

        return new Jpeg2000Coding { Levels = levels, BlockWidth = xcb, BlockHeight = ycb, Style = style, Reversible = transform == 1, Precincts = sizes };
    }

    /// <summary>Sqcd and SPqcd (T.800 Table A.27 to A.30).</summary>
    private Jpeg2000Quantization ReadQuantization(int p, int end)
    {
        if (p >= end) throw new Jpeg2000FormatException("A quantization segment is truncated.");
        int s = Data[p], style = s & 0x1F;
        p++;
        int count = style switch
        {
            0 => end - p,
            1 => Math.Min(1, (end - p) / 2),
            2 => (end - p) / 2,
            _ => throw new Jpeg2000FormatException("The quantization style is invalid."),
        };
        if (count == 0) throw new Jpeg2000FormatException("A quantization segment is truncated.");
        var exponents = new int[count];
        var mantissas = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (style == 0)
            {
                exponents[i] = Data[p + i] >> 3;
            }
            else
            {
                int v = U16(p + (2 * i));
                exponents[i] = v >> 11;
                mantissas[i] = v & 0x7FF;
            }
        }

        return Quantization(style, s >> 5, exponents, mantissas);
    }

    private static Jpeg2000Quantization Quantization(int style, int guard, int[] exponents, int[] mantissas) =>
        new() { Style = style, Guard = guard, Exponents = exponents, Mantissas = mantissas };

    /// <summary>The PPM data cut at its Nppm lengths into one chunk per tile-part (T.800 A.7.4).</summary>
    private static Queue<byte[]> PpmChunks(SortedList<int, byte[]> segments)
    {
        int total = 0;
        foreach (byte[] s in segments.Values) total += s.Length;
        var all = new byte[total];
        int at = 0;
        foreach (byte[] s in segments.Values)
        {
            s.CopyTo(all, at);
            at += s.Length;
        }

        var chunks = new Queue<byte[]>();
        for (int p = 0; p + 4 <= all.Length;)
        {
            long n = ((long)all[p] << 24) | ((long)all[p + 1] << 16) | ((long)all[p + 2] << 8) | all[p + 3];
            p += 4;
            if (n > all.Length - p) throw new Jpeg2000FormatException("A PPM chunk is truncated.");
            chunks.Enqueue(all.AsSpan(p, (int)n).ToArray());
            p += (int)n;
        }

        return chunks;
    }

    private int U16(int p) => (Data[p] << 8) | Data[p + 1];

    private long U32(int p) => ((long)Data[p] << 24) | ((long)Data[p + 1] << 16) | ((long)Data[p + 2] << 8) | Data[p + 3];
}
