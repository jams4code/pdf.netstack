namespace PdfNetStack.Imaging;

/// <summary>
/// Turns 8 bit samples into a baseline JPEG coefficient image (ITU T.81): JFIF YCbCr conversion
/// for color, optional 2:1 chroma subsampling, the forward DCT of A.3.3 and quantization with the
/// libjpeg tables for a quality. <see cref="JpegWriter"/> then writes it with Huffman tables built
/// for the image. Four channel samples are coded as stored, without a color transform, under an
/// Adobe marker that says so; their meaning, inverted or not, carries over unchanged.
/// </summary>
internal static class JpegEncoder
{
    // libjpeg's RGB to YCbCr constants (jccolor.c), 16 bit fixed point.
    private const int ScaleBits = 16;
    private const int Half = 1 << (ScaleBits - 1);

    private static readonly double[] Cosines = BuildCosines();

    /// <summary>Encodes <paramref name="raster"/> at <paramref name="quality"/>.</summary>
    /// <param name="raster">Gray, RGB or four channel samples.</param>
    /// <param name="quality">libjpeg quality, 1 to 100.</param>
    /// <param name="subsampleChroma">For RGB: code Cb and Cr at half resolution both ways (4:2:0).</param>
    /// <returns>A coefficient image; the caller disposes it.</returns>
    public static JpegImage Encode(Raster raster, int quality, bool subsampleChroma)
    {
        int n = raster.Channels;
        if (n is not (1 or 3 or 4)) throw new ArgumentException("Only 1, 3 or 4 channels can be coded.", nameof(raster));
        ushort[] luminance = JpegQuantization.ScaledTable(quality, chrominance: false);
        ushort[] chrominance = JpegQuantization.ScaledTable(quality, chrominance: true);

        int width = raster.Width, height = raster.Height;
        int hmax = n == 3 && subsampleChroma ? 2 : 1, vmax = hmax;
        int mcusX = (width + (8 * hmax) - 1) / (8 * hmax), mcusY = (height + (8 * vmax) - 1) / (8 * vmax);
        byte[][] planes = n == 3 ? ToYcc(raster) : Split(raster);

        var components = new JpegComponent[n];
        try
        {
            for (int c = 0; c < n; c++)
            {
                bool chroma = n == 3 && c > 0;
                int h = chroma ? 1 : hmax, v = chroma ? 1 : vmax;
                int samplesX = ((width * h) + hmax - 1) / hmax, samplesY = ((height * v) + vmax - 1) / vmax;
                int id = n == 4 ? "CMYK"[c] : c + 1;
                var component = new JpegComponent(id, h, v, chroma ? 1 : 0, (samplesX + 7) / 8, (samplesY + 7) / 8, mcusX * h, mcusY * v)
                {
                    Quantization = chroma ? chrominance : luminance,
                    Started = true,
                };
                components[c] = component;
                byte[] plane = h == hmax && v == vmax ? planes[c] : Downsample(planes[c], width, height, hmax / h, vmax / v);
                Transform(component, plane, samplesX, samplesY);
            }
        }
        catch
        {
            foreach (JpegComponent? c in components) c?.Release();
            throw;
        }

        var image = new JpegImage(JpegCoding.Baseline, width, height, components);
        if (n == 4)
        {
            // Adobe APP14: version 100, no flags, transform 0 (none).
            image.Segments.Add(new JpegSegment(0xEE, [(byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, 0]));
            image.AdobeTransform = 0;
        }
        else
        {
            // JFIF APP0, version 1.01, square pixels, no thumbnail.
            image.Segments.Add(new JpegSegment(0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]));
            image.HasJfif = true;
        }

        return image;
    }

    private static byte[][] Split(Raster raster)
    {
        int n = raster.Channels, pixels = raster.Width * raster.Height;
        if (n == 1) return [raster.Samples];
        var planes = new byte[n][];
        for (int c = 0; c < n; c++)
        {
            planes[c] = new byte[pixels];
            for (int i = 0; i < pixels; i++) planes[c][i] = raster.Samples[(i * n) + c];
        }

        return planes;
    }

