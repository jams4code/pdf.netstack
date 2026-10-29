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

    /// <summary>Samples, rows top to bottom; signed channels hold negative values.</summary>
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
        var columns = new int[Width];
        for (int c = 0; c < channels; c++)
        {
            Jpeg2000Channel ch = Channels[c];
            int max = (1 << ch.Depth) - 1, offset = ch.Signed ? 1 << (ch.Depth - 1) : 0;
            var table = new byte[max + 1];
            for (int v = 0; v <= max; v++) table[v] = scale ? (byte)(((v * 255L) + (max >> 1)) / max) : (byte)Math.Min(v, 255);

            int cx0 = Ceil(X0, ch.Dx), cy0 = Ceil(Y0, ch.Dy);
            for (int x = 0; x < Width; x++) columns[x] = Compat.Clamp(((X0 + x) / ch.Dx) - cx0, 0, ch.Width - 1);
            for (int y = 0; y < Height; y++)
            {
                int row = Compat.Clamp(((Y0 + y) / ch.Dy) - cy0, 0, ch.Height - 1) * ch.Width;
                long o = ((long)y * Width * channels) + c;
                for (int x = 0; x < Width; x++, o += channels) output[o] = table[Compat.Clamp(ch.Samples[row + columns[x]] + offset, 0, max)];
            }
        }

        return new Raster(Width, Height, channels, output);
    }

    private static int Ceil(int a, int b) => (int)(((long)a + b - 1) / b);
}

/// <summary>
/// Decodes JPEG 2000 (ITU-T T.800, part 1): JP2 files and raw codestreams. Tier-2 packet
/// decoding in every progression order with precincts, layers, PPM, PPT, SOP and EPH; tier-1
/// EBCOT with all code-block styles; scalar and reversible dequantization with ROI shifts; the
/// 5/3 and 9/7 inverse wavelets; RCT and ICT; DC level shift. Every size, count and the total
/// work are bounded by the caller's limits, and malformed data throws
/// <see cref="Jpeg2000FormatException"/>.
/// </summary>
internal static class Jpeg2000Decoder
{
    /// <summary>Decodes a JP2 file or codestream.</summary>
    /// <param name="byteLimit">Ceiling for the decoder's buffers.</param>
    /// <param name="maxPixels">Ceiling for the image area.</param>
    /// <param name="applyPalette">False keeps palette indexes, for a PDF Indexed color space.</param>
    /// <exception cref="Jpeg2000FormatException">The data is damaged, too large or unsupported.</exception>
    public static Jpeg2000Image Decode(ReadOnlySpan<byte> data, long byteLimit, long maxPixels = ImageSamples.MaxPixels, bool applyPalette = true)
    {
        byte[] bytes = data.ToArray();
        try
        {
            Jp2Header? header = null;
            int start = 0, end = bytes.Length;
            if (bytes.Length >= 12 && bytes[3] == 12 && bytes[4] == 0x6A && bytes[5] == 0x50 && bytes[6] == 0x20 && bytes[7] == 0x20)
            {
                header = ReadBoxes(bytes, out start, out end);
            }

            Jpeg2000Codestream stream = Jpeg2000Codestream.Read(bytes, start, end, maxPixels);
            int[][] planes = DecodeTiles(stream, byteLimit);
            return Assemble(stream, planes, header, applyPalette);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // Defensive: a bound missed above surfaces as a format error, never as a crash.
            throw new Jpeg2000FormatException("The JPEG 2000 data is malformed.", e);
        }
    }

    /// <summary>
    /// Decodes for a PDF image (ISO 32000-1, 7.4.9): the dictionary's color space wins and keeps
    /// palette indexes for Indexed; without one the JP2 header decides, then the channel count.
    /// Returns 8 bit samples in the space's component count, or a null raster when nothing fits.
    /// </summary>
    /// <exception cref="Jpeg2000FormatException">The data is damaged, too large or unsupported.</exception>
    public static (Raster? Raster, ColorSpace? Space) DecodeForPdf(byte[] data, ColorSpace? space, long byteLimit)
    {
        bool indexed = space?.IsIndexed == true;
        Jpeg2000Image image = Decode(data, byteLimit, ImageSamples.MaxPixels, applyPalette: !indexed);
        int colors = image.Channels.Count(c => !c.Opacity);
        List<Jpeg2000Channel> ordered = [.. image.Channels.Where(c => !c.Opacity), .. image.Channels.Where(c => c.Opacity)];
        var colorFirst = new Jpeg2000Image { X0 = image.X0, Y0 = image.Y0, Width = image.Width, Height = image.Height, Channels = ordered, ColorSpace = image.ColorSpace };

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
            if (image.ColorSpace == 18 && space == ColorSpace.Rgb) colorFirst = Sycc(colorFirst);
            indexed = false;
        }

