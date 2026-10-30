namespace PdfNetStack.Imaging;

/// <summary>
/// Reduces 1 bit images, such as scanned text, so that text stays legible. Each output pixel
/// takes the share of its area that 1 bits cover in the source: an exact box filter, with
/// fractional weights where pixel edges do not line up. The foreground, the less common of the two
/// values, wins a pixel it covers more than half of. It also wins a pixel it covers at least a
/// quarter of when that pixel is a ridge across or along the image: covered more than the
/// neighbour before it and at least as much as the neighbour after it. That is the middle of a
/// stroke thinner than the new pixels, which plain thresholding would erase; a stroke split
/// evenly between two pixels keeps the first, so it does not grow, and the soft edges of wide
/// strokes stay background. The arithmetic is exact and in a fixed order, so the proof
/// reproduces the result bit for bit.
/// </summary>
internal static class BilevelResampler
{
    /// <summary>The smallest share of a ridge pixel the foreground must cover to keep it.</summary>
    public const double RidgeShare = 0.25;

    /// <summary>
    /// The share of each output pixel covered by 1 bits, 0 to 1, rows top to bottom.
    /// <paramref name="rows"/> holds <paramref name="height"/> rows of <paramref name="width"/>
    /// bits, each padded to a whole byte.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target is larger than the source or the rows are too short.</exception>
    public static double[] Coverage(ReadOnlySpan<byte> rows, int width, int height, int targetWidth, int targetHeight)
    {
        if (targetWidth < 1 || targetHeight < 1 || targetWidth > width || targetHeight > height)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "The target size must be positive and no larger than the source.");
        }

        int rowBytes = (width + 7) / 8;
        if (rows.Length < (long)rowBytes * height) throw new ArgumentOutOfRangeException(nameof(rows), "The rows are shorter than the size.");

        // Each source column lies across at most two output columns: the first gets weight
        // Near[x], the next the rest. The same holds for rows.
        (int[] columnOf, double[] columnNear) = Spans(width, targetWidth);
        (int[] rowOf, double[] rowNear) = Spans(height, targetHeight);
        var coverage = new double[(long)targetWidth * targetHeight];
        var line = new double[targetWidth + 1];
        for (int y = 0; y < height; y++)
        {
            Array.Clear(line, 0, line.Length);
            ReadOnlySpan<byte> row = rows.Slice(y * rowBytes, rowBytes);
            for (int i = 0; i < rowBytes; i++)
            {
                int v = row[i];
                if (v == 0) continue;
                int end = Math.Min(8, width - (i << 3));
                for (int bit = 0; bit < end; bit++)
                {
                    if ((v & (0x80 >> bit)) == 0) continue;
                    int x = (i << 3) + bit;
                    line[columnOf[x]] += columnNear[x];
                    line[columnOf[x] + 1] += 1 - columnNear[x];
                }
            }

            int target = rowOf[y];
            double near = rowNear[y], far = 1 - near;
            long o = (long)target * targetWidth;
            for (int x = 0; x < targetWidth; x++) coverage[o + x] += near * line[x];
            if (far > 0 && target + 1 < targetHeight)
            {
                o += targetWidth;
                for (int x = 0; x < targetWidth; x++) coverage[o + x] += far * line[x];
            }
        }

        double area = (double)width / targetWidth * height / targetHeight;
        for (long i = 0; i < coverage.LongLength; i++) coverage[i] = Math.Min(1, coverage[i] / area);
        return coverage;
    }

    /// <summary>
    /// The output column each source position starts in, and the part of the source pixel that
    /// falls in it; the remainder falls in the next output column.
    /// </summary>
    private static (int[] Of, double[] Near) Spans(int size, int targetSize)
    {
        var of = new int[size];
        var near = new double[size];
        for (int x = 0; x < size; x++)
        {
            // Output pixel t covers source positions [t * size / targetSize, (t + 1) * size / targetSize),
            // compared here in whole numbers scaled by targetSize.
            long start = (long)x * targetSize;
            int t = (int)(start / size);
            long boundary = (long)(t + 1) * size;
            of[x] = t;
            near[x] = boundary >= start + targetSize ? 1 : (double)(boundary - start) / targetSize;
        }

        return (of, near);
    }

    /// <summary>True when 1 bits are the less common value, so they are the foreground.</summary>
    public static bool OnesAreForeground(ReadOnlySpan<byte> rows, int width, int height)
    {
        int rowBytes = (width + 7) / 8;
        long ones = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rows.Slice(y * rowBytes, rowBytes);
            for (int i = 0; i < rowBytes; i++)
            {
                int v = row[i];
                int end = Math.Min(8, width - (i << 3));
                if (end < 8) v &= 0xFF << (8 - end);
                ones += BitCount(v);
            }
        }

        return ones * 2 < (long)width * height;
    }

    private static int BitCount(int v)
    {
        int n = 0;
        for (; v != 0; v &= v - 1) n++;
        return n;
    }

    /// <summary>The output pixels, packed as rows of bits padded to a whole byte, from the coverage of 1 bits.</summary>
    public static byte[] Threshold(double[] coverage, int width, int height, bool onesAreForeground)
    {
        int rowBytes = (width + 7) / 8;
        var output = new byte[(long)rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool foreground = IsForeground(coverage, width, height, x, y, onesAreForeground);
                if (foreground == onesAreForeground) output[(y * rowBytes) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return output;
    }

    private static bool IsForeground(double[] coverage, int width, int height, int x, int y, bool onesAreForeground)
    {
        double Share(int px, int py)
        {
            if (px < 0 || py < 0 || px >= width || py >= height) return 0;
            double ones = coverage[((long)py * width) + px];
            return onesAreForeground ? ones : 1 - ones;
        }

        double share = Share(x, y);
        if (share > 0.5) return true;
        if (share < RidgeShare) return false;
        return Ridge(Share(x - 1, y), share, Share(x + 1, y)) || Ridge(Share(x, y - 1), share, Share(x, y + 1));
    }

    private static bool Ridge(double before, double share, double after) => before < share && after <= share;

    /// <summary>Coverage as 8 bit gray, the value of the 1 bits' share times 255: the source as the new grid sees it.</summary>
    public static Raster Gray(double[] coverage, int width, int height)
    {
        var samples = new byte[coverage.LongLength];
        for (long i = 0; i < samples.LongLength; i++) samples[i] = (byte)(int)((coverage[i] * 255) + 0.5);
        return new Raster(width, height, 1, samples);
    }

    /// <summary>Packed rows as 8 bit gray, 255 for a 1 bit and 0 for a 0 bit.</summary>
    public static Raster Gray(ReadOnlySpan<byte> rows, int width, int height)
    {
        int rowBytes = (width + 7) / 8;
        var samples = new byte[(long)width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if ((rows[(y * rowBytes) + (x >> 3)] & (0x80 >> (x & 7))) != 0) samples[((long)y * width) + x] = 255;
            }
        }

        return new Raster(width, height, 1, samples);
    }
}
