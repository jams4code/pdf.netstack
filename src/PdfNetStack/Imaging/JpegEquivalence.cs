namespace PdfNetStack.Imaging;

/// <summary>
/// Decides whether two JPEG streams decode to the same image. They must agree on geometry,
/// components, sampling and color transform. Every visible coefficient must be equal or, when
/// requantization is allowed, exactly the requantized source value under a table that is never
/// finer than the source. Entropy coding, restart markers and unused segments may differ.
/// </summary>
internal static class JpegEquivalence
{
    /// <returns>Null when equivalent, otherwise the first difference found.</returns>
    public static string? FindDifference(ReadOnlySpan<byte> source, ReadOnlySpan<byte> output, bool allowRequantization, long coefficientByteLimit)
    {
        JpegImage a, b;
        try
        {
            a = JpegReader.Read(source, coefficientByteLimit);
        }
        catch (JpegFormatException)
        {
            return "source JPEG data cannot be decoded and was changed";
        }

        using (a)
        {
            try
            {
                b = JpegReader.Read(output, coefficientByteLimit);
            }
            catch (JpegFormatException ex)
            {
                return "output JPEG data cannot be decoded: " + ex.Message;
            }

            using (b)
            {
                return Compare(a, b, allowRequantization);
            }
        }
    }

    private static string? Compare(JpegImage a, JpegImage b, bool allowRequantization)
    {
        if (a.Width != b.Width || a.Height != b.Height) return "JPEG dimensions differ";
        if (a.Components.Length != b.Components.Length) return "JPEG component count differs";
        if (a.AdobeTransform != b.AdobeTransform || a.HasJfif != b.HasJfif) return "JPEG color transform signalling differs";

        for (int i = 0; i < a.Components.Length; i++)
        {
            JpegComponent ca = a.Components[i], cb = b.Components[i];
            if (ca.Id != cb.Id || ca.Horizontal != cb.Horizontal || ca.Vertical != cb.Vertical) return "JPEG component layout differs";

            ushort[] qa = ca.Quantization, qb = cb.Quantization;
            for (int k = 0; k < 64; k++)
            {
                if (qb[k] == qa[k]) continue;
                if (!allowRequantization) return "JPEG quantization changed";
                if (qb[k] < qa[k]) return "JPEG quantization became finer than the source";
            }

            // Padding blocks outside the image are never displayed, so only visible blocks count.
            for (int row = 0; row < ca.BlocksPerColumn; row++)
            {
                for (int column = 0; column < ca.BlocksPerLine; column++)
                {
                    Span<short> blockA = ca.Block(row, column), blockB = cb.Block(row, column);
                    for (int k = 0; k < 64; k++)
                    {
                        if (blockB[k] != JpegQuantization.Requantize(blockA[k], qa[k], qb[k]))
                        {
                            return Compat.Format("JPEG coefficient differs in component {0}, block {1},{2}", i, row, column);
                        }
                    }
                }
            }
        }

        return null;
    }
}
