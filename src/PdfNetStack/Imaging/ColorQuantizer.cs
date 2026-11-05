namespace PdfNetStack.Imaging;

/// <summary>An image reduced to a palette: one index per pixel, and RGB triplets sorted by luminance.</summary>
internal sealed record QuantizedImage(byte[] Indices, byte[] Palette)
{
    /// <summary>Colors the palette holds.</summary>
    public int Colors => Palette.Length / 3;

    /// <summary>The smallest depth an Indexed color space allows for the palette: 1, 2, 4 or 8 bits.</summary>
    public int Bits => Colors switch
    {
        <= 2 => 1,
        <= 4 => 2,
        <= 16 => 4,
        _ => 8,
    };

    /// <summary>The image as RGB samples, as a reader would expand it.</summary>
    public Raster Expand(int width, int height)
    {
        var samples = new byte[Indices.Length * 3];
        for (int i = 0, o = 0; i < Indices.Length; i++, o += 3)
        {
            int p = Indices[i] * 3;
            samples[o] = Palette[p];
            samples[o + 1] = Palette[p + 1];
            samples[o + 2] = Palette[p + 2];
        }

        return new Raster(width, height, 3, samples);
    }

    /// <summary>The indices as rows of <see cref="Bits"/> bit samples, each row padded to a byte.</summary>
    public byte[] PackedRows(int width, int height)
    {
        int bits = Bits;
        if (bits == 8) return Indices;
        int rowBytes = ((width * bits) + 7) / 8;
        var packed = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes, source = y * width;
            for (int x = 0; x < width; x++)
            {
                int bit = x * bits;
                packed[row + (bit >> 3)] |= (byte)(Indices[source + x] << (8 - bits - (bit & 7)));
            }
        }

        return packed;
    }
}

/// <summary>
/// Reduces 8 bit RGB images to a palette for an Indexed color space. A variance driven median cut
/// finds the clusters and k-means refines them, both on the image's distinct colors weighted by how
/// many pixels use them. A color that holds most of its cluster stays exact, so a white page stays
/// white and flat fills stay flat, which is what line art and screenshots need and what Flate
/// rewards. Pixels are never dithered: dither is noise to Flate, and to a reader of fine labels.
/// </summary>
internal sealed class ColorQuantizer
{
    /// <summary>Distinct colors kept exactly; beyond this colors are rounded to 6 bits per channel.</summary>
    private const int MaxExactColors = 1 << 16;

    private const int RefinementRounds = 3;

    private readonly Raster _raster;
    private readonly int[] _colors;
    private readonly int[] _weights;
    private readonly bool _rounded;

    private ColorQuantizer(Raster raster, int[] colors, int[] weights, bool rounded)
    {
        _raster = raster;
        _colors = colors;
        _weights = weights;
        _rounded = rounded;
    }

    /// <summary>Counts the colors of a 3 channel raster, once for all the palette sizes tried on it.</summary>
    public static ColorQuantizer For(Raster raster)
    {
        if (raster.Channels != 3) throw new ArgumentException("Only 3 channel images have a palette here.", nameof(raster));
        byte[] s = raster.Samples;
        int pixels = raster.Width * raster.Height;
        var counts = new Dictionary<int, int>();
        bool rounded = false;
        for (int i = 0, o = 0; i < pixels; i++, o += 3)
        {
            int key = (s[o] << 16) | (s[o + 1] << 8) | s[o + 2];
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
            if (counts.Count > MaxExactColors)
            {
                rounded = true;
                break;
            }
        }

        if (rounded)
        {
            counts.Clear();
            for (int i = 0, o = 0; i < pixels; i++, o += 3)
            {
                int key = Round(s[o], s[o + 1], s[o + 2]);
                counts.TryGetValue(key, out int count);
                counts[key] = count + 1;
            }
        }

        var colors = new int[counts.Count];
        var weights = new int[counts.Count];
        int n = 0;
        foreach (KeyValuePair<int, int> pair in counts)
        {
            colors[n] = pair.Key;
            weights[n++] = pair.Value;
        }

        // Dictionary order is not specified, and the output must not depend on it.
        Array.Sort(colors, weights);
        return new ColorQuantizer(raster, colors, weights, rounded);
    }

