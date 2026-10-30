using System.Runtime.CompilerServices;

namespace PdfNetStack.Imaging;

/// <summary>One decoded channel at its own resolution, with its place on the reference grid.</summary>
internal sealed class Jpeg2000Channel
{
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Sample separation on the reference grid (XRsiz, YRsiz).</summary>
    public int Dx { get; init; }

    public int Dy { get; init; }

    public int Depth { get; init; }

    public bool Signed { get; init; }

    /// <summary>True for an opacity channel by the JP2 channel definition box.</summary>
    public bool Opacity { get; init; }

    /// <summary>The codestream component the channel comes from.</summary>
    public int Component { get; init; }

    /// <summary>The JP2 palette column the component's values index, or null for the values themselves.</summary>
    public int[]? Lookup { get; init; }

    /// <summary>Samples, rows top to bottom; signed channels hold negative values. Empty until decoded.</summary>
    public int[] Samples { get; init; } = [];
}

/// <summary>A decoded JPEG 2000 image: its channels after the JP2 palette and channel definitions.</summary>
internal sealed class Jpeg2000Image
{
    /// <summary>Image area origin on the reference grid (XOsiz, YOsiz).</summary>
    public int X0 { get; init; }

    public int Y0 { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public IReadOnlyList<Jpeg2000Channel> Channels { get; init; } = [];

    /// <summary>The JP2 enumerated color space (16 sRGB, 17 greyscale, 18 sYCC, 12 CMYK), or null.</summary>
    public int? ColorSpace { get; init; }

    /// <summary>
    /// The first <paramref name="channels"/> channels as interleaved 8 bit samples over the image
    /// area. Subsampled channels repeat each sample. Scaling maps 0 to 2^depth - 1 onto 0 to 255
    /// (signed channels offset by half their range first); without it values are clamped as they are.
    /// </summary>
    public Raster ToRaster(int channels, bool scale)
    {
        long size = (long)Width * Height * channels;
        if (size > Compat.MaxArrayLength) throw new Jpeg2000FormatException("The image is too large.");
        var output = new byte[size];
        for (int c = 0; c < channels; c++)
        {
            var target = new Jpeg2000RasterChannel(this, Channels[c], c, scale);
            target.Write(output, Width, channels, 0, 0, Channels[c].Width, Channels[c].Height, Channels[c].Samples);
        }

        return new Raster(Width, Height, channels, output);
    }
}

/// <summary>
/// Where one channel's samples land in an interleaved 8 bit raster: the image position of each
/// sample column and row, and the table from sample values to bytes.
/// </summary>
internal sealed class Jpeg2000RasterChannel
{
    private readonly int[] _columns;
    private readonly int[] _rows;
    private readonly byte[] _table;
    private readonly int _offset;
    private readonly int _channel;

    /// <param name="channel">The channel's description; its depth and sign set the table.</param>
    /// <param name="index">The channel's place in each pixel.</param>
    public Jpeg2000RasterChannel(Jpeg2000Image image, Jpeg2000Channel channel, int index, bool scale)
        : this(image, channel, index, scale, channel.Depth, channel.Signed)
    {
    }

    public Jpeg2000RasterChannel(Jpeg2000Image image, Jpeg2000Channel channel, int index, bool scale, int depth, bool signed)
    {
        _channel = index;
        int max = (1 << depth) - 1;
        _offset = signed ? 1 << (depth - 1) : 0;
        _table = new byte[max + 1];
        for (int v = 0; v <= max; v++) _table[v] = scale ? (byte)(((v * 255L) + (max >> 1)) / max) : (byte)Math.Min(v, 255);

        // Pixel x of the image area shows sample column ((X0 + x) / dx) - ceil(X0 / dx), kept inside the channel.
        int cx0 = Ceil(image.X0, channel.Dx), cy0 = Ceil(image.Y0, channel.Dy);
        _columns = new int[image.Width];
        _rows = new int[image.Height];
        for (int x = 0; x < image.Width; x++) _columns[x] = Compat.Clamp(((image.X0 + x) / channel.Dx) - cx0, 0, channel.Width - 1);
        for (int y = 0; y < image.Height; y++) _rows[y] = Compat.Clamp(((image.Y0 + y) / channel.Dy) - cy0, 0, channel.Height - 1);
    }

    /// <summary>The byte for a sample value.</summary>
    public byte Map(int value) => _table[Compat.Clamp(value + _offset, 0, _table.Length - 1)];

    /// <summary>
    /// Writes the pixels that show samples of the block at column <paramref name="x"/>, row
    /// <paramref name="y"/>, <paramref name="width"/> by <paramref name="rows"/>, rows packed in
    /// <paramref name="values"/>.
    /// </summary>
    public void Write(byte[] output, int imageWidth, int channels, int x, int y, int width, int rows, int[] values)
    {
        int xa = LowerBound(_columns, x), xb = LowerBound(_columns, x + width);
        int ya = LowerBound(_rows, y), yb = LowerBound(_rows, y + rows);
        int[] columns = _columns;
        byte[] table = _table;
        int top = table.Length - 1, offset = _offset;
        for (int py = ya; py < yb; py++)
        {
            int from = ((_rows[py] - y) * width) - x;
            long o = ((((long)py * imageWidth) + xa) * channels) + _channel;
            for (int px = xa; px < xb; px++, o += channels) output[o] = table[Compat.Clamp(values[from + columns[px]] + offset, 0, top)];
        }
    }

    /// <summary>The first index whose value is at least <paramref name="value"/>; the values never decrease.</summary>
    private static int LowerBound(int[] values, int value)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (values[mid] < value) low = mid + 1;
            else high = mid;
        }

        return low;
    }

    private static int Ceil(int a, int b) => (int)(((long)a + b - 1) / b);
}

