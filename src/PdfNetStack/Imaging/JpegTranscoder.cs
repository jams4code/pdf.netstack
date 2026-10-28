namespace PdfNetStack.Imaging;

/// <summary>
/// Re-encodes JPEG data at the coefficient level. Losslessly, by rebuilding the Huffman tables and
/// dropping segments a PDF reader never uses. Optionally lossy, by requantizing to a JPEG quality.
/// Components, sampling and color transform pass through, so the color model can never change.
/// </summary>
internal static class JpegTranscoder
{
    /// <param name="jpeg">The DCTDecode stream content.</param>
    /// <param name="quality">Target quality 1..100 for requantization, or null to stay lossless.</param>
    /// <param name="pdfColorTransform">The /ColorTransform decode parameter, when the PDF sets one.</param>
    /// <param name="expectedComponents">Components the image's color space requires; a mismatch leaves the data alone.</param>
    /// <param name="coefficientByteLimit">Ceiling for the coefficient buffers.</param>
    /// <param name="requantized">Set when coefficients were requantized, so the output is no longer lossless.</param>
    /// <returns>The re-encoded data, or null when it is not a supported JPEG.</returns>
    public static byte[]? TryTranscode(
        ReadOnlySpan<byte> jpeg,
        int? quality,
        int? pdfColorTransform,
        int expectedComponents,
        long coefficientByteLimit,
        out bool requantized)
    {
        requantized = false;
        JpegImage image;
        try
        {
            image = JpegReader.Read(jpeg, coefficientByteLimit);
        }
        catch (JpegFormatException)
        {
            return null;
        }

        using (image)
        {
            if (image.Components.Length != expectedComponents) return null;
            if (quality is { } q) requantized = JpegQuantization.Requantize(image, q, image.ChromaComponents(pdfColorTransform));
            byte[]? output = JpegWriter.TryWrite(image, jpeg.Length);
            if (output is null) requantized = false;
            return output;
        }
    }
}
