namespace PdfNetStack.Imaging;

/// <summary>
/// Quantization tables and requantization. Tables and coefficient blocks are kept in zigzag
/// order throughout, the order in which JPEG stores them, so no reordering is ever needed.
/// </summary>
internal static class JpegQuantization
{
    /// <summary>Natural (row major) position of each zigzag index.</summary>
    public static ReadOnlySpan<byte> ZigZagToNatural =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // ITU T.81 Annex K, tables K.1 and K.2, natural order.
    private static ReadOnlySpan<byte> LuminanceBase =>
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static ReadOnlySpan<byte> ChrominanceBase =>
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    /// <summary>
    /// The Annex K table scaled to <paramref name="quality"/> the way libjpeg does it, so quality
    /// numbers mean what they mean in every other JPEG tool. Zigzag order, clamped to 1..255.
    /// </summary>
    public static ushort[] ScaledTable(int quality, bool chrominance)
    {
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        ReadOnlySpan<byte> source = chrominance ? ChrominanceBase : LuminanceBase;
        var table = new ushort[64];
        for (int k = 0; k < 64; k++)
        {
            int value = (source[ZigZagToNatural[k]] * scale + 50) / 100;
            table[k] = (ushort)Compat.Clamp(value, 1, 255);
        }

        return table;
    }

    /// <summary>
    /// Re-expresses a coefficient quantized with step <paramref name="from"/> in the coarser step
    /// <paramref name="to"/>, rounding half away from zero. Integer only, so every platform agrees.
    /// The verifier uses this same definition to prove a requantized image.
    /// </summary>
    public static int Requantize(int coefficient, int from, int to)
    {
        if (coefficient == 0 || from == to) return coefficient;
        int magnitude = ((coefficient < 0 ? -coefficient : coefficient) * from + to / 2) / to;
        return coefficient < 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// Coarsens every component's table to at least the scaled table for <paramref name="quality"/>
    /// and requantizes its coefficients. A table entry is never made finer than the source, so a
    /// quality above the image's own leaves it untouched.
    /// </summary>
    /// <param name="chroma">Per component: true when it carries color difference (Cb, Cr).</param>
    /// <returns>True when at least one coefficient table changed.</returns>
    public static bool Requantize(JpegImage image, int quality, ReadOnlySpan<bool> chroma)
    {
        ushort[] luminance = ScaledTable(quality, chrominance: false);
        ushort[] chrominance = ScaledTable(quality, chrominance: true);
        bool changed = false;
        for (int i = 0; i < image.Components.Length; i++)
        {
            JpegComponent component = image.Components[i];
            ushort[] target = chroma[i] ? chrominance : luminance;
            ushort[] source = component.Quantization;
            var coarser = new ushort[64];
            bool differs = false;
            for (int k = 0; k < 64; k++)
            {
                coarser[k] = Math.Max(source[k], target[k]);
                differs |= coarser[k] != source[k];
            }

            if (!differs) continue;
            Span<short> coefficients = component.Coefficients;
            for (int offset = 0; offset < coefficients.Length; offset += 64)
            {
                Span<short> block = coefficients.Slice(offset, 64);
                for (int k = 0; k < 64; k++)
                {
                    if (coarser[k] != source[k]) block[k] = (short)Requantize(block[k], source[k], coarser[k]);
                }
            }

            component.Quantization = coarser;
            changed = true;
        }

        return changed;
    }
}