/// <summary>
/// Decodes JPEG 2000 (ITU-T T.800, part 1): JP2 files and raw codestreams. Tier-2 packet
/// decoding in every progression order with precincts, layers, PPM, PPT, SOP and EPH; tier-1
/// EBCOT with all code-block styles; scalar and reversible dequantization with ROI shifts; the
/// 5/3 and 9/7 inverse wavelets; RCT and ICT; DC level shift. Tiles are reconstructed a band of
/// rows at a time, so the working memory beyond the output is a few rows of the widest tile.
/// Every size, count and the total work are bounded by the caller's limits, and malformed data
/// throws <see cref="Jpeg2000FormatException"/>.
/// </summary>
internal static class Jpeg2000Decoder
{
    // Samples per component a band of rows aims at: a tile this size or smaller is done at once.
    private const int BandSamples = 1 << 18;

    /// <summary>Decodes a JP2 file or codestream into planes of samples.</summary>
    /// <param name="byteLimit">Ceiling for the decoder's buffers.</param>
    /// <param name="maxPixels">Ceiling for the image area.</param>
    /// <param name="applyPalette">False keeps palette indexes, for a PDF Indexed color space.</param>
    /// <param name="bandRows">Rows reconstructed together, or 0 to choose by width.</param>
    /// <exception cref="Jpeg2000FormatException">The data is damaged, too large or unsupported.</exception>
    public static Jpeg2000Image Decode(ReadOnlySpan<byte> data, long byteLimit, long maxPixels = ImageSamples.MaxPixels, bool applyPalette = true, int bandRows = 0)
    {
        byte[] bytes = data.ToArray();
        return Guarded(() =>
        {
            (Jpeg2000Codestream stream, Jp2Header? header) = Open(bytes, maxPixels);
            return Assemble(stream, Planes(stream, byteLimit, bandRows), header, applyPalette);
        });
    }

    /// <summary>
    /// Decodes for a PDF image (ISO 32000-1, 7.4.9): the dictionary's color space wins and keeps
    /// palette indexes for Indexed; without one the JP2 header decides, then the channel count.
    /// Returns 8 bit samples in the space's component count, or a null raster when nothing fits.
    /// The samples go straight from each band of rows into the raster.
    /// </summary>
    /// <param name="bandRows">Rows reconstructed together, or 0 to choose by width.</param>
    /// <exception cref="Jpeg2000FormatException">The data is damaged, too large or unsupported.</exception>
    public static (Raster? Raster, ColorSpace? Space) DecodeForPdf(byte[] data, ColorSpace? space, long byteLimit, int bandRows = 0) =>
        Guarded(() => Pdf(data, space, byteLimit, bandRows));

    private static (Raster? Raster, ColorSpace? Space) Pdf(byte[] data, ColorSpace? space, long byteLimit, int bandRows)
    {
        bool keepIndexes = space?.IsIndexed == true, indexed = keepIndexes;
        (Jpeg2000Codestream stream, Jp2Header? header) = Open(data, ImageSamples.MaxPixels);
        Jpeg2000Image image = Assemble(stream, null, header, applyPalette: !indexed);
        int colors = image.Channels.Count(c => !c.Opacity);
        List<Jpeg2000Channel> ordered = [.. image.Channels.Where(c => !c.Opacity), .. image.Channels.Where(c => c.Opacity)];
        bool sycc = false;

        if (space is null || space.Components > ordered.Count)
        {
            space = image.ColorSpace switch
            {
                17 => ColorSpace.Gray,
                16 or 18 => ColorSpace.Rgb,
                12 => ColorSpace.Cmyk,
                _ => null,
            };
            if (space is null || space.Components > colors) space = colors switch { 1 or 2 => ColorSpace.Gray, 3 => ColorSpace.Rgb, >= 4 => ColorSpace.Cmyk, _ => null };
            if (space is null) return (null, null);
            sycc = image.ColorSpace == 18 && space == ColorSpace.Rgb && SyccApplies(ordered);
            indexed = false;
        }

        if (indexed && ordered[0].Depth > 8) return (null, space);
        var colorFirst = new Jpeg2000Image { X0 = image.X0, Y0 = image.Y0, Width = image.Width, Height = image.Height, Channels = ordered, ColorSpace = image.ColorSpace };

        // sYCC pairs samples by index, which is a position only when the three components share a grid.
        if (sycc && !ordered.Take(3).All(c => stream.Components[c.Component].Dx == stream.Components[ordered[0].Component].Dx
            && stream.Components[c.Component].Dy == stream.Components[ordered[0].Component].Dy))
        {
            int[][] planes = Planes(stream, byteLimit, bandRows);
            Jpeg2000Image decoded = Assemble(stream, planes, header, applyPalette: !keepIndexes);
            List<Jpeg2000Channel> all = [.. decoded.Channels.Where(c => !c.Opacity), .. decoded.Channels.Where(c => c.Opacity)];
            var whole = new Jpeg2000Image { X0 = image.X0, Y0 = image.Y0, Width = image.Width, Height = image.Height, Channels = all, ColorSpace = image.ColorSpace };
            return (Sycc(whole).ToRaster(space.Components, scale: true), space);
        }

        int count = space.Components;
        long outputBytes = (long)image.Width * image.Height * count;
        if (outputBytes > Compat.MaxArrayLength) throw new Jpeg2000FormatException("The image is too large.");
        if (ordered.Take(count).Any(c => c.Width <= 0 || c.Height <= 0)) throw new Jpeg2000FormatException("A channel has no samples.");
        var sink = new RasterSink(colorFirst, count, scale: !indexed, sycc);
        Decode(stream, sink, byteLimit, outputBytes, bandRows, sink.Allocate);
        return (sink.Raster, space);
    }

