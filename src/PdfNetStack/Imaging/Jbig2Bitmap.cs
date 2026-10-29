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
        for (int y = 0; y < Height; y++)
        {
            for (int i = 0; i < width; i++)
            {
                if (Get(x + i, y) != 0) result.Set(i, y, 1);
            }
        }

        return result;
    }

    /// <summary>
    /// Draws <paramref name="source"/> with its top left at (<paramref name="x"/>, <paramref name="y"/>),
    /// combined by OR, AND, XOR, XNOR or REPLACE (T.88, 7.4.1.5 and 6.4.5). Parts outside are dropped.
    /// </summary>
    public void Compose(Jbig2Bitmap source, long x, long y, int op, Jbig2Budget budget)
    {
        long left = Math.Max(0, x), top = Math.Max(0, y);
        long right = Math.Min(Width, x + source.Width), bottom = Math.Min(Height, y + source.Height);
        if (left >= right || top >= bottom) return;
        budget.Spend((right - left) * (bottom - top));
        for (int ty = (int)top; ty < bottom; ty++)
        {
            int sy = (int)(ty - y);
            for (int tx = (int)left; tx < right; tx++)
            {
                int s = source.Get((int)(tx - x), sy);
                int d = Get(tx, ty);
                int v = op switch
                {
                    Or => d | s,
                    And => d & s,
                    Xor => d ^ s,
                    Xnor => 1 ^ d ^ s,
                    _ => s,
                };
                if (v != d) Set(tx, ty, v);
            }
        }
    }

    public const int Or = 0;
    public const int And = 1;
    public const int Xor = 2;
    public const int Xnor = 3;
    public const int Replace = 4;
}
