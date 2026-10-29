namespace PdfNetStack.Imaging;

/// <summary>The pattern dictionary (T.88, 6.7) and halftone region (T.88, 6.6) decoding procedures.</summary>
internal static class Jbig2Halftone
{
    /// <summary>6.7.5: one collective bitmap cut into GRAYMAX + 1 patterns of equal size.</summary>
    public static Jbig2Bitmap[] DecodePatterns(byte[] data, int start, int end, Jbig2Budget budget)
    {
        // 7.4.4.1: flags, HDPW, HDPH, GRAYMAX.
        if (end - start < 7) throw new Jbig2FormatException("A pattern dictionary segment is too short.");
        int flags = data[start], width = data[start + 1], height = data[start + 2];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        long count = Jbig2Decoder.UInt32(data, start + 3) + 1;
        if (width == 0 || height == 0) throw new Jbig2FormatException("A pattern has no size.");
        if (count > Jbig2Text.MaxSymbols) throw new Jbig2FormatException("A pattern dictionary has too many patterns.");

        // The patterns lie side by side; A1 points at the same pixel of the pattern to the left.
        long collectiveWidth = count * width;
        Jbig2Bitmap collective = mmr
            ? Jbig2Generic.DecodeMmr(data.AsSpan(start + 7, end - start - 7), collectiveWidth, height, budget).Bitmap
            : Jbig2Generic.Decode(
                new Jbig2ArithmeticDecoder(data, start + 7, end), Jbig2Generic.GenericContexts(), collectiveWidth, height, template, false, null,
                template == 0 ? [-width, 0, -3, -1, 2, -2, -2, -2] : [-width, 0], budget);

        var patterns = new Jbig2Bitmap[count];
        for (int i = 0; i < count; i++) patterns[i] = collective.Columns(i * width, width, budget);
        return patterns;
    }

    /// <summary>The halftone region segment's fields (7.4.5.1) after the region information.</summary>
    public sealed class Parameters
    {
        public long Width { get; init; }

        public long Height { get; init; }

        public bool Mmr { get; init; }

        public int Template { get; init; }

        public bool Skip { get; init; }

        public int Operator { get; init; }

        public int DefaultPixel { get; init; }

        public long GridWidth { get; init; }

        public long GridHeight { get; init; }

        public long GridX { get; init; }

        public long GridY { get; init; }

        public int VectorX { get; init; }

        public int VectorY { get; init; }
    }

    /// <summary>6.6.5: decodes the gray-scale image, then draws the pattern each gray value names on the grid.</summary>
    public static Jbig2Bitmap DecodeRegion(Parameters p, Jbig2Bitmap[] patterns, byte[] data, int start, int end, Jbig2Budget budget)
    {
        if (patterns.Length == 0) throw new Jbig2FormatException("A halftone region has no patterns.");
        Jbig2Bitmap region = Jbig2Bitmap.Create(p.Width, p.Height, budget);
        if (p.DefaultPixel != 0) region.Fill(1);
        int patternWidth = patterns[0].Width, patternHeight = patterns[0].Height;
        Jbig2Bitmap grid = Jbig2Bitmap.Create(p.GridWidth, p.GridHeight, budget);

        // 6.6.5.1: grid cells whose pattern would fall wholly outside the region are skipped.
        Jbig2Bitmap? skip = null;
        if (p.Skip)
        {
            skip = grid;
            for (int mg = 0; mg < grid.Height; mg++)
            {
                for (int ng = 0; ng < grid.Width; ng++)
                {
                    (long x, long y) = Cell(p, mg, ng);
                    if (x + patternWidth <= 0 || x >= region.Width || y + patternHeight <= 0 || y >= region.Height) skip.Set(ng, mg, 1);
                }
            }
        }

        int bitsPerValue = Jbig2SymbolDictionary.Log2(patterns.Length);
        Jbig2Bitmap[] planes = GrayPlanes(p, bitsPerValue, skip, data, start, end, budget);

        // 6.6.5.2: render each cell's pattern. Values past the last pattern use the last one.
        budget.Spend(grid.Width * (long)grid.Height * Math.Max(1, bitsPerValue));
        for (int mg = 0; mg < grid.Height; mg++)
        {
            for (int ng = 0; ng < grid.Width; ng++)
            {
                long value = 0;
                for (int j = bitsPerValue - 1; j >= 0; j--) value = (value << 1) | (uint)planes[j].Get(ng, mg);
                (long x, long y) = Cell(p, mg, ng);
                region.Compose(patterns[(int)Math.Min(value, patterns.Length - 1)], x, y, p.Operator, budget);
            }
        }

        return region;
    }

    /// <summary>The top left of a grid cell's pattern: the grid origin and vectors are in 1/256 pixel.</summary>
    private static (long X, long Y) Cell(Parameters p, long mg, long ng) =>
        ((p.GridX + (mg * p.VectorY) + (ng * p.VectorX)) >> 8, (p.GridY + (mg * p.VectorX) - (ng * p.VectorY)) >> 8);

    /// <summary>
    /// C.5: the gray-scale image as bitplanes, most significant first, each Gray coded against the
    /// one above it. Returns the planes as plain binary.
    /// </summary>
    private static Jbig2Bitmap[] GrayPlanes(Parameters p, int bitsPerValue, Jbig2Bitmap? skip, byte[] data, int start, int end, Jbig2Budget budget)
    {
        var planes = new Jbig2Bitmap[bitsPerValue];
        Jbig2ArithmeticDecoder? decoder = p.Mmr ? null : new Jbig2ArithmeticDecoder(data, start, end);
        byte[] contexts = Jbig2Generic.GenericContexts();
        int[] at = p.Template == 0 ? [3, -1, -3, -1, 2, -2, -2, -2] : [p.Template == 1 ? 3 : 2, -1];
        int position = start;
        for (int j = bitsPerValue - 1; j >= 0; j--)
        {
            if (decoder is not null)
            {
                planes[j] = Jbig2Generic.Decode(decoder, contexts, p.GridWidth, p.GridHeight, p.Template, false, skip, at, budget);
            }
            else
            {
                // Each MMR plane ends with EOFB and a byte boundary.
                (Jbig2Bitmap plane, long bits) = Jbig2Generic.DecodeMmr(data.AsSpan(position, Math.Max(0, end - position)), p.GridWidth, p.GridHeight, budget);
                planes[j] = plane;
                position = (int)Math.Min(end, position + ((bits + 7) >> 3) + 3);
            }

            if (j + 1 < bitsPerValue)
            {
                byte[] above = planes[j + 1].Data, plane = planes[j].Data;
                for (int i = 0; i < plane.Length; i++) plane[i] ^= above[i];
            }
        }

        return planes;
    }
}
