namespace PdfNetStack.Imaging;

/// <summary>
/// An image decoded to samples laid out as a PDF image XObject wants them: rows top to bottom,
/// each starting on a byte boundary, 16 bit samples big endian. Alpha, when present, is kept
/// apart for the soft mask.
/// </summary>
internal sealed class DecodedImage
{
    public DecodedImage(int width, int height, int colors, int bitsPerComponent, byte[] samples)
    {
        if (samples.Length != RowBytes(width, colors, bitsPerComponent) * height) throw new ArgumentException("Sample count does not match the size.", nameof(samples));
        Width = width;
        Height = height;
        Colors = colors;
        BitsPerComponent = bitsPerComponent;
        Samples = samples;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Color components per pixel: 1 gray or palette index, 3 RGB, 4 CMYK.</summary>
    public int Colors { get; }

    /// <summary>1, 2, 4, 8 or 16.</summary>
    public int BitsPerComponent { get; }

    public byte[] Samples { get; }

    /// <summary>RGB triples when the samples are palette indexes, else null.</summary>
    public byte[]? Palette { get; init; }

    /// <summary>One alpha sample per pixel, of <see cref="AlphaBits"/> bits, or null when the image is opaque.</summary>
    public byte[]? Alpha { get; init; }

    /// <summary>8 or 16.</summary>
    public int AlphaBits { get; init; } = 8;

    /// <summary>True when 1 bit gray samples read 0 as white (TIFF's WhiteIsZero, CCITT data).</summary>
    public bool InvertedGray { get; init; }

    public byte[]? IccProfile { get; init; }

    public double DpiX { get; init; }

    public double DpiY { get; init; }

    /// <summary>EXIF orientation, 1 (as stored) to 8.</summary>
    public int Orientation { get; init; } = 1;

    public static long RowBytes(int width, int colors, int bits) => ((long)width * colors * bits + 7) / 8;

    /// <summary>
    /// Checks that a width and height are positive, within <see cref="PngImage.MaxPixels"/>, and
    /// that their samples fit in one array.
    /// </summary>
    /// <exception cref="FormatException">The size is out of range.</exception>
    public static void CheckSize(long width, long height, int bytesPerPixel, string format)
    {
        if (width <= 0 || height <= 0 || width * height > PngImage.MaxPixels || width * height * bytesPerPixel > Compat.MaxArrayLength)
        {
            throw new FormatException(format + " image size is invalid or too large.");
        }
    }

    /// <summary>8 bit alpha, or null when every value is 255.</summary>
    public static byte[]? Translucent(byte[] alpha) => Array.TrueForAll(alpha, a => a == 255) ? null : alpha;
}
