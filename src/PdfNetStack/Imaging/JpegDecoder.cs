namespace PdfNetStack.Imaging;

/// <summary>
/// Decodes a JPEG to 8 bit samples the way libjpeg does by default: dequantization, inverse DCT,
/// fancy (triangle) upsampling of 2:1 chroma and its fixed point YCbCr conversion. Results match
/// libjpeg within a step or two per sample; libjpeg's integer DCT rounds differently.
/// </summary>
/// <remarks>
/// Output is gray, RGB or CMYK. YCCK becomes CMYK as libjpeg converts it, and Adobe CMYK stays
/// inverted as stored: in a PDF the image's /Decode array says how to read it, so samples are
/// handed over as they are rather than guessed at.
/// </remarks>
internal static class JpegDecoder
{
    // libjpeg's color conversion constants (jdcolor.c): 16 bit fixed point.
    private const int ScaleBits = 16;
    private const int Half = 1 << (ScaleBits - 1);

    private static readonly double[] Cosines = BuildCosines();

    /// <param name="data">The JPEG.</param>
    /// <param name="colorTransform">The DCTDecode /ColorTransform, or null outside a PDF.</param>
    /// <param name="byteLimit">Ceiling for coefficient and sample buffers.</param>
    /// <exception cref="JpegFormatException">The data is damaged, too large or uses an unsupported coding process.</exception>
    public static Raster Decode(ReadOnlySpan<byte> data, int? colorTransform, long byteLimit)
    {
        using JpegImage image = JpegReader.Read(data, byteLimit);
        int width = image.Width, height = image.Height, n = image.Components.Length;
        if (n is not (1 or 3 or 4)) throw new JpegFormatException($"JPEG with {n} components is not supported.");
        long samples = (long)width * height * n;
        if (samples > byteLimit || samples > Compat.MaxArrayLength) throw new JpegFormatException("JPEG exceeds the decode limit.");

        var planes = new byte[n][];
        for (int c = 0; c < n; c++) planes[c] = Upsample(image, image.Components[c], Samples(image.Components[c]));

        var output = new byte[samples];
        bool[] chroma = image.ChromaComponents(colorTransform);
        if (n >= 3 && chroma[1] && chroma[2]) ConvertYcc(planes, output, width * height, n);
        else Interleave(planes, output, width * height, n);
        return new Raster(width, height, n, output);
    }

