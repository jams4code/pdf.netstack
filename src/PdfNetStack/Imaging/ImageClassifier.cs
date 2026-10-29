namespace PdfNetStack.Imaging;

/// <summary>
/// Tells continuous tone images, where JPEG wins, from line art, screenshots, charts and other
/// synthetic graphics, where JPEG rings around edges and lossless compression is small anyway.
/// Synthetic images reuse a few colors, cover large areas with one of them, and repeat a pixel
/// exactly next to itself; photographs and scans do none of that, if only because of noise.
/// </summary>
internal static class ImageClassifier
{
    /// <summary>Pixels examined at most; larger images are sampled on a regular grid of rows.</summary>
    private const int SampleBudget = 1 << 18;

    /// <summary>Distinct colors counted at most; more than enough to rule out a palette image.</summary>
    private const int ColorCap = 4096;

    /// <summary>True when the image looks photographic, so JPEG suits it.</summary>
    public static bool IsContinuousTone(Raster raster)
    {
        int width = raster.Width, height = raster.Height, n = raster.Channels;
        if (width < 16 || height < 16) return false;

        // Whole rows keep neighbor pairs intact; the row step spreads the sample over the image.
        int rowStep = Math.Max(1, (int)((long)width * height / SampleBudget));
        var colors = new Dictionary<int, int>();
        long pixels = 0, pairs = 0, equalPairs = 0;
        byte[] s = raster.Samples;
        for (int y = 0; y < height; y += rowStep)
        {
            int row = y * width * n;
            int previous = -1;
            for (int x = 0; x < width; x++)
            {
                int key = Key(s, row + (x * n), n);
                if (previous >= 0)
                {
                    pairs++;
                    if (key == previous) equalPairs++;
                }

                previous = key;
                pixels++;
                if (colors.TryGetValue(key, out int count)) colors[key] = count + 1;
                else if (colors.Count < ColorCap) colors[key] = 1;
            }
        }

        // A palette, bilevel or posterized image. Gray has only 256 levels to begin with.
        if (colors.Count <= (n == 1 ? 32 : 256)) return false;

        // Flat runs: backgrounds, fills and text strokes of a screenshot or a chart.
        if (equalPairs > pairs * 0.4) return false;

        // A few colors dominating the area, as a background and its text do.
        long dominant = 0;
        foreach (int count in colors.Values.OrderByDescending(c => c).Take(8)) dominant += count;
        return dominant <= pixels * 0.5;
    }

    /// <summary>The pixel's samples packed into one key; four channels keep the top six bits of each.</summary>
    private static int Key(byte[] s, int o, int n) => n switch
    {
        1 => s[o],
        3 => (s[o] << 16) | (s[o + 1] << 8) | s[o + 2],
        _ => ((s[o] >> 2) << 18) | ((s[o + 1] >> 2) << 12) | ((s[o + 2] >> 2) << 6) | (s[o + 3] >> 2),
    };
}
