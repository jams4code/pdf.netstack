namespace PdfNetStack.Imaging;

/// <summary>
/// Reduces an image with a Lanczos filter of three lobes, stretched by the reduction factor so
/// that it also low-pass filters what the smaller grid can no longer hold. Rows are filtered
/// first and kept in a small ring, so memory stays at a few rows of the output whatever the
/// input size. Edge samples are repeated. Every step is scalar double arithmetic in a fixed
/// order, so the output is repeatable; runtimes whose sine differs in the last bit may round a
/// sample differently.
/// </summary>
internal static class Resampler
{
    private const int Lobes = 3;

    /// <summary>Resamples to <paramref name="width"/> by <paramref name="height"/>, which must not exceed the source size.</summary>
    public static Raster Downsample(Raster source, int width, int height)
    {
        if (width < 1 || height < 1 || width > source.Width || height > source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The target size must be positive and no larger than the source.");
        }

        if (width == source.Width && height == source.Height) return source;

        int n = source.Channels;
        Kernel horizontal = Kernel.Build(source.Width, width);
        Kernel vertical = Kernel.Build(source.Height, height);

        // Filtered source rows, by source row index modulo the ring size.
        int ringSize = vertical.MaxTaps;
        var ring = new double[ringSize][];
        var ringRow = new int[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            ring[i] = new double[width * n];
            ringRow[i] = -1;
        }

        var output = new byte[(long)width * height * n];
        byte[] samples = source.Samples;
        int sourceStride = source.Width * n;
        var accumulator = new double[width * n];
        for (int y = 0; y < height; y++)
        {
            int first = vertical.First[y];
            int taps = vertical.Count[y];
            Array.Clear(accumulator, 0, accumulator.Length);
            for (int t = 0; t < taps; t++)
            {
                int row = first + t;
                int slot = row % ringSize;
                if (ringRow[slot] != row)
                {
                    FilterRow(samples, row * sourceStride, n, horizontal, ring[slot]);
                    ringRow[slot] = row;
                }

                double weight = vertical.Weights[(y * vertical.MaxTaps) + t];
                double[] filtered = ring[slot];
                for (int i = 0; i < accumulator.Length; i++) accumulator[i] += weight * filtered[i];
            }

            int o = y * width * n;
            for (int i = 0; i < accumulator.Length; i++) output[o + i] = ToByte(accumulator[i]);
        }

        return new Raster(width, height, n, output);
    }

    private static void FilterRow(byte[] samples, int offset, int n, Kernel kernel, double[] destination)
    {
        int width = kernel.First.Length;
        for (int x = 0; x < width; x++)
        {
            int first = kernel.First[x];
            int taps = kernel.Count[x];
            int w = x * kernel.MaxTaps;
            for (int c = 0; c < n; c++)
            {
                double sum = 0;
                for (int t = 0; t < taps; t++) sum += kernel.Weights[w + t] * samples[offset + ((first + t) * n) + c];
                destination[(x * n) + c] = sum;
            }
        }
    }

    private static byte ToByte(double value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(int)(value + 0.5);

    /// <summary>
    /// Weights for one axis. Output sample i covers source positions centered on (i + 0.5) * scale - 0.5;
    /// taps beyond the edge fold onto the edge sample, and each row of weights sums to one.
    /// </summary>
    private sealed class Kernel
    {
        private Kernel(int[] first, int[] count, double[] weights, int maxTaps)
        {
            First = first;
            Count = count;
            Weights = weights;
            MaxTaps = maxTaps;
        }

        public int[] First { get; }

        public int[] Count { get; }

        /// <summary>MaxTaps weights per output sample, the first Count of them used.</summary>
        public double[] Weights { get; }

        public int MaxTaps { get; }

        public static Kernel Build(int sourceSize, int targetSize)
        {
            double scale = (double)sourceSize / targetSize;
            double support = Lobes * scale;
            int maxTaps = (int)Math.Ceiling(2 * support) + 2;
            var first = new int[targetSize];
            var count = new int[targetSize];
            var weights = new double[targetSize * maxTaps];
            var raw = new double[maxTaps];
            for (int i = 0; i < targetSize; i++)
            {
                double center = ((i + 0.5) * scale) - 0.5;
                int lo = (int)Math.Floor(center - support) + 1;
                int hi = (int)Math.Floor(center + support);
                if (hi - lo + 1 > maxTaps) hi = lo + maxTaps - 1;

                // Taps outside the image repeat the edge sample, so their weight moves onto it.
                int start = Math.Max(lo, 0), end = Math.Min(hi, sourceSize - 1);
                Array.Clear(raw, 0, maxTaps);
                double total = 0;
                for (int j = lo; j <= hi; j++)
                {
                    double w = Lanczos((j - center) / scale);
                    raw[Math.Min(Math.Max(j, start), end) - start] += w;
                    total += w;
                }

                first[i] = start;
                count[i] = end - start + 1;
                for (int t = 0; t < count[i]; t++) weights[(i * maxTaps) + t] = total == 0 ? 1.0 / count[i] : raw[t] / total;
            }

            return new Kernel(first, count, weights, maxTaps);
        }

        private static double Lanczos(double x)
        {
            if (x == 0) return 1;
            if (x <= -Lobes || x >= Lobes) return 0;
            double px = Math.PI * x;
            return Lobes * Math.Sin(px) * Math.Sin(px / Lobes) / (px * px);
        }
    }
}