    /// <summary>True when a palette of <paramref name="maxColors"/> colors holds every pixel exactly.</summary>
    public bool IsExact(int maxColors) => !_rounded && _colors.Length <= maxColors;

    /// <summary>The image with at most <paramref name="maxColors"/> colors, exact when it already has no more.</summary>
    public QuantizedImage Reduce(int maxColors)
    {
        if (maxColors is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(maxColors));
        int[] palette = IsExact(maxColors) ? _colors : Clusters(maxColors);
        palette = palette.Distinct().OrderBy(Luminance).ThenBy(c => c).ToArray();

        var triplets = new byte[palette.Length * 3];
        for (int i = 0; i < palette.Length; i++)
        {
            triplets[i * 3] = (byte)(palette[i] >> 16);
            triplets[(i * 3) + 1] = (byte)(palette[i] >> 8);
            triplets[(i * 3) + 2] = (byte)palette[i];
        }

        byte[] s = _raster.Samples;
        var indices = new byte[_raster.Width * _raster.Height];
        var nearest = new Dictionary<int, byte>();
        for (int i = 0, o = 0; i < indices.Length; i++, o += 3)
        {
            int key = (s[o] << 16) | (s[o + 1] << 8) | s[o + 2];
            int lookup = _rounded ? Round(s[o], s[o + 1], s[o + 2]) : key;
            if (!nearest.TryGetValue(lookup, out byte index))
            {
                index = (byte)Nearest(lookup, palette);
                nearest[lookup] = index;
            }

            indices[i] = index;
        }

        return new QuantizedImage(indices, triplets);
    }

    private int[] Clusters(int maxColors)
    {
        // Points are numbered through this array, which the cut reorders in place box by box.
        int[] order = new int[_colors.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;

        var boxes = new List<Box> { Box.Measure(order, 0, order.Length, _colors, _weights) };
        while (boxes.Count < maxColors)
        {
            int widest = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count > 1 && boxes[i].Error > 0 && (widest < 0 || boxes[i].Error > boxes[widest].Error)) widest = i;
            }

            if (widest < 0) break;
            Box box = boxes[widest];
            int split = box.Split(order, _colors, _weights);
            boxes[widest] = Box.Measure(order, box.Start, split - box.Start, _colors, _weights);
            boxes.Add(Box.Measure(order, split, box.Start + box.Count - split, _colors, _weights));
        }

        var centers = new double[boxes.Count * 3];
        for (int i = 0; i < boxes.Count; i++)
        {
            centers[i * 3] = boxes[i].MeanR;
            centers[(i * 3) + 1] = boxes[i].MeanG;
            centers[(i * 3) + 2] = boxes[i].MeanB;
        }

