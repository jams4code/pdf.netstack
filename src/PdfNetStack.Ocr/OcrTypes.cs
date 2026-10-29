using PdfNetStack.Drawing;

namespace PdfNetStack.Ocr;

/// <summary>Recognizes text in images. Implementations must be safe to call from several threads.</summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>Recognizes one upright 8 bit gray image; boxes come back in its pixel coordinates.</summary>
    /// <param name="image">The image, text upright.</param>
    /// <param name="request">The script to read.</param>
    /// <param name="cancellationToken">Stops the work between lines.</param>
    OcrImageResult Recognize(OcrBitmap image, OcrRequest request, CancellationToken cancellationToken);
}

/// <summary>An 8 bit gray image: rows top to bottom, <see cref="Width"/> bytes each, 0 black.</summary>
public sealed class OcrBitmap
{
    /// <summary>Wraps gray pixels.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="pixels">Width times height bytes.</param>
    /// <exception cref="ArgumentException">The pixel count does not match the size.</exception>
    public OcrBitmap(int width, int height, byte[] pixels)
    {
        Compat.ThrowIfNull(pixels, nameof(pixels));
        if (width <= 0 || height <= 0 || pixels.Length != (long)width * height) throw new ArgumentException("The pixels do not match the size.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixels, one byte each.</summary>
    public byte[] Pixels { get; }

    /// <summary>Gray from a decoded PDF image: RGB is weighed by luma (ITU-R BT.601).</summary>
    /// <param name="bitmap">The decoded image.</param>
    public static OcrBitmap FromPdfBitmap(PdfBitmap bitmap)
    {
        Compat.ThrowIfNull(bitmap, nameof(bitmap));
        ReadOnlySpan<byte> source = bitmap.Pixels.Span;
        var gray = new byte[(long)bitmap.Width * bitmap.Height];
        if (bitmap.Format == PdfBitmapFormat.Gray8)
        {
            source.CopyTo(gray);
        }
        else
        {
            for (int i = 0; i < gray.Length; i++)
            {
                gray[i] = (byte)(((299 * source[3 * i]) + (587 * source[(3 * i) + 1]) + (114 * source[(3 * i) + 2]) + 500) / 1000);
            }
        }

        return new OcrBitmap(bitmap.Width, bitmap.Height, gray);
    }
}

/// <summary>What to read.</summary>
public sealed class OcrRequest
{
    /// <summary>The script: <c>latin</c> (default) or <c>arabic</c>, which also reads Persian and Urdu.</summary>
    public string Language { get; init; } = "latin";
}

/// <summary>Text found in one image, lines in reading order.</summary>
public sealed class OcrImageResult
{
    /// <summary>Wraps lines.</summary>
    /// <param name="lines">The lines, in reading order.</param>
    public OcrImageResult(IReadOnlyList<OcrLine> lines) => Lines = lines ?? [];

    /// <summary>The lines, top to bottom.</summary>
    public IReadOnlyList<OcrLine> Lines { get; }

    /// <summary>All the text, lines joined with newlines.</summary>
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>A line of text.</summary>
/// <param name="Text">The text in logical order.</param>
/// <param name="Quad">Corners as the text stands: top left, top right, bottom right, bottom left.</param>
/// <param name="Confidence">Mean character confidence, 0 to 1.</param>
/// <param name="Words">The words, in logical order.</param>
/// <param name="RightToLeft">True when the line reads right to left.</param>
public sealed record OcrLine(string Text, PdfPoint[] Quad, double Confidence, IReadOnlyList<OcrWord> Words, bool RightToLeft);

/// <summary>A word.</summary>
/// <param name="Text">The text in logical order.</param>
/// <param name="Quad">Corners as the text stands: top left, top right, bottom right, bottom left.</param>
/// <param name="Confidence">Mean character confidence, 0 to 1.</param>
public sealed record OcrWord(string Text, PdfPoint[] Quad, double Confidence);
