namespace PdfNetStack.Imaging;

/// <summary>
/// Structural similarity (Wang, Bovik, Sheikh and Simoncelli, 2004) on luminance, the perceptual
/// gate for lossy image changes. Windows are 8 by 8 pixels on a 4 pixel stride, built from sums
/// over 4 by 4 blocks, with the paper's constants for 8 bit data. The score is the mean over all
/// windows: 1 for identical images, falling as structure is lost.
/// </summary>
internal static class Ssim
{
    private const double C1 = 0.01 * 255 * 0.01 * 255;
    private const double C2 = 0.03 * 255 * 0.03 * 255;

    /// <summary>Mean SSIM between two rasters of the same size and channel count.</summary>
    public static double Compare(Raster a, Raster b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Channels != b.Channels)
        {
            throw new ArgumentException("Rasters must have the same size and channels.", nameof(b));
        }

        return Compare(Luminance(a), Luminance(b), a.Width, a.Height);
    }

    /// <summary>
    /// Luminance as a viewer would weigh it, from the samples as stored: BT.601 weights for RGB,
    /// and for four components the same weights on the naive CMYK conversion. Both sides of a
    /// comparison go through the same mapping, so stored ink conventions cancel out.
    /// </summary>
    public static byte[] Luminance(Raster raster)
    {
        int pixels = raster.Width * raster.Height;
        byte[] s = raster.Samples;
        switch (raster.Channels)
        {
            case 1:
                return s;
            case 3:
            {
                var y = new byte[pixels];
                for (int i = 0, o = 0; i < pixels; i++, o += 3) y[i] = (byte)(((77 * s[o]) + (150 * s[o + 1]) + (29 * s[o + 2]) + 128) >> 8);
                return y;
            }

            default:
            {
                var y = new byte[pixels];
                int n = raster.Channels;
                for (int i = 0, o = 0; i < pixels; i++, o += n)
                {
                    int ink = (((77 * s[o]) + (150 * s[o + 1]) + (29 * s[o + 2]) + 128) >> 8) + s[o + 3];
                    y[i] = (byte)(255 - Math.Min(255, ink));
                }

                return y;
            }
        }
    }

    /// <summary>Largest difference between the two images' mean value of any one channel, in sample steps.</summary>
    public static double MeanShift(Raster a, Raster b)
    {
        int n = a.Channels;
        var sumA = new long[n];
        var sumB = new long[n];
        for (int i = 0; i < a.Samples.Length; i++)
        {
            sumA[i % n] += a.Samples[i];
            sumB[i % n] += b.Samples[i];
        }

        double pixels = (double)a.Width * a.Height, worst = 0;
        for (int c = 0; c < n; c++) worst = Math.Max(worst, Math.Abs(sumA[c] - sumB[c]) / pixels);
        return worst;
    }

    private static double Compare(byte[] a, byte[] b, int width, int height)
    {
        int columns = width / 4, rows = height / 4;

        // Too small for one 8 by 8 window: the whole image is the window.
        if (columns < 2 || rows < 2) return Window(Sums.Of(a, b, 0, 0, width, height, width), width * height);

        var previous = new Sums[columns];
        var current = new Sums[columns];
        double total = 0;
        long windows = 0;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++) current[column] = Sums.Of(a, b, column * 4, row * 4, 4, 4, width);
            if (row > 0)
            {
                for (int column = 0; column + 1 < columns; column++)
                {
                    Sums window = previous[column] + previous[column + 1] + current[column] + current[column + 1];
                    total += Window(window, 64);
                    windows++;
                }
            }

            (previous, current) = (current, previous);
        }

        return total / windows;
    }

    private static double Window(Sums s, int count)
    {
        double meanA = (double)s.A / count, meanB = (double)s.B / count;
        double varianceA = ((double)s.AA / count) - (meanA * meanA);
        double varianceB = ((double)s.BB / count) - (meanB * meanB);
        double covariance = ((double)s.AB / count) - (meanA * meanB);
        return ((2 * meanA * meanB) + C1) * ((2 * covariance) + C2)
            / (((meanA * meanA) + (meanB * meanB) + C1) * (varianceA + varianceB + C2));
    }

    private readonly record struct Sums(long A, long B, long AA, long BB, long AB)
    {
        public static Sums operator +(Sums x, Sums y) => new(x.A + y.A, x.B + y.B, x.AA + y.AA, x.BB + y.BB, x.AB + y.AB);

        public static Sums Of(byte[] a, byte[] b, int x0, int y0, int w, int h, int stride)
        {
            long sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
            for (int y = y0; y < y0 + h; y++)
            {
                int o = y * stride;
                for (int x = x0; x < x0 + w; x++)
                {
                    int va = a[o + x], vb = b[o + x];
                    sa += va;
                    sb += vb;
                    saa += va * va;
                    sbb += vb * vb;
                    sab += va * vb;
                }
            }

            return new Sums(sa, sb, saa, sbb, sab);
        }

        public static Sums Add(Sums x, Sums y) => x + y;
    }
}