        return Refine(centers, boxes.Count);
    }

    /// <summary>
    /// Lloyd rounds from the median cut's means, then the palette: each cluster's mean, or the
    /// cluster's most used color when that color holds at least half of the cluster's pixels.
    /// </summary>
    private int[] Refine(double[] centers, int k)
    {
        var assignment = new int[_colors.Length];
        var sums = new double[k * 3];
        var totals = new long[k];
        for (int round = 0; round <= RefinementRounds; round++)
        {
            Array.Clear(sums, 0, sums.Length);
            Array.Clear(totals, 0, totals.Length);
            for (int i = 0; i < _colors.Length; i++)
            {
                int c = _colors[i];
                int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                int best = 0;
                double bestDistance = double.MaxValue;
                for (int j = 0; j < k; j++)
                {
                    double dr = r - centers[j * 3], dg = g - centers[(j * 3) + 1], db = b - centers[(j * 3) + 2];
                    double distance = (2 * dr * dr) + (4 * dg * dg) + (db * db);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = j;
                    }
                }

                assignment[i] = best;
                long w = _weights[i];
                sums[best * 3] += r * (double)w;
                sums[(best * 3) + 1] += g * (double)w;
                sums[(best * 3) + 2] += b * (double)w;
                totals[best] += w;
            }

            for (int j = 0; j < k; j++)
            {
                if (totals[j] == 0) continue;
                centers[j * 3] = sums[j * 3] / totals[j];
                centers[(j * 3) + 1] = sums[(j * 3) + 1] / totals[j];
                centers[(j * 3) + 2] = sums[(j * 3) + 2] / totals[j];
            }
        }

        var heaviest = new int[k];
        for (int j = 0; j < k; j++) heaviest[j] = -1;
        for (int i = 0; i < _colors.Length; i++)
        {
            int j = assignment[i];
            if (heaviest[j] < 0 || _weights[i] > _weights[heaviest[j]]) heaviest[j] = i;
        }

        var palette = new List<int>(k);
        for (int j = 0; j < k; j++)
        {
            if (totals[j] == 0) continue;
            if (_weights[heaviest[j]] * 2L >= totals[j])
            {
                palette.Add(_colors[heaviest[j]]);
                continue;
            }

            palette.Add((Clamp(centers[j * 3]) << 16) | (Clamp(centers[(j * 3) + 1]) << 8) | Clamp(centers[(j * 3) + 2]));
        }

        return palette.ToArray();
    }

    private static int Nearest(int color, int[] palette)
    {
        int r = (color >> 16) & 255, g = (color >> 8) & 255, b = color & 255;
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            int p = palette[i];
            int dr = r - ((p >> 16) & 255), dg = g - ((p >> 8) & 255), db = b - (p & 255);
            int distance = (2 * dr * dr) + (4 * dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>The middle of the color's 6 bit cell, so rounding has no bias.</summary>
    private static int Round(int r, int g, int b) => ((r & 0xFC) << 16) | ((g & 0xFC) << 8) | (b & 0xFC) | 0x020202;

    private static int Luminance(int c) => (77 * ((c >> 16) & 255)) + (150 * ((c >> 8) & 255)) + (29 * (c & 255));

    private static int Clamp(double v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)Math.Round(v);

    /// <summary>A run of points of the median cut, with its weighted means and squared error.</summary>
    private readonly record struct Box(int Start, int Count, double MeanR, double MeanG, double MeanB, double VarR, double VarG, double VarB, long Weight)
    {
        public double Error => VarR + VarG + VarB;

        public static Box Measure(int[] order, int start, int count, int[] colors, int[] weights)
        {
            double sr = 0, sg = 0, sb = 0, qr = 0, qg = 0, qb = 0;
            long total = 0;
            for (int i = start; i < start + count; i++)
            {
                int c = colors[order[i]];
                double w = weights[order[i]];
                int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                sr += r * w;
                sg += g * w;
                sb += b * w;
                qr += r * (double)r * w;
                qg += g * (double)g * w;
                qb += b * (double)b * w;
                total += weights[order[i]];
            }

            // Sums of squared error per channel, weighted as the distance is: green counts most.
            double t = total;
            return new Box(start, count, sr / t, sg / t, sb / t, 2 * (qr - (sr * sr / t)), 4 * (qg - (sg * sg / t)), qb - (sb * sb / t), total);
        }

        /// <summary>Sorts the box along its widest channel and returns where the weighted median falls.</summary>
        public int Split(int[] order, int[] colors, int[] weights)
        {
            int shift = VarG >= VarR && VarG >= VarB ? 8 : VarR >= VarB ? 16 : 0;
            Array.Sort(order, Start, Count, Comparer<int>.Create((x, y) =>
            {
                int a = (colors[x] >> shift) & 255, b = (colors[y] >> shift) & 255;
                return a != b ? a.CompareTo(b) : colors[x].CompareTo(colors[y]);
            }));

            long half = Weight / 2, running = 0;
            int end = Start + Count;
            for (int i = Start; i < end - 1; i++)
            {
                running += weights[order[i]];
                if (running >= half) return i + 1;
            }

            return end - 1;
        }
    }
}