    /// <summary>RGB to JFIF YCbCr with libjpeg's fixed point arithmetic.</summary>
    private static byte[][] ToYcc(Raster raster)
    {
        int pixels = raster.Width * raster.Height;
        byte[] s = raster.Samples;
        var y = new byte[pixels];
        var cb = new byte[pixels];
        var cr = new byte[pixels];
        int yr = Fix(0.29900), yg = Fix(0.58700), yb = Fix(0.11400);
        int cbr = Fix(0.16874), cbg = Fix(0.33126), half = Fix(0.5);
        int crg = Fix(0.41869), crb = Fix(0.08131);
        const int center = (128 << ScaleBits) + Half - 1;
        for (int i = 0, o = 0; i < pixels; i++, o += 3)
        {
            int r = s[o], g = s[o + 1], b = s[o + 2];
            y[i] = (byte)(((yr * r) + (yg * g) + (yb * b) + Half) >> ScaleBits);
            cb[i] = (byte)((-(cbr * r) - (cbg * g) + (half * b) + center) >> ScaleBits);
            cr[i] = (byte)(((half * r) - (crg * g) - (crb * b) + center) >> ScaleBits);
        }

        return [y, cb, cr];
    }

    private static int Fix(double x) => (int)((x * (1 << ScaleBits)) + 0.5);

    /// <summary>Box averages of <paramref name="fx"/> by <paramref name="fy"/> pixels, edges repeated; the rounding bias alternates as in libjpeg.</summary>
    private static byte[] Downsample(byte[] plane, int width, int height, int fx, int fy)
    {
        int w = (width + fx - 1) / fx, h = (height + fy - 1) / fy, area = fx * fy;
        var output = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < fy; dy++)
                {
                    int row = Math.Min((y * fy) + dy, height - 1) * width;
                    for (int dx = 0; dx < fx; dx++) sum += plane[row + Math.Min((x * fx) + dx, width - 1)];
                }

                int bias = (area / 2) - 1 + (x & 1);
                output[(y * w) + x] = (byte)((sum + bias) / area);
            }
        }

        return output;
    }

    /// <summary>Forward DCT and quantization of every allocated block; samples past the edge repeat the last row and column.</summary>
    private static void Transform(JpegComponent component, byte[] plane, int width, int height)
    {
        ushort[] q = component.Quantization;
        ReadOnlySpan<byte> natural = JpegQuantization.ZigZagToNatural;
        Span<double> block = stackalloc double[64];
        Span<double> rows = stackalloc double[64];
        for (int by = 0; by < component.AllocatedPerColumn; by++)
        {
            for (int bx = 0; bx < component.AllocatedPerLine; bx++)
            {
                for (int y = 0; y < 8; y++)
                {
                    int row = Math.Min((by * 8) + y, height - 1) * width;
                    for (int x = 0; x < 8; x++) block[(y * 8) + x] = plane[row + Math.Min((bx * 8) + x, width - 1)] - 128.0;
                }

                ForwardDct(block, rows);
                Span<short> coefficients = component.Block(by, bx);
                for (int k = 0; k < 64; k++)
                {
                    double value = Math.Round(block[natural[k]] / q[k], MidpointRounding.AwayFromZero);
                    int limit = k == 0 ? 2047 : 1023;
                    coefficients[k] = (short)Math.Max(-limit, Math.Min(limit, value));
                }
            }
        }
    }

    /// <summary>The 2D forward DCT of ITU T.81 A.3.3, separably, in place in <paramref name="block"/>.</summary>
    private static void ForwardDct(Span<double> block, Span<double> scratch)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int x = 0; x < 8; x++) sum += Cosines[(u * 8) + x] * block[(y * 8) + x];
                scratch[(y * 8) + u] = sum;
            }
        }

        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++) sum += Cosines[(v * 8) + y] * scratch[(y * 8) + u];
                block[(v * 8) + u] = sum;
            }
        }
    }

    /// <summary>C(u) cos((2x + 1) u pi / 16) / 2, indexed [u * 8 + x].</summary>
    private static double[] BuildCosines()
    {
        var table = new double[64];
        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < 8; x++)
            {
                double c = u == 0 ? 1 / Math.Sqrt(2) : 1;
                table[(u * 8) + x] = c * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) / 2;
            }
        }

        return table;
    }
}