        if (indexed && ordered[0].Depth > 8) return (null, space);
        return (colorFirst.ToRaster(space.Components, scale: !indexed), space);
    }

    /// <summary>The image's components, decoded tile by tile into planes at component resolution.</summary>
    private static int[][] DecodeTiles(Jpeg2000Codestream stream, long byteLimit)
    {
        Jpeg2000Component[] components = stream.Components;
        int n = components.Length;

        // Planes plus the largest tile's buffers must fit the limit before anything is allocated.
        long planeBytes = 0, tileBytes = 0;
        for (int c = 0; c < n; c++)
        {
            long w = Ceil(stream.X1, components[c].Dx) - Ceil(stream.X0, components[c].Dx);
            long h = Ceil(stream.Y1, components[c].Dy) - Ceil(stream.Y0, components[c].Dy);
            if (w * h > Compat.MaxArrayLength) throw new Jpeg2000FormatException("The image is too large.");
            planeBytes += w * h * 4;
            tileBytes += Math.Min(w, ((long)stream.TileWidth / components[c].Dx) + 2) * Math.Min(h, ((long)stream.TileHeight / components[c].Dy) + 2) * 4;
        }

        if (planeBytes + tileBytes > byteLimit) throw new Jpeg2000FormatException("The image exceeds the decode limit.");
        var planes = new int[n][];
        for (int c = 0; c < n; c++)
        {
            long w = Ceil(stream.X1, components[c].Dx) - Ceil(stream.X0, components[c].Dx);
            long h = Ceil(stream.Y1, components[c].Dy) - Ceil(stream.Y0, components[c].Dy);
            planes[c] = new int[w * h];
        }

        var block = new Jpeg2000BlockDecoder();
        long blockLimit = Math.Max(4096, byteLimit / 128);
        for (int t = 0; t < stream.Tiles.Length; t++)
        {
            if (stream.Tiles[t] is Jpeg2000TileParts parts) DecodeTile(stream, t, parts, planes, block, blockLimit);
        }

        return planes;
    }

    private static void DecodeTile(Jpeg2000Codestream stream, int t, Jpeg2000TileParts parts, int[][] planes, Jpeg2000BlockDecoder block, long blockLimit)
    {
        Jpeg2000Parameters main = stream.Main, tile = parts.Parameters;
        Jpeg2000Parameters general = tile.HasCod ? tile : main;
        int p = t % stream.TilesAcross, q = t / stream.TilesAcross;
        int tx0 = (int)Math.Max(stream.X0, stream.TileX0 + ((long)p * stream.TileWidth));
        int ty0 = (int)Math.Max(stream.Y0, stream.TileY0 + ((long)q * stream.TileHeight));
        int tx1 = (int)Math.Min(stream.X1, stream.TileX0 + ((long)(p + 1) * stream.TileWidth));
        int ty1 = (int)Math.Min(stream.Y1, stream.TileY0 + ((long)(q + 1) * stream.TileHeight));

        int n = stream.Components.Length;
        var tcs = new Jpeg2000TileComponent[n];
        var quantizations = new Jpeg2000Quantization[n];
        int maxResolutions = 0;
        long precincts = 0;
        for (int c = 0; c < n; c++)
        {
            // Precedence (A.6): tile COC, tile COD, main COC, main COD; likewise for QCC and QCD.
            Jpeg2000Coding coding = tile.ComponentCoding[c] ?? tile.Coding ?? main.ComponentCoding[c] ?? main.Coding!;
            quantizations[c] = tile.ComponentQuantization[c] ?? tile.Quantization ?? main.ComponentQuantization[c] ?? main.Quantization!;
            int roi = tile.RoiShift[c] ?? main.RoiShift[c] ?? 0;
            tcs[c] = BuildComponent(stream.Components[c], coding, quantizations[c], roi, (tx0, ty0, tx1, ty1));
            foreach (Jpeg2000Resolution res in tcs[c].Resolutions) precincts += res.NextLayer.Length;
            if (precincts > blockLimit) throw new Jpeg2000FormatException("A tile has too many precincts.");
            maxResolutions = Math.Max(maxResolutions, coding.Levels + 1);
        }

        // The tile's data, and its packet headers when PPM or PPT carry them (A.7.4, A.7.5).
        int total = 0;
        foreach ((int _, int length) in parts.Bodies) total += length;
        var body = new byte[total];
        int at = 0;
        foreach ((int offset, int length) in parts.Bodies)
        {
            Buffer.BlockCopy(stream.Data, offset, body, at, length);
            at += length;
        }

        byte[]? headers = null;
        if (parts.Ppm is not null) headers = Concatenate(parts.Ppm);
        else if (parts.Ppt is not null) headers = Concatenate(parts.Ppt.Values);

        List<Jpeg2000Progression> progressions = tile.Progressions.Count > 0 ? tile.Progressions : main.Progressions;
        if (progressions.Count == 0) progressions = [new Jpeg2000Progression(0, 0, general.Layers, maxResolutions, n, general.Order)];
        var packets = new Jpeg2000Packets(tcs, body, headers, general.Layers, general.Sop, general.Eph, blockLimit)
        {
            ComponentDx = [.. stream.Components.Select(c => c.Dx)],
            ComponentDy = [.. stream.Components.Select(c => c.Dy)],
            TileX0 = tx0,
            TileY0 = ty0,
        };
        packets.Read(progressions);

        // Tier-1, dequantization and the inverse wavelet per component; then MCT and level shift.
        var integers = new int[n][];
        var reals = new float[n][];
        for (int c = 0; c < n; c++)
        {
            Jpeg2000TileComponent tc = tcs[c];
            if (tc.Width <= 0 || tc.Height <= 0) continue;
            if (tc.Coding.Reversible) integers[c] = new int[(long)tc.Width * tc.Height];
            else reals[c] = new float[(long)tc.Width * tc.Height];
            Coefficients(tc, block, integers[c], reals[c]);
            if (tc.Coding.Reversible) Jpeg2000Wavelet.Inverse(integers[c], tc);
            else Jpeg2000Wavelet.Inverse(reals[c], tc);
        }

        if (general.Mct && n >= 3) InverseColorTransform(tcs, integers, reals);

        for (int c = 0; c < n; c++)
        {
            Jpeg2000TileComponent tc = tcs[c];
            if (tc.Width <= 0 || tc.Height <= 0) continue;
            Jpeg2000Component comp = stream.Components[c];
            int cx0 = Ceil(stream.X0, comp.Dx), cy0 = Ceil(stream.Y0, comp.Dy);
            int planeWidth = Ceil(stream.X1, comp.Dx) - cx0;
            int shift = comp.Signed ? 0 : 1 << (comp.Depth - 1);
            int min = comp.Signed ? -(1 << (comp.Depth - 1)) : 0, max = comp.Signed ? (1 << (comp.Depth - 1)) - 1 : (1 << comp.Depth) - 1;
            int[] plane = planes[c];
            for (int y = 0; y < tc.Height; y++)
            {
                int from = y * tc.Width, to = ((tc.Y0 - cy0 + y) * planeWidth) + tc.X0 - cx0;
                if (integers[c] is int[] ints)
                {
                    for (int x = 0; x < tc.Width; x++) plane[to + x] = Compat.Clamp(ints[from + x] + shift, min, max);
                }
                else
                {
                    float[] floats = reals[c];

                    // Rounded half to even, as lrintf in OpenJPEG.
                    for (int x = 0; x < tc.Width; x++)
                    {
                        double v = Math.Round((double)floats[from + x]);
                        plane[to + x] = v >= max - shift ? max : v <= min - shift ? min : (int)v + shift;
                    }
                }
            }
        }
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
                bands = [Band(comp, coding, quant, 0, 0, rx0, ry0, rx1, ry1, 0, 0)];
            }
            else
            {
                int lw = CeilShift(rx1, 1) - CeilShift(rx0, 1), lh = CeilShift(ry1, 1) - CeilShift(ry0, 1);
                bands =
                [
                    Band(comp, coding, quant, r, 1, rx0 >> 1, CeilShift(ry0, 1), rx1 >> 1, CeilShift(ry1, 1), lw, 0),
                    Band(comp, coding, quant, r, 2, CeilShift(rx0, 1), ry0 >> 1, CeilShift(rx1, 1), ry1 >> 1, 0, lh),
                    Band(comp, coding, quant, r, 3, rx0 >> 1, ry0 >> 1, rx1 >> 1, ry1 >> 1, lw, lh),
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

    private static Jpeg2000Band Band(Jpeg2000Component comp, Jpeg2000Coding coding, Jpeg2000Quantization quant, int r, int orientation, int x0, int y0, int x1, int y1, int offsetX, int offsetY)
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
            OffsetX = offsetX,
            OffsetY = offsetY,
        };
    }

    /// <summary>Every code-block of a tile-component through tier-1 and dequantization into its buffer.</summary>
    private static void Coefficients(Jpeg2000TileComponent tc, Jpeg2000BlockDecoder decoder, int[]? integers, float[]? reals)
    {
        int stride = tc.Width;
        foreach (Jpeg2000Resolution res in tc.Resolutions)
        {
            foreach (Jpeg2000PrecinctBand[]? precinct in res.Precincts)
            {
                if (precinct is null) continue;
                for (int b = 0; b < precinct.Length; b++)
                {
                    Jpeg2000Band band = res.Bands[b];
                    foreach (Jpeg2000CodeBlock cb in precinct[b].Blocks)
                    {
                        if (cb.Segments is null) continue;
                        int planes = band.Planes + tc.RoiShift - cb.ZeroPlanes;
                        int[] data = decoder.Decode(cb, band.Orientation, tc.Coding.Style, planes);
                        int w = cb.X1 - cb.X0, h = cb.Y1 - cb.Y0;
                        if (tc.RoiShift > 0) Unshift(data, w * h, tc.RoiShift);
                        int origin = ((band.OffsetY + cb.Y0 - band.Y0) * stride) + band.OffsetX + cb.X0 - band.X0;
                        float half = band.Step / 2;
                        for (int y = 0; y < h; y++)
                        {
                            int o = origin + (y * stride), i = y * w;
                            if (integers is not null)
                            {
                                for (int x = 0; x < w; x++) integers[o + x] = data[i + x] / 2;
                            }
                            else
                            {
                                for (int x = 0; x < w; x++) reals![o + x] = data[i + x] * half;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// The maximum shift method (T.800 H.1): coefficients at or above 2^s are the region of
    /// interest and are shifted back down; the rest is background. Values here are doubled.
    /// </summary>
    private static void Unshift(int[] data, int count, int shift)
    {
        if (shift >= 30)
        {
            Array.Clear(data, 0, count);
            return;
        }

        int threshold = 1 << (shift + 1);
        for (int i = 0; i < count; i++)
        {
            int v = data[i], magnitude = Math.Abs(v);
            if (magnitude >= threshold) data[i] = v < 0 ? -(magnitude >> shift) : magnitude >> shift;
        }
    }

    /// <summary>The inverse RCT (G-2) or ICT (G-6) on the first three components.</summary>
    private static void InverseColorTransform(Jpeg2000TileComponent[] tcs, int[][] integers, float[][] reals)
    {
        int w = tcs[0].Width, h = tcs[0].Height;
        if (w <= 0 || h <= 0) return;
        if (tcs[1].Width != w || tcs[2].Width != w || tcs[1].Height != h || tcs[2].Height != h) throw new Jpeg2000FormatException("The color transform needs components of one size.");
        int count = w * h;
        if (integers[0] is not null && integers[1] is not null && integers[2] is not null)
        {
            int[] y0 = integers[0], y1 = integers[1], y2 = integers[2];
            for (int i = 0; i < count; i++)
            {
                int g = y0[i] - ((y2[i] + y1[i]) >> 2);
                y0[i] = y2[i] + g;
                y2[i] = y1[i] + g;
                y1[i] = g;
            }
        }
        else if (reals[0] is not null && reals[1] is not null && reals[2] is not null)
        {
            float[] y0 = reals[0], y1 = reals[1], y2 = reals[2];
            for (int i = 0; i < count; i++)
            {
                float y = y0[i], cb = y1[i], cr = y2[i];
                y0[i] = y + (1.402f * cr);
                y1[i] = y - (0.34413f * cb) - (0.71414f * cr);
                y2[i] = y + (1.772f * cb);
            }
        }
        else
        {
            throw new Jpeg2000FormatException("The color transform mixes reversible and irreversible components.");
        }
    }

    private static int Ceil(int a, int b) => (int)(((long)a + b - 1) / b);

    private static int CeilShift(int a, int n) => n >= 31 ? (a > 0 ? 1 : 0) : (int)(((long)a + (1L << n) - 1) >> n);

    /// <summary>sYCC to sRGB (IEC 61966-2-1 Amendment 1), chroma centered at half the range.</summary>
    private static Jpeg2000Image Sycc(Jpeg2000Image image)
    {
        IReadOnlyList<Jpeg2000Channel> ch = image.Channels;
        if (ch.Count < 3 || ch[0].Width != ch[1].Width || ch[0].Width != ch[2].Width || ch[0].Height != ch[1].Height || ch[0].Height != ch[2].Height) return image;
        int depth = ch[0].Depth, max = (1 << depth) - 1, half = 1 << (depth - 1);
        int count = ch[0].Samples.Length;
        var r = new int[count];
        var g = new int[count];
        var b = new int[count];
        for (int i = 0; i < count; i++)
        {
            double y = ch[0].Samples[i], cb = ch[1].Samples[i] - half, cr = ch[2].Samples[i] - half;
            r[i] = Compat.Clamp((int)Math.Round(y + (1.402 * cr)), 0, max);
            g[i] = Compat.Clamp((int)Math.Round(y - (0.344136 * cb) - (0.714136 * cr)), 0, max);
            b[i] = Compat.Clamp((int)Math.Round(y + (1.772 * cb)), 0, max);
        }

        Jpeg2000Channel Plane(Jpeg2000Channel like, int[] samples) =>
            new() { Width = like.Width, Height = like.Height, Dx = like.Dx, Dy = like.Dy, Depth = depth, Samples = samples };
        List<Jpeg2000Channel> channels = [Plane(ch[0], r), Plane(ch[0], g), Plane(ch[0], b), .. ch.Skip(3)];
        return new Jpeg2000Image { X0 = image.X0, Y0 = image.Y0, Width = image.Width, Height = image.Height, Channels = channels, ColorSpace = 16 };
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

    /// <summary>The channels: components through the palette (I.5.3.4, I.5.3.5), ordered by cdef (I.5.3.6).</summary>
    private static Jpeg2000Image Assemble(Jpeg2000Codestream stream, int[][] planes, Jp2Header? header, bool applyPalette)
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
            Samples = planes[c],
        };

        if (applyPalette && header?.Palette is int[][] palette && header.Mapping is { Length: > 0 } mapping)
        {
            foreach ((int component, int type, int column) in mapping)
            {
                if (component >= planes.Length) throw new Jpeg2000FormatException("The JP2 component mapping names a component that does not exist.");
                Jpeg2000Channel source = FromComponent(component);
                if (type != 1)
                {
                    channels.Add(source);
                    continue;
                }

                if (column >= palette.Length) throw new Jpeg2000FormatException("The JP2 component mapping names a palette column that does not exist.");
                int[] entries = palette[column];
                var samples = new int[source.Samples.Length];
                for (int i = 0; i < samples.Length; i++) samples[i] = entries[Compat.Clamp(source.Samples[i], 0, entries.Length - 1)];
                channels.Add(new Jpeg2000Channel
                {
                    Width = source.Width,
                    Height = source.Height,
                    Dx = source.Dx,
                    Dy = source.Dy,
                    Depth = header.PaletteDepths[column],
                    Signed = header.PaletteSigned[column],
                    Samples = samples,
                });
            }
        }
        else
        {
            for (int c = 0; c < planes.Length; c++) channels.Add(FromComponent(c));
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
        new() { Width = c.Width, Height = c.Height, Dx = c.Dx, Dy = c.Dy, Depth = c.Depth, Signed = c.Signed, Samples = c.Samples, Opacity = true };

    private static long U32(byte[] data, int p) =>
        p + 4 <= data.Length ? ((long)data[p] << 24) | ((long)data[p + 1] << 16) | ((long)data[p + 2] << 8) | data[p + 3] : throw new Jpeg2000FormatException("A JP2 box is truncated.");
}