    /// <summary>Runs a decode, turning a bound missed above into a format error rather than a crash.</summary>
    private static TResult Guarded<TResult>(Func<TResult> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new Jpeg2000FormatException("The JPEG 2000 data is malformed.", e);
        }
    }

    private static (Jpeg2000Codestream Stream, Jp2Header? Header) Open(byte[] bytes, long maxPixels)
    {
        Jp2Header? header = null;
        int start = 0, end = bytes.Length;
        if (bytes.Length >= 12 && bytes[3] == 12 && bytes[4] == 0x6A && bytes[5] == 0x50 && bytes[6] == 0x20 && bytes[7] == 0x20)
        {
            header = ReadBoxes(bytes, out start, out end);
        }

        return (Jpeg2000Codestream.Read(bytes, start, end, maxPixels), header);
    }

    /// <summary>True when the first three channels are of one size, as the sYCC conversion needs.</summary>
    private static bool SyccApplies(IReadOnlyList<Jpeg2000Channel> ch) =>
        ch.Count >= 3 && ch[0].Width == ch[1].Width && ch[0].Width == ch[2].Width && ch[0].Height == ch[1].Height && ch[0].Height == ch[2].Height;

    /// <summary>The image's components as planes at component resolution.</summary>
    private static int[][] Planes(Jpeg2000Codestream stream, long byteLimit, int bandRows)
    {
        Jpeg2000Component[] components = stream.Components;
        int n = components.Length;
        long planeBytes = 0;
        for (int c = 0; c < n; c++)
        {
            long w = ComponentWidth(stream, c), h = ComponentHeight(stream, c);
            if (w * h > Compat.MaxArrayLength) throw new Jpeg2000FormatException("The image is too large.");
            planeBytes += w * h * 4;
        }

        var sink = new PlaneSink(stream);
        Decode(stream, sink, byteLimit, planeBytes, bandRows, sink.Allocate);
        return sink.Planes;
    }

    private static int ComponentWidth(Jpeg2000Codestream stream, int c) => Ceil(stream.X1, stream.Components[c].Dx) - Ceil(stream.X0, stream.Components[c].Dx);

    private static int ComponentHeight(Jpeg2000Codestream stream, int c) => Ceil(stream.Y1, stream.Components[c].Dy) - Ceil(stream.Y0, stream.Components[c].Dy);

    /// <summary>Decodes every tile into the sink, within <paramref name="byteLimit"/> bytes with the sink's own.</summary>
    /// <param name="sinkBytes">What the sink holds.</param>
    /// <param name="allocate">Allocates the sink's buffers once the first check has passed.</param>
    private static void Decode(Jpeg2000Codestream stream, ISink sink, long byteLimit, long sinkBytes, int bandRows, Action allocate)
    {
        long workingLimit = byteLimit - sinkBytes;

        // The nominal tile under the main header's coding must fit before anything is allocated;
        // each tile is checked again under its own coding.
        Jpeg2000Coding main = stream.Main.Coding!;
        long nominal = 0;
        for (int c = 0; c < stream.Components.Length; c++)
        {
            long w = Math.Min(ComponentWidth(stream, c), ((long)stream.TileWidth / stream.Components[c].Dx) + 2);
            long h = Math.Min(ComponentHeight(stream, c), ((long)stream.TileHeight / stream.Components[c].Dy) + 2);
            nominal += Jpeg2000Synthesis.WorkingBytes(w, h, BandHeight(w, h, bandRows), main.BlockHeight) + (8 * w * Math.Min(h, BandHeight(w, h, bandRows)));
        }

        if (nominal > workingLimit) throw new Jpeg2000FormatException("The image exceeds the decode limit.");
        allocate();
        if (Array.Exists(stream.Tiles, t => t is null)) sink.Missing();

        var block = new Jpeg2000BlockDecoder();
        var buffers = new Jpeg2000Buffers();
        long blockLimit = Math.Max(4096, byteLimit / 128);
        for (int t = 0; t < stream.Tiles.Length; t++)
        {
            if (stream.Tiles[t] is Jpeg2000TileParts parts) DecodeTile(stream, t, parts, sink, block, buffers, blockLimit, workingLimit, bandRows);
        }
    }

    /// <summary>Rows reconstructed together for a tile-component <paramref name="width"/> by <paramref name="height"/>.</summary>
    private static int BandHeight(long width, long height, int bandRows)
    {
        long rows = bandRows > 0 ? bandRows : Math.Max(16, BandSamples / Math.Max(1, width));
        return (int)Math.Max(1, Math.Min(Math.Max(1, height), rows));
    }

    private static void DecodeTile(
        Jpeg2000Codestream stream, int t, Jpeg2000TileParts parts, ISink sink, Jpeg2000BlockDecoder block, Jpeg2000Buffers buffers, long blockLimit, long workingLimit, int bandRows)
    {
        Jpeg2000Parameters main = stream.Main, tile = parts.Parameters;
        Jpeg2000Parameters general = tile.HasCod ? tile : main;
        (int tx0, int ty0, int tx1, int ty1) = TileArea(stream, t);

        int n = stream.Components.Length;
        var tcs = new Jpeg2000TileComponent[n];
        int maxResolutions = 0;
        long precincts = 0;
        for (int c = 0; c < n; c++)
        {
            // Precedence (A.6): tile COC, tile COD, main COC, main COD; likewise for QCC and QCD.
            Jpeg2000Coding coding = tile.ComponentCoding[c] ?? tile.Coding ?? main.ComponentCoding[c] ?? main.Coding!;
            Jpeg2000Quantization quantization = tile.ComponentQuantization[c] ?? tile.Quantization ?? main.ComponentQuantization[c] ?? main.Quantization!;
            int roi = tile.RoiShift[c] ?? main.RoiShift[c] ?? 0;
            tcs[c] = BuildComponent(stream.Components[c], coding, quantization, roi, (tx0, ty0, tx1, ty1));
            foreach (Jpeg2000Resolution res in tcs[c].Resolutions) precincts += res.NextLayer.Length;
            if (precincts > blockLimit) throw new Jpeg2000FormatException("A tile has too many precincts.");
            maxResolutions = Math.Max(maxResolutions, coding.Levels + 1);
        }

        // The rows of every component share one height per band, so components of one size line up for the color transform.
        int widest = tcs.Max(tc => Math.Max(0, tc.Width)), tallest = tcs.Max(tc => Math.Max(0, tc.Height));
        int rows = BandHeight(widest, tallest, bandRows);
        long working = 0;
        foreach (Jpeg2000TileComponent tc in tcs)
        {
            if (tc.Width > 0 && tc.Height > 0) working += Jpeg2000Synthesis.WorkingBytes(tc.Width, tc.Height, rows, tc.Coding.BlockHeight) + (8L * tc.Width * Math.Min(tc.Height, rows));
        }

        if (working > workingLimit) throw new Jpeg2000FormatException("The image exceeds the decode limit.");

        // The tile's data, joined when it comes in several tile-parts, and its packet headers when
        // PPM or PPT carry them (A.7.4, A.7.5).
        byte[] body = stream.Data;
        int bodyStart = 0, bodyEnd = 0;
        if (parts.Bodies.Count == 1)
        {
            (bodyStart, bodyEnd) = (parts.Bodies[0].Offset, parts.Bodies[0].Offset + parts.Bodies[0].Length);
        }
        else
        {
            foreach ((int _, int length) in parts.Bodies) bodyEnd += length;
            body = new byte[bodyEnd];
            int at = 0;
            foreach ((int offset, int length) in parts.Bodies)
            {
                Buffer.BlockCopy(stream.Data, offset, body, at, length);
                at += length;
            }
        }

        byte[]? headers = null;
        if (parts.Ppm is not null) headers = Concatenate(parts.Ppm);
        else if (parts.Ppt is not null) headers = Concatenate(parts.Ppt.Values);

        List<Jpeg2000Progression> progressions = tile.Progressions.Count > 0 ? tile.Progressions : main.Progressions;
        if (progressions.Count == 0) progressions = [new Jpeg2000Progression(0, 0, general.Layers, maxResolutions, n, general.Order)];
        var packets = new Jpeg2000Packets(tcs, body, bodyStart, bodyEnd, headers, general.Layers, general.Sop, general.Eph, blockLimit)
        {
            ComponentDx = [.. stream.Components.Select(c => c.Dx)],
            ComponentDy = [.. stream.Components.Select(c => c.Dy)],
            TileX0 = tx0,
            TileY0 = ty0,
        };
        packets.Read(progressions);

        // Tier-1, dequantization and the inverse wavelet per component, band by band; then MCT and level shift.
        var synthesis = new Jpeg2000Synthesis?[n];
        for (int c = 0; c < n; c++)
        {
            if (tcs[c].Width > 0 && tcs[c].Height > 0) synthesis[c] = Jpeg2000Synthesis.Create(tcs[c], c, block, buffers);
        }

        bool mct = general.Mct && n >= 3 && synthesis[0] is not null;
        if (mct)
        {
            if (tcs[1].Width != tcs[0].Width || tcs[2].Width != tcs[0].Width || tcs[1].Height != tcs[0].Height || tcs[2].Height != tcs[0].Height)
            {
                throw new Jpeg2000FormatException("The color transform needs components of one size.");
            }

            if (tcs[1].Coding.Reversible != tcs[0].Coding.Reversible || tcs[2].Coding.Reversible != tcs[0].Coding.Reversible)
            {
                throw new Jpeg2000FormatException("The color transform mixes reversible and irreversible components.");
            }
        }

        var bands = new RowBand[n];
        for (int c = 0; c < n; c++) bands[c].Values = synthesis[c] is null ? null : buffers.Get<int>(Jpeg2000Buffers.Key(c, 0, Jpeg2000Buffers.Shifted), (long)tcs[c].Width * Math.Min(tcs[c].Height, rows));
        for (int y = 0; y < tallest; y += rows)
        {
            for (int c = 0; c < n; c++)
            {
                Jpeg2000TileComponent tc = tcs[c];
                bands[c].Rows = 0;
                if (synthesis[c] is null || y >= tc.Height) continue;
                bands[c].Rows = Math.Min(rows, tc.Height - y);
                synthesis[c]!.Rows(y, y + bands[c].Rows);
            }

            bool transform = mct && bands[0].Rows > 0;
            for (int c = 0; c < n; c++)
            {
                if (bands[c].Rows == 0) continue;
                Jpeg2000TileComponent tc = tcs[c];
                Jpeg2000Component comp = stream.Components[c];
                bands[c].X = tc.X0 - Ceil(stream.X0, comp.Dx);
                bands[c].Y = tc.Y0 - Ceil(stream.Y0, comp.Dy) + y;
                bands[c].Width = tc.Width;
                if (!transform || c >= 3) LevelShift(synthesis[c]!, comp, tc.Width * bands[c].Rows, bands[c].Values!);
            }

            if (transform) InverseColorTransform(synthesis, stream.Components, bands, tcs[0].Width * bands[0].Rows);

            sink.Write(bands);
        }
    }

    /// <summary>The tile's area on the reference grid (B.3).</summary>
    private static (int X0, int Y0, int X1, int Y1) TileArea(Jpeg2000Codestream stream, int t)
    {
        int p = t % stream.TilesAcross, q = t / stream.TilesAcross;
        int tx0 = (int)Math.Max(stream.X0, stream.TileX0 + ((long)p * stream.TileWidth));
        int ty0 = (int)Math.Max(stream.Y0, stream.TileY0 + ((long)q * stream.TileHeight));
        int tx1 = (int)Math.Min(stream.X1, stream.TileX0 + ((long)(p + 1) * stream.TileWidth));
        int ty1 = (int)Math.Min(stream.Y1, stream.TileY0 + ((long)(q + 1) * stream.TileHeight));
        return (tx0, ty0, tx1, ty1);
    }

    /// <summary>The DC level shift (G.1.2) and the clamp to the component's range, rounding irreversible values.</summary>
    private static void LevelShift(Jpeg2000Synthesis synthesis, Jpeg2000Component comp, int count, int[] plane)
    {
        (int shift, int min, int max) = Range(comp);
        if (synthesis.Output is int[] ints)
        {
            for (int i = 0; i < count; i++) plane[i] = Compat.Clamp(ints[i] + shift, min, max);
            return;
        }

        float[] floats = (float[])synthesis.Output;
        for (int i = 0; i < count; i++) plane[i] = Shift(floats[i], shift, min, max);
    }

    /// <summary>The level shift and the component's range of samples.</summary>
    private static (int Shift, int Min, int Max) Range(Jpeg2000Component comp) =>
        comp.Signed ? (0, -(1 << (comp.Depth - 1)), (1 << (comp.Depth - 1)) - 1) : (1 << (comp.Depth - 1), 0, (1 << comp.Depth) - 1);

    /// <summary>An irreversible value rounded half to even, as lrintf in OpenJPEG, shifted and clamped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Shift(float value, int shift, int min, int max)
    {
        double v = Math.Round((double)value);
        return v >= max - shift ? max : v <= min - shift ? min : (int)v + shift;
    }

    private static byte[] Concatenate(IEnumerable<byte[]> chunks)
    {
        var all = new List<byte>();
        foreach (byte[] chunk in chunks) all.AddRange(chunk);
        return [.. all];
    }

    /// <summary>Tile-component, resolution, subband and precinct geometry (T.800 B.3 to B.6) and step sizes (E.1).</summary>
    private static Jpeg2000TileComponent BuildComponent(Jpeg2000Component comp, Jpeg2000Coding coding, Jpeg2000Quantization quant, int roi, (int X0, int Y0, int X1, int Y1) tile)
    {
        int x0 = Ceil(tile.X0, comp.Dx), y0 = Ceil(tile.Y0, comp.Dy), x1 = Ceil(tile.X1, comp.Dx), y1 = Ceil(tile.Y1, comp.Dy);
        int levels = coding.Levels;
        var resolutions = new Jpeg2000Resolution[levels + 1];
        for (int r = 0; r <= levels; r++)
        {
            int n = levels - r;
            int rx0 = CeilShift(x0, n), ry0 = CeilShift(y0, n), rx1 = CeilShift(x1, n), ry1 = CeilShift(y1, n);
            int ppx = coding.PrecinctWidth(r), ppy = coding.PrecinctHeight(r);
            int across = rx1 > rx0 ? CeilShift(rx1, ppx) - (rx0 >> ppx) : 0;
            int down = ry1 > ry0 ? CeilShift(ry1, ppy) - (ry0 >> ppy) : 0;
            long count = (long)across * down;
            if (count > 1 << 24) throw new Jpeg2000FormatException("A resolution has too many precincts.");

            Jpeg2000Band[] bands;
            if (r == 0)
            {
                bands = [Band(comp, coding, quant, 0, 0, rx0, ry0, rx1, ry1)];
            }
            else
            {
                bands =
                [
                    Band(comp, coding, quant, r, 1, rx0 >> 1, CeilShift(ry0, 1), rx1 >> 1, CeilShift(ry1, 1)),
                    Band(comp, coding, quant, r, 2, CeilShift(rx0, 1), ry0 >> 1, CeilShift(rx1, 1), ry1 >> 1),
                    Band(comp, coding, quant, r, 3, rx0 >> 1, ry0 >> 1, rx1 >> 1, ry1 >> 1),
                ];
            }

            resolutions[r] = new Jpeg2000Resolution
            {
                X0 = rx0,
                Y0 = ry0,
                X1 = rx1,
                Y1 = ry1,
                PrecinctWidth = ppx,
                PrecinctHeight = ppy,
                PrecinctsAcross = across,
                PrecinctsDown = down,
                BlockWidth = Math.Min(coding.BlockWidth, r == 0 ? ppx : ppx - 1),
                BlockHeight = Math.Min(coding.BlockHeight, r == 0 ? ppy : ppy - 1),
                Bands = bands,
                Precincts = new Jpeg2000PrecinctBand[]?[count],
                NextLayer = new int[count],
            };
        }

        return new Jpeg2000TileComponent { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Coding = coding, RoiShift = roi, Resolutions = resolutions };
    }

    private static Jpeg2000Band Band(Jpeg2000Component comp, Jpeg2000Coding coding, Jpeg2000Quantization quant, int r, int orientation, int x0, int y0, int x1, int y1)
    {
        int levels = coding.Levels;
        int index = r == 0 ? 0 : 1 + (3 * (r - 1)) + (orientation - 1);
        int decompositions = r == 0 ? levels : levels - r + 1;
        (int exponent, int mantissa) = quant.Band(index, levels, decompositions);

        // E-2 and E-3: Mb = G + exponent - 1; the step size from the band's nominal range.
        int gain = orientation == 0 ? 0 : orientation == 3 ? 2 : 1;
        float step = coding.Reversible ? 1 : (float)((1 + (mantissa / 2048.0)) * Math.Pow(2, comp.Depth + gain - exponent));
        return new Jpeg2000Band
        {
            Orientation = orientation,
            X0 = x0,
            Y0 = y0,
            X1 = Math.Max(x0, x1),
            Y1 = Math.Max(y0, y1),
            Planes = quant.Guard + exponent - 1,
            Step = step,
        };
    }

    /// <summary>The inverse RCT (G-2) or ICT (G-6) on the first three components' rows, then their level shift.</summary>
    private static void InverseColorTransform(Jpeg2000Synthesis?[] synthesis, Jpeg2000Component[] components, RowBand[] bands, int count)
    {
        (int s0, int min0, int max0) = Range(components[0]);
        (int s1, int min1, int max1) = Range(components[1]);
        (int s2, int min2, int max2) = Range(components[2]);
        int[] r = bands[0].Values!, g = bands[1].Values!, b = bands[2].Values!;
        if (synthesis[0]!.Output is int[] y0 && synthesis[1]!.Output is int[] y1 && synthesis[2]!.Output is int[] y2)
        {
            for (int i = 0; i < count; i++)
            {
                int green = y0[i] - ((y2[i] + y1[i]) >> 2);
                r[i] = Compat.Clamp(y2[i] + green + s0, min0, max0);
                g[i] = Compat.Clamp(green + s1, min1, max1);
                b[i] = Compat.Clamp(y1[i] + green + s2, min2, max2);
            }
        }
        else
        {
            float[] f0 = (float[])synthesis[0]!.Output, f1 = (float[])synthesis[1]!.Output, f2 = (float[])synthesis[2]!.Output;
            for (int i = 0; i < count; i++)
            {
                // Each value is rounded to a float, as stored between the steps before.
                float y = f0[i], cb = f1[i], cr = f2[i];
                r[i] = Shift((float)(y + (1.402f * cr)), s0, min0, max0);
                g[i] = Shift((float)(y - (0.34413f * cb) - (0.71414f * cr)), s1, min1, max1);
                b[i] = Shift((float)(y + (1.772f * cb)), s2, min2, max2);
            }
        }
    }

    private static int Ceil(int a, int b) => (int)(((long)a + b - 1) / b);

    private static int CeilShift(int a, int n) => n >= 31 ? (a > 0 ? 1 : 0) : (int)(((long)a + (1L << n) - 1) >> n);

    /// <summary>sYCC to sRGB (IEC 61966-2-1 Amendment 1), chroma centered at half the range.</summary>
    private static Jpeg2000Image Sycc(Jpeg2000Image image)
    {
        IReadOnlyList<Jpeg2000Channel> ch = image.Channels;
        if (!SyccApplies(ch)) return image;
        int depth = ch[0].Depth, max = (1 << depth) - 1;
        int count = ch[0].Samples.Length;
        var r = new int[count];
        var g = new int[count];
        var b = new int[count];
        for (int i = 0; i < count; i++) (r[i], g[i], b[i]) = SyccToRgb(ch[0].Samples[i], ch[1].Samples[i], ch[2].Samples[i], depth, max);

        Jpeg2000Channel Plane(Jpeg2000Channel like, int[] samples) =>
            new() { Width = like.Width, Height = like.Height, Dx = like.Dx, Dy = like.Dy, Depth = depth, Samples = samples };
        List<Jpeg2000Channel> channels = [Plane(ch[0], r), Plane(ch[0], g), Plane(ch[0], b), .. ch.Skip(3)];
        return new Jpeg2000Image { X0 = image.X0, Y0 = image.Y0, Width = image.Width, Height = image.Height, Channels = channels, ColorSpace = 16 };
    }

    private static (int R, int G, int B) SyccToRgb(int luma, int blue, int red, int depth, int max)
    {
        int half = 1 << (depth - 1);
        double y = luma, cb = blue - half, cr = red - half;
        return (Compat.Clamp((int)Math.Round(y + (1.402 * cr)), 0, max),
            Compat.Clamp((int)Math.Round(y - (0.344136 * cb) - (0.714136 * cr)), 0, max),
            Compat.Clamp((int)Math.Round(y + (1.772 * cb)), 0, max));
    }

    /// <summary>The rows of one component a band of a tile yields, at their place in the component's plane.</summary>
    private struct RowBand
    {
        public int X;
        public int Y;
        public int Width;
        public int Rows;
        public int[]? Values;
    }

    /// <summary>Where decoded bands of rows go.</summary>
    private interface ISink
    {
        /// <summary>One band of rows of a tile, per component; components without rows in it have none.</summary>
        void Write(RowBand[] bands);

        /// <summary>Before any tile: some tiles have no data, and their samples are 0.</summary>
        void Missing();
    }

    /// <summary>Collects every component as a plane of samples.</summary>
    private sealed class PlaneSink(Jpeg2000Codestream stream) : ISink
    {
        public int[][] Planes { get; private set; } = [];

        public void Allocate()
        {
            Planes = new int[stream.Components.Length][];
            for (int c = 0; c < Planes.Length; c++) Planes[c] = new int[(long)ComponentWidth(stream, c) * ComponentHeight(stream, c)];
        }

        public void Write(RowBand[] bands)
        {
            for (int c = 0; c < bands.Length; c++)
            {
                RowBand band = bands[c];
                int planeWidth = ComponentWidth(stream, c);
                for (int y = 0; y < band.Rows; y++) Array.Copy(band.Values!, y * band.Width, Planes[c], ((band.Y + y) * planeWidth) + band.X, band.Width);
            }
        }

        public void Missing()
        {
            // The planes start at 0.
        }
    }

    /// <summary>
    /// Writes channels straight into an interleaved 8 bit raster, as <see cref="Jpeg2000Image.ToRaster"/>
    /// would from the planes: through the JP2 palette, sYCC to RGB when asked, then the byte table.
    /// </summary>
    private sealed class RasterSink : ISink
    {
        private readonly Jpeg2000Image _image;
        private readonly int _count;
        private readonly bool _sycc;
        private readonly Jpeg2000RasterChannel[] _targets;
        private readonly int[][] _values;
        private readonly int[][] _sources;

        public RasterSink(Jpeg2000Image image, int count, bool scale, bool sycc)
        {
            _image = image;
            _count = count;
            _sycc = sycc;
            _targets = new Jpeg2000RasterChannel[count];
            _values = new int[count][];
            _sources = new int[count][];
            for (int c = 0; c < count; c++)
            {
                // sYCC output has the depth of the first channel and no sign.
                Jpeg2000Channel channel = image.Channels[c];
                _targets[c] = sycc && c < 3
                    ? new Jpeg2000RasterChannel(image, channel, c, scale, image.Channels[0].Depth, signed: false)
                    : new Jpeg2000RasterChannel(image, channel, c, scale);
                _values[c] = [];
            }
        }

        public Raster? Raster { get; private set; }

        public void Allocate() => Raster = new Raster(_image.Width, _image.Height, _count, new byte[(long)_image.Width * _image.Height * _count]);

        public void Write(RowBand[] bands)
        {
            byte[] output = Raster!.Samples;
            for (int c = 0; c < _count; c++)
            {
                Jpeg2000Channel channel = _image.Channels[c];
                RowBand band = bands[channel.Component];
                if (band.Rows == 0) continue;
                int size = band.Width * band.Rows;
                if (channel.Lookup is null && !(_sycc && c < 3))
                {
                    _sources[c] = band.Values!;
                    continue;
                }

                // A channel of its own: through the palette, or a copy the sYCC conversion may overwrite.
                if (_values[c].Length < size) _values[c] = new int[size];
                int[] values = _values[c];
                if (channel.Lookup is int[] lookup)
                {
                    for (int i = 0; i < size; i++) values[i] = lookup[Compat.Clamp(band.Values![i], 0, lookup.Length - 1)];
                }
                else
                {
                    Array.Copy(band.Values!, values, size);
                }

                _sources[c] = values;
            }

            if (_sycc)
            {
                // The three channels share one grid here, so their bands line up sample for sample.
                RowBand band = bands[_image.Channels[0].Component];
                int depth = _image.Channels[0].Depth, max = (1 << depth) - 1;
                int[] r = _values[0], g = _values[1], b = _values[2];
                if (band.Rows > 0)
                {
                    for (int i = 0, size = band.Width * band.Rows; i < size; i++) (r[i], g[i], b[i]) = SyccToRgb(r[i], g[i], b[i], depth, max);
                }
            }

            for (int c = 0; c < _count; c++)
            {
                RowBand band = bands[_image.Channels[c].Component];
                if (band.Rows > 0) _targets[c].Write(output, _image.Width, _count, band.X, band.Y, band.Width, band.Rows, _sources[c]);
            }
        }

        public void Missing()
        {
            // Every sample of every channel reads 0 there; the tiles that have data overwrite their pixels.
            var zero = new byte[_count];
            int[] values = new int[_count];
            for (int c = 0; c < _count; c++) values[c] = _image.Channels[c].Lookup is int[] lookup ? lookup[0] : 0;
            if (_sycc)
            {
                int depth = _image.Channels[0].Depth;
                (values[0], values[1], values[2]) = SyccToRgb(values[0], values[1], values[2], depth, (1 << depth) - 1);
            }

            for (int c = 0; c < _count; c++) zero[c] = _targets[c].Map(values[c]);
            byte[] output = Raster!.Samples;
            for (int i = 0; i < output.Length; i += _count) zero.CopyTo(output, i);
        }
    }

    /// <summary>The JP2 header's color specification, palette, component mapping and channel definitions.</summary>
    private sealed class Jp2Header
    {
        public int? EnumeratedColorSpace { get; set; }

        public int[][]? Palette { get; set; }

        public int[] PaletteDepths { get; set; } = [];

        public bool[] PaletteSigned { get; set; } = [];

        public (int Component, int Type, int Column)[]? Mapping { get; set; }

        public (int Channel, int Type, int Association)[]? Definitions { get; set; }
    }

    /// <summary>The JP2 boxes (T.800 Annex I): the header superbox and the contiguous codestream.</summary>
    private static Jp2Header ReadBoxes(byte[] data, out int start, out int end)
    {
        var header = new Jp2Header();
        start = -1;
        end = -1;
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            (int type, int body, int next) = Box(data, pos, data.Length);
            if (type == 0x6A703268)
            {
                // jp2h
                for (int p = body; p + 8 <= next;)
                {
                    (int t, int b, int n) = Box(data, p, next);
                    ReadHeaderBox(data, t, b, n, header);
                    p = n;
                }
            }
            else if (type == 0x6A703263)
            {
                // jp2c
                start = body;
                end = next;
                break;
            }

            pos = next;
        }

        if (start < 0) throw new Jpeg2000FormatException("The JP2 file has no codestream.");
        return header;
    }

    private static (int Type, int Body, int Next) Box(byte[] data, int pos, int limit)
    {
        long length = U32(data, pos);
        int type = (int)U32(data, pos + 4);
        int body = pos + 8;
        if (length == 1)
        {
            if (pos + 16 > limit) throw new Jpeg2000FormatException("A JP2 box is truncated.");
            length = (U32(data, pos + 8) << 32) | U32(data, pos + 12);
            body = pos + 16;
        }
        else if (length == 0)
        {
            length = limit - pos;
        }

        if (length < body - pos) throw new Jpeg2000FormatException("A JP2 box length is invalid.");

        // A box running past the end is cut there: truncated files still decode what they hold.
        long next = Math.Min(limit, pos + length);
        return (type, body, (int)next);
    }

    private static void ReadHeaderBox(byte[] data, int type, int p, int end, Jp2Header header)
    {
        int length = end - p;
        switch (type)
        {
            case 0x636F6C72 when length >= 3 && header.EnumeratedColorSpace is null:
                // colr: the first one counts; method 1 is an enumerated space.
                if (data[p] == 1 && length >= 7) header.EnumeratedColorSpace = (int)U32(data, p + 3);
                else header.EnumeratedColorSpace = -1;
                break;
            case 0x70636C72 when length >= 3:
            {
                // pclr: entries, columns, each column's depth, then the entries.
                int entries = (data[p] << 8) | data[p + 1], columns = data[p + 2];
                if (entries is 0 or > 1024 || columns == 0 || length < 3 + columns) throw new Jpeg2000FormatException("The JP2 palette is invalid.");
                var depths = new int[columns];
                var signed = new bool[columns];
                int bytes = 0;
                for (int i = 0; i < columns; i++)
                {
                    depths[i] = (data[p + 3 + i] & 0x7F) + 1;
                    signed[i] = (data[p + 3 + i] & 0x80) != 0;
                    if (depths[i] > 16) throw new Jpeg2000FormatException("The JP2 palette is too deep.");
                    bytes += (depths[i] + 7) / 8;
                }

                if (length < 3 + columns + ((long)entries * bytes)) throw new Jpeg2000FormatException("The JP2 palette is truncated.");
                var palette = new int[columns][];
                int at = p + 3 + columns;
                for (int c = 0; c < columns; c++) palette[c] = new int[entries];
                for (int e = 0; e < entries; e++)
                {
                    for (int c = 0; c < columns; c++)
                    {
                        int v = 0;
                        for (int k = 0; k < (depths[c] + 7) / 8; k++) v = (v << 8) | data[at++];
                        if (signed[c] && (v & (1 << (depths[c] - 1))) != 0) v -= 1 << depths[c];
                        palette[c][e] = v;
                    }
                }

                header.Palette = palette;
                header.PaletteDepths = depths;
                header.PaletteSigned = signed;
                break;
            }

            case 0x636D6170:
            {
                // cmap: per channel, the component, direct (0) or through the palette (1), and the column.
                var mapping = new (int, int, int)[length / 4];
                for (int i = 0; i < mapping.Length; i++) mapping[i] = ((data[p + (4 * i)] << 8) | data[p + (4 * i) + 1], data[p + (4 * i) + 2], data[p + (4 * i) + 3]);
                header.Mapping = mapping;
                break;
            }

            case 0x63646566 when length >= 2:
            {
                // cdef: per channel, its type (0 color, 1 and 2 opacity) and the color it belongs to.
                int count = Math.Min((data[p] << 8) | data[p + 1], (length - 2) / 6);
                var definitions = new (int, int, int)[count];
                for (int i = 0; i < count; i++)
                {
                    int q = p + 2 + (6 * i);
                    definitions[i] = ((data[q] << 8) | data[q + 1], (data[q + 2] << 8) | data[q + 3], (data[q + 4] << 8) | data[q + 5]);
                }

                header.Definitions = definitions;
                break;
            }
        }
    }

    /// <summary>
    /// The channels: components through the palette (I.5.3.4, I.5.3.5), ordered by cdef (I.5.3.6).
    /// Without planes the channels are described but hold no samples.
    /// </summary>
    private static Jpeg2000Image Assemble(Jpeg2000Codestream stream, int[][]? planes, Jp2Header? header, bool applyPalette)
    {
        var channels = new List<Jpeg2000Channel>();
        Jpeg2000Channel FromComponent(int c) => new()
        {
            Width = Ceil(stream.X1, stream.Components[c].Dx) - Ceil(stream.X0, stream.Components[c].Dx),
            Height = Ceil(stream.Y1, stream.Components[c].Dy) - Ceil(stream.Y0, stream.Components[c].Dy),
            Dx = stream.Components[c].Dx,
            Dy = stream.Components[c].Dy,
            Depth = stream.Components[c].Depth,
            Signed = stream.Components[c].Signed,
            Component = c,
            Samples = planes?[c] ?? [],
        };

        if (applyPalette && header?.Palette is int[][] palette && header.Mapping is { Length: > 0 } mapping)
        {
            foreach ((int component, int type, int column) in mapping)
            {
                if (component >= stream.Components.Length) throw new Jpeg2000FormatException("The JP2 component mapping names a component that does not exist.");
                Jpeg2000Channel source = FromComponent(component);
                if (type != 1)
                {
                    channels.Add(source);
                    continue;
                }

                if (column >= palette.Length) throw new Jpeg2000FormatException("The JP2 component mapping names a palette column that does not exist.");
                int[] entries = palette[column];
                int[] samples = planes is null ? [] : new int[source.Samples.Length];
                for (int i = 0; i < samples.Length; i++) samples[i] = entries[Compat.Clamp(source.Samples[i], 0, entries.Length - 1)];
                channels.Add(new Jpeg2000Channel
                {
                    Width = source.Width,
                    Height = source.Height,
                    Dx = source.Dx,
                    Dy = source.Dy,
                    Depth = header.PaletteDepths[column],
                    Signed = header.PaletteSigned[column],
                    Component = component,
                    Lookup = entries,
                    Samples = samples,
                });
            }
        }
        else
        {
            for (int c = 0; c < stream.Components.Length; c++) channels.Add(FromComponent(c));
        }

        if (header?.Definitions is { Length: > 0 } definitions && definitions.All(d => d.Channel < channels.Count))
        {
            // Color channels in their association order, then the others marked as opacity.
            var ordered = definitions
                .Select(d => (Definition: d, Channel: channels[d.Channel]))
                .OrderBy(d => d.Definition.Type == 0 && d.Definition.Association is > 0 and < 65535 ? 0 : 1)
                .ThenBy(d => d.Definition.Type == 0 ? d.Definition.Association : int.MaxValue)
                .Select(d => d.Definition.Type == 0 ? d.Channel : Opaque(d.Channel))
                .ToList();
            channels = [.. ordered, .. channels.Where((_, i) => !definitions.Any(d => d.Channel == i))];
        }

        return new Jpeg2000Image
        {
            X0 = stream.X0,
            Y0 = stream.Y0,
            Width = stream.X1 - stream.X0,
            Height = stream.Y1 - stream.Y0,
            Channels = channels,
            ColorSpace = header?.EnumeratedColorSpace is int cs && cs >= 0 ? cs : null,
        };
    }

    private static Jpeg2000Channel Opaque(Jpeg2000Channel c) =>
        new() { Width = c.Width, Height = c.Height, Dx = c.Dx, Dy = c.Dy, Depth = c.Depth, Signed = c.Signed, Component = c.Component, Lookup = c.Lookup, Samples = c.Samples, Opacity = true };

    private static long U32(byte[] data, int p) =>
        p + 4 <= data.Length ? ((long)data[p] << 24) | ((long)data[p + 1] << 16) | ((long)data[p + 2] << 8) | data[p + 3] : throw new Jpeg2000FormatException("A JP2 box is truncated.");
}
