namespace PdfNetStack.Imaging;

/// <summary>JBIG2 data that is damaged, unsupported or larger than the image allows.</summary>
internal sealed class Jbig2FormatException(string message) : Exception(message);

/// <summary>
/// The work one image may cost, in pixels decoded, drawn or copied. Every bitmap is paid for
/// before it is allocated, so hostile sizes and counts stop here instead of in memory or time.
/// </summary>
internal sealed class Jbig2Budget(long pixels)
{
    /// <summary>The cost of one coded step that is not a pixel: a symbol instance, a height class, an export run.</summary>
    public const int Step = 64;

    private long _left = pixels;

    public void Spend(long pixels)
    {
        if (pixels < 0 || pixels > _left) throw new Jbig2FormatException("The data needs more work than the image size allows.");
        _left -= pixels;
    }
}

/// <summary>A 1 bit image, rows packed most significant bit first, 1 for black (T.88, 4.2).</summary>
internal sealed class Jbig2Bitmap
{
    // A bitmap object costs more than its pixels: this keeps millions of empty symbols in check.
    private const int Overhead = 64;

    private Jbig2Bitmap(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = (width + 7) >> 3;
        Data = new byte[(long)Stride * height];
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Data { get; }

    /// <exception cref="Jbig2FormatException">The size is negative or over the budget.</exception>
    public static Jbig2Bitmap Create(long width, long height, Jbig2Budget budget)
    {
        if (width < 0 || height < 0 || width > int.MaxValue - 7 || height > int.MaxValue) throw new Jbig2FormatException("A bitmap size is out of range.");
        budget.Spend((width * height) + Overhead);
        if (((width + 7) >> 3) * height > Compat.MaxArrayLength) throw new Jbig2FormatException("A bitmap size is out of range.");
        return new Jbig2Bitmap((int)width, (int)height);
    }

    /// <summary>The pixel, 0 outside the bitmap (T.88, 6.2.5.2).</summary>
    public int Get(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height ? (Data[(y * Stride) + (x >> 3)] >> (7 - (x & 7))) & 1 : 0;

    /// <summary>Sets a pixel inside the bitmap.</summary>
    public void Set(int x, int y, int value)
    {
        int at = (y * Stride) + (x >> 3);
        byte bit = (byte)(0x80 >> (x & 7));
        Data[at] = value != 0 ? (byte)(Data[at] | bit) : (byte)(Data[at] & ~bit);
    }

    public void Fill(int value) => Data.AsSpan().Fill(value != 0 ? (byte)0xFF : (byte)0);

    /// <summary>Sets the pixels of row <paramref name="y"/> from column <paramref name="from"/> up to <paramref name="to"/>, inside the bitmap, to 1.</summary>
    public void SetRun(int y, int from, int to)
    {
        int first = from >> 3, last = (to - 1) >> 3, row = y * Stride;
        for (int b = first; b <= last; b++) Data[row + b] |= (byte)Mask(b, from, to);
    }

    /// <summary>Row <paramref name="to"/> becomes a copy of row <paramref name="from"/>, or 0 when that is above the bitmap.</summary>
    public void CopyRow(int from, int to)
    {
        Span<byte> target = Data.AsSpan(to * Stride, Stride);
        if (from < 0) target.Clear();
        else Data.AsSpan(from * Stride, Stride).CopyTo(target);
    }

    /// <summary>The columns from <paramref name="x"/>, <paramref name="width"/> wide: a symbol of a collective bitmap.</summary>
    public Jbig2Bitmap Columns(int x, int width, Jbig2Budget budget)
    {
        Jbig2Bitmap result = Create(width, Height, budget);
        long right = Math.Min(width, (long)Width - x);
        if (right > 0 && Height > 0) result.Draw(this, -(long)x, 0, Replace, Math.Max(0, -(long)x), 0, right, Height);
        return result;
    }

    /// <summary>
    /// Draws <paramref name="source"/> with its top left at (<paramref name="x"/>, <paramref name="y"/>),
    /// combined by OR, AND, XOR, XNOR or REPLACE (T.88, 7.4.1.5 and 6.4.5). Parts outside are dropped.
    /// </summary>
    public void Compose(Jbig2Bitmap source, long x, long y, int op, Jbig2Budget budget) => Compose(source, x, y, op, budget, 0, 0, Width, Height);

    /// <summary>
    /// Like <see cref="Compose(Jbig2Bitmap, long, long, int, Jbig2Budget)"/>, drawing only inside the
    /// rectangle from (<paramref name="clipLeft"/>, <paramref name="clipTop"/>) to
    /// (<paramref name="clipRight"/>, <paramref name="clipBottom"/>).
    /// </summary>
    public void Compose(Jbig2Bitmap source, long x, long y, int op, Jbig2Budget budget, long clipLeft, long clipTop, long clipRight, long clipBottom)
    {
        long left = Math.Max(Math.Max(0, clipLeft), x), top = Math.Max(Math.Max(0, clipTop), y);
        long right = Math.Min(Math.Min(Width, clipRight), x + source.Width), bottom = Math.Min(Math.Min(Height, clipBottom), y + source.Height);
        if (left >= right || top >= bottom) return;
        budget.Spend((right - left) * (bottom - top));
        Draw(source, x, y, op, left, top, right, bottom);
    }

    /// <summary>Sets the rectangle from (<paramref name="left"/>, <paramref name="top"/>) to (<paramref name="right"/>, <paramref name="bottom"/>), cut to the bitmap, to <paramref name="value"/>.</summary>
    public void Fill(long left, long top, long right, long bottom, int value, Jbig2Budget budget)
    {
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(Width, right);
        bottom = Math.Min(Height, bottom);
        if (left >= right || top >= bottom) return;
        budget.Spend((right - left) * (bottom - top));
        int first = (int)(left >> 3), last = (int)((right - 1) >> 3);
        for (int y = (int)top; y < bottom; y++)
        {
            for (int b = first, at = (y * Stride) + first; b <= last; b++, at++)
            {
                int mask = Mask(b, left, right);
                Data[at] = (byte)(value != 0 ? Data[at] | mask : Data[at] & ~mask);
            }
        }
    }

    /// <summary>The bits of byte <paramref name="b"/> of a row that fall in the columns from <paramref name="left"/> to <paramref name="right"/>.</summary>
    private static int Mask(int b, long left, long right)
    {
        int mask = 0xFF;
        if (b == left >> 3) mask &= 0xFF >> (int)(left & 7);
        if (b == (right - 1) >> 3) mask &= (0xFF << (7 - (int)((right - 1) & 7))) & 0xFF;
        return mask;
    }

    /// <summary>
    /// Combines the source into the columns from <paramref name="left"/> to <paramref name="right"/>
    /// and the rows from <paramref name="top"/> to <paramref name="bottom"/>, which lie inside both
    /// bitmaps, a byte at a time: the source bits are shifted into the target's alignment, and the
    /// operator applies under a mask of the columns drawn.
    /// </summary>
    private void Draw(Jbig2Bitmap source, long x, long y, int op, long left, long top, long right, long bottom)
    {
        int first = (int)(left >> 3), last = (int)((right - 1) >> 3);

        // Target byte b holds columns 8b to 8b + 7, which show source columns from 8b - x: every
        // target byte takes the same bit offset into a pair of source bytes.
        long start = (8L * first) - x;
        int shift = 8 - (int)(start & 7);
        int firstSource = (int)(start >> 3);
        byte[] target = Data, bits = source.Data;
        int sourceStride = source.Stride;
        for (int ty = (int)top; ty < bottom; ty++)
        {
            int row = ty * Stride, sourceRow = (int)(ty - y) * sourceStride;
            int s = firstSource;
            for (int b = first; b <= last; b++, s++)
            {
                // Source bytes outside the row read as 0; the mask keeps them out of the result.
                int high = s >= 0 && s < sourceStride ? bits[sourceRow + s] : 0;
                int low = s + 1 >= 0 && s + 1 < sourceStride ? bits[sourceRow + s + 1] : 0;
                int value = (((high << 8) | low) >> shift) & 0xFF;
                int mask = b == first || b == last ? Mask(b, left, right) : 0xFF;
                int d = target[row + b];
                target[row + b] = (byte)(op switch
                {
                    Or => d | (value & mask),
                    And => d & (value | ~mask),
                    Xor => d ^ (value & mask),
                    Xnor => d ^ (~value & mask),
                    _ => (d & ~mask) | (value & mask),
                });
            }
        }
    }

    public const int Or = 0;
    public const int And = 1;
    public const int Xor = 2;
    public const int Xnor = 3;
    public const int Replace = 4;
}