    /// <summary>The component's samples at its own resolution, BlocksPerLine * 8 wide.</summary>
    private static byte[] Samples(JpegComponent component)
    {
        int stride = component.BlocksPerLine * 8;
        var plane = new byte[stride * component.BlocksPerColumn * 8];
        Span<double> block = stackalloc double[64];
        Span<double> rows = stackalloc double[64];
        ReadOnlySpan<byte> natural = JpegQuantization.ZigZagToNatural;
        ushort[] q = component.Quantization;
        for (int by = 0; by < component.BlocksPerColumn; by++)
        {
            for (int bx = 0; bx < component.BlocksPerLine; bx++)
            {
                Span<short> coefficients = component.Block(by, bx);
                block.Clear();
                bool acZero = true;
                for (int k = 0; k < 64; k++)
                {
                    if (coefficients[k] == 0) continue;
                    block[natural[k]] = coefficients[k] * q[k];
                    if (k > 0) acZero = false;
                }

                int origin = (by * 8 * stride) + (bx * 8);
                if (acZero)
                {
                    // A flat block: the DC term alone, scaled by 1/8 (C(0)^2 / 4).
                    byte flat = Clamp((block[0] / 8) + 128);
                    for (int y = 0; y < 8; y++) plane.AsSpan(origin + (y * stride), 8).Fill(flat);
                    continue;
                }

                InverseDct(block, rows);
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++) plane[origin + (y * stride) + x] = Clamp(block[(y * 8) + x] + 128);
                }
            }
        }

        return plane;
    }

    /// <summary>
    /// The 2D inverse DCT of ITU T.81 A.3.3, separably: rows, then columns. In place in
    /// <paramref name="block"/>; rows of zeros are skipped, as most are after quantization.
    /// </summary>
    private static void InverseDct(Span<double> block, Span<double> scratch)
    {
        for (int v = 0; v < 8; v++)
        {
            Span<double> row = block.Slice(v * 8, 8);
            Span<double> result = scratch.Slice(v * 8, 8);
            bool zero = true;
            for (int u = 0; u < 8; u++) zero &= row[u] == 0;
            if (zero)
            {
                result.Clear();
                continue;
            }

            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int u = 0; u < 8; u++) sum += Cosines[(x * 8) + u] * row[u];
                result[x] = sum;
            }
        }

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++) sum += Cosines[(y * 8) + v] * scratch[(v * 8) + x];
                block[(y * 8) + x] = sum;
            }
        }
    }

    /// <summary>C(u) cos((2x + 1) u pi / 16) / 2, indexed [x * 8 + u].</summary>
    private static double[] BuildCosines()
    {
        var table = new double[64];
        for (int x = 0; x < 8; x++)
        {
            for (int u = 0; u < 8; u++)
            {
                double c = u == 0 ? 1 / Math.Sqrt(2) : 1;
                table[(x * 8) + u] = c * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) / 2;
            }
        }

        return table;
    }

    private static byte Clamp(double value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(int)(value + 0.5);

    /// <summary>
    /// Brings a component to full resolution, width by height. 2:1 ratios use libjpeg's fancy
    /// upsampling (jdsample.c): a triangle filter, 3/4 of the nearer sample and 1/4 of the farther,
    /// with edge samples repeated. Other integral ratios repeat samples.
    /// </summary>
    private static byte[] Upsample(JpegImage image, JpegComponent component, byte[] plane)
    {
        int width = image.Width, height = image.Height, stride = component.BlocksPerLine * 8;
        int hmax = image.MaxHorizontal, vmax = image.MaxVertical;
        if (hmax % component.Horizontal != 0 || vmax % component.Vertical != 0) throw new JpegFormatException("JPEG uses fractional sampling factors.");
        int hf = hmax / component.Horizontal, vf = vmax / component.Vertical;

        // The component's real size; samples past it are padding (ITU T.81 A.1.1).
        int cw = (int)(((long)width * component.Horizontal + hmax - 1) / hmax);
        int ch = (int)(((long)height * component.Vertical + vmax - 1) / vmax);
        var output = new byte[width * height];

        if (hf == 1 && vf == 1)
        {
            for (int y = 0; y < height; y++) plane.AsSpan(y * stride, width).CopyTo(output.AsSpan(y * width));
        }
        else if (hf == 2 && vf == 1 && cw > 2)
        {
            var row = new byte[2 * cw];
            for (int y = 0; y < height; y++)
            {
                FancyRow(plane.AsSpan(y * stride, cw), row);
                row.AsSpan(0, width).CopyTo(output.AsSpan(y * width));
            }
        }
        else if (hf == 1 && vf == 2)
        {
            for (int y = 0; y < height; y++)
            {
                int source = y / 2, neighbor = y % 2 == 0 ? Math.Max(source - 1, 0) : Math.Min(source + 1, ch - 1);
                int bias = y % 2 == 0 ? 1 : 2;
                for (int x = 0; x < width; x++)
                {
                    output[(y * width) + x] = (byte)(((plane[(source * stride) + x] * 3) + plane[(neighbor * stride) + x] + bias) >> 2);
                }
            }
        }
        else if (hf == 2 && vf == 2 && cw > 2)
        {
            var sums = new int[cw];
            for (int y = 0; y < height; y++)
            {
                int source = y / 2, neighbor = y % 2 == 0 ? Math.Max(source - 1, 0) : Math.Min(source + 1, ch - 1);
                for (int x = 0; x < cw; x++) sums[x] = (plane[(source * stride) + x] * 3) + plane[(neighbor * stride) + x];
                FancySums(sums, output.AsSpan(y * width, width));
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                int source = Math.Min(y / vf, ch - 1) * stride;
                for (int x = 0; x < width; x++) output[(y * width) + x] = plane[source + Math.Min(x / hf, cw - 1)];
            }
        }

        return output;
    }

    /// <summary>One row doubled horizontally; the rounding bias alternates so the filter has no drift.</summary>
    private static void FancyRow(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int n = input.Length;
        output[0] = input[0];
        output[1] = (byte)(((input[0] * 3) + input[1] + 2) >> 2);
        for (int i = 1; i < n - 1; i++)
        {
            int v = input[i] * 3;
            output[2 * i] = (byte)((v + input[i - 1] + 1) >> 2);
            output[(2 * i) + 1] = (byte)((v + input[i + 1] + 2) >> 2);
        }

        output[(2 * n) - 2] = (byte)(((input[n - 1] * 3) + input[n - 2] + 1) >> 2);
        output[(2 * n) - 1] = input[n - 1];
    }

    /// <summary>The horizontal half of 2x2 fancy upsampling, over column sums already weighted 3:1 vertically.</summary>
    private static void FancySums(int[] sums, Span<byte> output)
    {
        int n = sums.Length;
        Span<byte> full = n * 2 <= 4096 ? stackalloc byte[n * 2] : new byte[n * 2];
        full[0] = (byte)(((sums[0] * 4) + 8) >> 4);
        full[1] = (byte)(((sums[0] * 3) + sums[1] + 7) >> 4);
        for (int i = 1; i < n - 1; i++)
        {
            int v = sums[i] * 3;
            full[2 * i] = (byte)((v + sums[i - 1] + 8) >> 4);
            full[(2 * i) + 1] = (byte)((v + sums[i + 1] + 7) >> 4);
        }

        full[(2 * n) - 2] = (byte)(((sums[n - 1] * 3) + sums[n - 2] + 8) >> 4);
        full[(2 * n) - 1] = (byte)(((sums[n - 1] * 4) + 7) >> 4);
        full.Slice(0, output.Length).CopyTo(output);
    }

    private static void Interleave(byte[][] planes, byte[] output, int pixels, int n)
    {
        for (int i = 0; i < pixels; i++)
        {
            for (int c = 0; c < n; c++) output[(i * n) + c] = planes[c][i];
        }
    }

    /// <summary>
    /// YCbCr to RGB (JFIF), or YCCK to CMYK as libjpeg does it: the RGB of the YCbCr part,
    /// inverted, with K passed through.
    /// </summary>
    private static void ConvertYcc(byte[][] planes, byte[] output, int pixels, int n)
    {
        int[] crR = new int[256], cbB = new int[256], crG = new int[256], cbG = new int[256];
        for (int i = 0; i < 256; i++)
        {
            int x = i - 128;
            crR[i] = ((Fix(1.40200) * x) + Half) >> ScaleBits;
            cbB[i] = ((Fix(1.77200) * x) + Half) >> ScaleBits;
            crG[i] = -Fix(0.71414) * x;
            cbG[i] = (-Fix(0.34414) * x) + Half;
        }

        byte[] yp = planes[0], cbp = planes[1], crp = planes[2];
        for (int i = 0; i < pixels; i++)
        {
            int y = yp[i], cb = cbp[i], cr = crp[i];
            int r = Clamp(y + crR[cr]), g = Clamp(y + ((cbG[cb] + crG[cr]) >> ScaleBits)), b = Clamp(y + cbB[cb]);
            int o = i * n;
            if (n == 3)
            {
                output[o] = (byte)r;
                output[o + 1] = (byte)g;
                output[o + 2] = (byte)b;
            }
            else
            {
                output[o] = (byte)(255 - r);
                output[o + 1] = (byte)(255 - g);
                output[o + 2] = (byte)(255 - b);
                output[o + 3] = planes[3][i];
            }
        }
    }

    private static int Fix(double x) => (int)((x * (1 << ScaleBits)) + 0.5);

    private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
}
