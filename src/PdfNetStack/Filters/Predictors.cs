namespace PdfNetStack.Filters;

/// <summary>Parameters of the PNG and TIFF predictors attached to Flate or LZW.</summary>
internal readonly record struct PredictorParams(int Predictor, int Colors, int BitsPerComponent, int Columns)
{
    public int BytesPerPixel => Math.Max(1, (Colors * BitsPerComponent + 7) / 8);

    public int RowBytes => (int)(((long)Colors * BitsPerComponent * Columns + 7) / 8);

    public bool IsValid =>
        Predictor is 1 or 2 or >= 10 and <= 15 &&
        Colors is >= 1 and <= 32 &&
        BitsPerComponent is 1 or 2 or 4 or 8 or 16 &&
        Columns is >= 1 and <= 1 << 24;
}

internal static class Predictors
{
    /// <summary>
    /// Reverses a PNG (10-15) or TIFF (2) predictor into a pooled buffer owned by the caller.
    /// Returns false when the parameters are unsupported.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, PredictorParams p, long limit, out BoundedBuffer? result)
    {
        result = null;
        if (!p.IsValid || p.Predictor == 1) return false;
        if (p.Predictor == 2) return TryDecodeTiff(data, p, limit, out result);
        return TryDecodePng(data, p, limit, out result);
    }

    private static bool TryDecodePng(ReadOnlySpan<byte> data, PredictorParams p, long limit, out BoundedBuffer? result)
    {
        result = null;

        // Row size comes from untrusted parameters. Data shorter than one row is a single
        // partial row, and only its first bytes can ever be output, so the row is clamped.
        int rowBytes = Math.Min(p.RowBytes, Math.Max(1, data.Length));
        int bpp = p.BytesPerPixel;
        int rows = data.Length / (rowBytes + 1);
        int tail = data.Length - rows * (rowBytes + 1);
        long outLength = (long)rows * rowBytes + Math.Max(0, tail - 1);
        if (outLength > limit) throw new StreamDecodeException("Decoded stream exceeds the configured size limit.");

        var output = new BoundedBuffer((int)outLength, limit);
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        int src = 0;
        while (src < data.Length)
        {
            int filter = data[src++];
            int available = Math.Min(rowBytes, data.Length - src);
            Array.Clear(current);
            data.Slice(src, available).CopyTo(current);
            src += available;

            switch (filter)
            {
                case 0:
                    break;
                case 1:
                    for (int i = bpp; i < rowBytes; i++) current[i] += current[i - bpp];
                    break;
                case 2:
                    for (int i = 0; i < rowBytes; i++) current[i] += previous[i];
                    break;
                case 3:
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int left = i >= bpp ? current[i - bpp] : 0;
                        current[i] += (byte)((left + previous[i]) >> 1);
                    }

                    break;
                case 4:
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int a = i >= bpp ? current[i - bpp] : 0;
                        int b = previous[i];
                        int c = i >= bpp ? previous[i - bpp] : 0;
                        current[i] += Paeth(a, b, c);
                    }

                    break;
                default:
                    output.Dispose();
                    return false;
            }

            output.Write(current.AsSpan(0, available));
            (previous, current) = (current, previous);
        }

        result = output;
        return true;
    }

    private static bool TryDecodeTiff(ReadOnlySpan<byte> data, PredictorParams p, long limit, out BoundedBuffer? result)
    {
        result = null;
        if (p.BitsPerComponent != 8 && p.BitsPerComponent != 16) return false;
        var output = new BoundedBuffer(data.Length, limit);
        output.Write(data);
        Span<byte> r = output.WritableSpan;
        int rowBytes = p.RowBytes;
        int colors = p.Colors;
        for (int rowStart = 0; rowStart + rowBytes <= r.Length; rowStart += rowBytes)
        {
            if (p.BitsPerComponent == 8)
            {
                for (int i = colors; i < rowBytes; i++) r[rowStart + i] += r[rowStart + i - colors];
            }
            else
            {
                int stride = colors * 2;
                for (int i = stride; i + 1 < rowBytes; i += 2)
                {
                    int prev = (r[rowStart + i - stride] << 8) | r[rowStart + i - stride + 1];
                    int cur = (r[rowStart + i] << 8) | r[rowStart + i + 1];
                    int sum = (prev + cur) & 0xFFFF;
                    r[rowStart + i] = (byte)(sum >> 8);
                    r[rowStart + i + 1] = (byte)sum;
                }
            }
        }

        result = output;
        return true;
    }

    /// <summary>
    /// Applies PNG prediction choosing the filter per row with the minimum sum of absolute
    /// differences heuristic. Input length must be an exact multiple of the row size.
    /// </summary>
    public static BoundedBuffer EncodePng(ReadOnlySpan<byte> data, PredictorParams p)
    {
        int rowBytes = p.RowBytes;
        int bpp = p.BytesPerPixel;
        int rows = data.Length / rowBytes;
        var lease = new BoundedBuffer(rows * (rowBytes + 1), long.MaxValue);
        Span<byte> output = lease.Extend(rows * (rowBytes + 1));
        var candidate = new byte[5][];
        for (int f = 0; f < 5; f++) candidate[f] = new byte[rowBytes];
        ReadOnlySpan<byte> previous = new byte[rowBytes];

        for (int r = 0; r < rows; r++)
        {
            ReadOnlySpan<byte> row = data.Slice(r * rowBytes, rowBytes);
            Span<byte> none = candidate[0], sub = candidate[1], up = candidate[2], avg = candidate[3], paeth = candidate[4];

            row.CopyTo(none);
            for (int i = 0; i < bpp && i < rowBytes; i++)
            {
                byte b = previous[i];
                sub[i] = row[i];
                up[i] = (byte)(row[i] - b);
                avg[i] = (byte)(row[i] - (b >> 1));
                paeth[i] = (byte)(row[i] - b);
            }

            for (int i = bpp; i < rowBytes; i++)
            {
                byte x = row[i];
                byte a = row[i - bpp];
                byte b = previous[i];
                byte c = previous[i - bpp];
                sub[i] = (byte)(x - a);
                up[i] = (byte)(x - b);
                avg[i] = (byte)(x - ((a + b) >> 1));
                paeth[i] = (byte)(x - Paeth(a, b, c));
            }

            int best = 0;
            long bestScore = Score(none);
            for (int f = 1; f < 5; f++)
            {
                long score = Score(candidate[f]);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = f;
                }
            }

            int o = r * (rowBytes + 1);
            output[o] = (byte)best;
            candidate[best].CopyTo(output[(o + 1)..]);
            previous = row;
        }

        return lease;
    }

    // Sum of absolute values when bytes are read as signed, the heuristic libpng uses.
    private static long Score(ReadOnlySpan<byte> row)
    {
        long score = 0;
        foreach (byte v in row) score += v < 128 ? v : 256 - v;
        return score;
    }

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return (byte)a;
        return pb <= pc ? (byte)b : (byte)c;
    }
}
