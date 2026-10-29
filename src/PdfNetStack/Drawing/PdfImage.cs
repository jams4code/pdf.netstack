using PdfNetStack.Imaging;
using PdfNetStack.Objects;

namespace PdfNetStack.Drawing;

/// <summary>The file format an image came from.</summary>
public enum PdfImageFormat
{
    /// <summary>JPEG, embedded as is (DCTDecode).</summary>
    Jpeg,

    /// <summary>PNG, embedded as Flate compressed samples, with its alpha channel as a soft mask.</summary>
    Png,
}

/// <summary>
/// A JPEG or PNG image to draw with <see cref="PdfGraphics.DrawImage(PdfImage, double, double, double, double)"/>.
/// The image is written once per document however often it is drawn, and can be shared between
/// documents and threads.
/// </summary>
/// <remarks>
/// JPEGs are copied without recompression; EXIF orientation is honored, so photos stand upright.
/// PNGs keep their compressed data when that is possible (no alpha channel, not interlaced) and
/// otherwise are decoded once, their alpha becoming a soft mask. Embedded ICC profiles are kept.
/// </remarks>
public sealed class PdfImage
{
    private const double DefaultDpi = 96;

    private readonly Func<PdfDocument, PdfReference> _write;

    private PdfImage(PdfImageFormat format, int pixelWidth, int pixelHeight, double dpiX, double dpiY, int orientation, bool transparent, Func<PdfDocument, PdfReference> write)
    {
        Format = format;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        HorizontalResolution = dpiX > 0 ? dpiX : DefaultDpi;
        VerticalResolution = dpiY > 0 ? dpiY : DefaultDpi;
        Orientation = orientation;
        HasTransparency = transparent;
        _write = write;
    }

    /// <summary>The source format.</summary>
    public PdfImageFormat Format { get; }

    /// <summary>Width of the stored image in pixels.</summary>
    public int PixelWidth { get; }

    /// <summary>Height of the stored image in pixels.</summary>
    public int PixelHeight { get; }

    /// <summary>Horizontal pixels per inch, as the file records it, else 96.</summary>
    public double HorizontalResolution { get; }

    /// <summary>Vertical pixels per inch, as the file records it, else 96.</summary>
    public double VerticalResolution { get; }

    /// <summary>True when the image carries transparency: an alpha channel or a transparent color.</summary>
    public bool HasTransparency { get; }

    /// <summary>Natural width in points as displayed, after EXIF orientation.</summary>
    public double Width => Orientation >= 5 ? PixelHeight * 72 / VerticalResolution : PixelWidth * 72 / HorizontalResolution;

    /// <summary>Natural height in points as displayed, after EXIF orientation.</summary>
    public double Height => Orientation >= 5 ? PixelWidth * 72 / HorizontalResolution : PixelHeight * 72 / VerticalResolution;

    /// <summary>EXIF orientation, 1 (as stored) to 8.</summary>
    internal int Orientation { get; }

    /// <summary>Loads a JPEG or PNG file.</summary>
    /// <exception cref="FormatException">The data is not a supported image.</exception>
    public static PdfImage Load(string path)
    {
        Compat.ThrowIfNull(path, nameof(path));
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>Loads a JPEG or PNG image from a stream, read to its end.</summary>
    /// <exception cref="FormatException">The data is not a supported image.</exception>
    public static PdfImage Load(Stream stream)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Load(copy.ToArray());
    }

    /// <summary>Loads a JPEG or PNG image from its bytes. The array is used directly; do not change it afterwards.</summary>
    /// <exception cref="FormatException">The data is not a supported image.</exception>
    public static PdfImage Load(byte[] data)
    {
        Compat.ThrowIfNull(data, nameof(data));
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8) return Jpeg(data);
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return Png(data);
        throw new FormatException("Only JPEG and PNG images are supported.");
    }

    /// <summary>Writes the image XObject, and its soft mask, into a document.</summary>
    internal PdfReference AddTo(PdfDocument document) => _write(document);

    /// <summary>
    /// The transform from image space (the unit square, first row at the top) to a rectangle on
    /// the y down canvas, turned and mirrored as the EXIF orientation says.
    /// </summary>
    internal PdfMatrix Placement(double x, double y, double width, double height)
    {
        // Displayed position (X, Y), 0 to 1 within the rectangle, from stored position (u, v):
        // X = au + bv + c and Y = du + ev + f.
        (int a, int b, int c, int d, int e, int f) = Orientation switch
        {
            2 => (-1, 0, 1, 0, 1, 0),
            3 => (-1, 0, 1, 0, -1, 1),
            4 => (1, 0, 0, 0, -1, 1),
            5 => (0, 1, 0, 1, 0, 0),
            6 => (0, -1, 1, 1, 0, 0),
            7 => (0, -1, 1, -1, 0, 1),
            8 => (0, 1, 0, -1, 0, 1),
            _ => (1, 0, 0, 0, 1, 0),
        };

        // Image space has v = 1 - y_image, so substitute and scale to the rectangle.
        return new PdfMatrix(width * a, height * d, -width * b, -height * e, x + width * (b + c), y + height * (e + f));
    }

    private static PdfImage Jpeg(byte[] data)
    {
        JpegHeader h = JpegHeader.Read(data);
        return new PdfImage(PdfImageFormat.Jpeg, h.Width, h.Height, h.DpiX, h.DpiY, h.Orientation, false, doc =>
        {
            var dict = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(h.Width),
                ["Height"] = PdfNumber.FromInteger(h.Height),
                ["BitsPerComponent"] = PdfNumber.FromInteger(8),
                ["ColorSpace"] = ColorSpace(doc, h.Components, h.IccProfile),
                ["Filter"] = new PdfName("DCTDecode"),
            };

            // Adobe applications write CMYK JPEGs inverted; every reader undoes it this way.
            if (h.Components == 4 && h.Adobe) dict.Set("Decode", PdfArray.Of(1, 0, 1, 0, 1, 0, 1, 0));
            return doc.AddObject(new PdfStream(dict, data));
        });
    }

    private static PdfImage Png(byte[] data)
    {
        PngImage png = PngImage.Read(data);
        byte[]? trns = png.Transparency;
        bool partialPaletteAlpha = png.ColorType == 3 && trns is not null && trns.Any(t => t is not (0 or 255));
        bool transparent = png.HasAlphaChannel || (trns is not null && (png.ColorType != 3 || trns.Any(t => t != 255)));
        bool passThrough = !png.Interlaced && !png.HasAlphaChannel && !partialPaletteAlpha;

        // Decode lazily, once, and only when the samples must be rewritten.
        Lazy<(byte[] Color, byte[]? Alpha)> samples = new(() => Split(png));
        return new PdfImage(PdfImageFormat.Png, png.Width, png.Height, png.DpiX, png.DpiY, 1, transparent, doc =>
        {
            int depth = png.BitDepth;
            var dict = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(png.Width),
                ["Height"] = PdfNumber.FromInteger(png.Height),
                ["ColorSpace"] = png.ColorType == 3 ? Indexed(doc, png) : ColorSpace(doc, png.Colors, png.IccProfile),
            };

            PdfStream stream;
            if (passThrough)
            {
                dict.Set("BitsPerComponent", PdfNumber.FromInteger(depth));
                dict.Set("Filter", new PdfName("FlateDecode"));
                dict.Set("DecodeParms", new PdfDictionary
                {
                    ["Predictor"] = PdfNumber.FromInteger(15),
                    ["Colors"] = PdfNumber.FromInteger(png.Colors),
                    ["BitsPerComponent"] = PdfNumber.FromInteger(depth),
                    ["Columns"] = PdfNumber.FromInteger(png.Width),
                });
                stream = new PdfStream(dict, png.Data);
            }
            else
            {
                (byte[] color, byte[]? alpha) = samples.Value;
                dict.Set("BitsPerComponent", PdfNumber.FromInteger(depth));
                stream = PdfStream.FromData(color, dict);
                if (alpha is not null)
                {
                    int alphaDepth = png.ColorType == 3 ? 8 : depth;
                    var mask = new PdfDictionary
                    {
                        ["Type"] = new PdfName("XObject"),
                        ["Subtype"] = new PdfName("Image"),
                        ["Width"] = PdfNumber.FromInteger(png.Width),
                        ["Height"] = PdfNumber.FromInteger(png.Height),
                        ["ColorSpace"] = new PdfName("DeviceGray"),
                        ["BitsPerComponent"] = PdfNumber.FromInteger(alphaDepth),
                    };
                    dict.Set("SMask", doc.AddObject(PdfStream.FromData(alpha, mask)));
                }
            }

            PdfArray? colorKey = ColorKeyMask(png);
            if (colorKey is not null) dict.Set("Mask", colorKey);
            return doc.AddObject(stream);
        });
    }

    /// <summary>
    /// Color samples without alpha, and the alpha channel when there is one: from the alpha
    /// samples of gray and RGB images, or from the palette's tRNS entries.
    /// </summary>
    private static (byte[] Color, byte[]? Alpha) Split(PngImage png)
    {
        byte[] decoded = png.Decode();
        int width = png.Width, height = png.Height, depth = png.BitDepth;
        if (png.HasAlphaChannel)
        {
            int bytes = depth / 8, colors = png.Colors, pixel = (colors + 1) * bytes;
            var color = new byte[(long)width * height * colors * bytes];
            var alpha = new byte[(long)width * height * bytes];
            for (long p = 0, c = 0, a = 0; p < decoded.Length; p += pixel)
            {
                Array.Copy(decoded, p, color, c, colors * bytes);
                Array.Copy(decoded, p + colors * bytes, alpha, a, bytes);
                c += colors * bytes;
                a += bytes;
            }

            return (color, alpha);
        }

        if (png.ColorType == 3 && png.Transparency is byte[] trns && trns.Any(t => t is not (0 or 255)))
        {
            long stride = png.RowBytes(width);
            var alpha = new byte[(long)width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int bit = x * depth;
                    int index = (decoded[y * stride + bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                    alpha[(long)y * width + x] = index < trns.Length ? trns[index] : (byte)255;
                }
            }

            return (decoded, alpha);
        }

        return (decoded, null);
    }

    /// <summary>tRNS as a color key mask: one transparent gray or RGB value, or fully transparent palette entries.</summary>
    private static PdfArray? ColorKeyMask(PngImage png)
    {
        byte[]? t = png.Transparency;
        if (t is null) return null;
        switch (png.ColorType)
        {
            case 0 when t.Length >= 2:
            {
                int v = t[0] << 8 | t[1];
                return PdfArray.Of(v, v);
            }

            case 2 when t.Length >= 6:
            {
                int r = t[0] << 8 | t[1], g = t[2] << 8 | t[3], b = t[4] << 8 | t[5];
                return PdfArray.Of(r, r, g, g, b, b);
            }

            case 3 when t.All(a => a is 0 or 255) && t.Any(a => a == 0):
            {
                var ranges = new PdfArray();
                for (int i = 0; i < t.Length; i++)
                {
                    if (t[i] != 0) continue;
                    ranges.Add(PdfNumber.FromInteger(i));
                    ranges.Add(PdfNumber.FromInteger(i));
                }

                return ranges;
            }

            default:
                return null;
        }
    }

    private static PdfObject Indexed(PdfDocument doc, PngImage png)
    {
        int entries = Math.Min(256, png.Palette!.Length / 3);
        var lookup = new byte[3 * entries];
        Array.Copy(png.Palette, lookup, lookup.Length);
        return PdfArray.Of(new PdfName("Indexed"), ColorSpace(doc, 3, png.IccProfile), PdfNumber.FromInteger(entries - 1), new PdfString(lookup.AsSpan()));
    }

    /// <summary>A device color space for the component count, or ICCBased when the image carries a profile.</summary>
    private static PdfObject ColorSpace(PdfDocument doc, int components, byte[]? icc)
    {
        var device = new PdfName(components switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" });
        if (icc is null) return device;
        var profile = PdfStream.FromData(icc);
        profile.Dictionary.Set("N", PdfNumber.FromInteger(components));
        profile.Dictionary.Set("Alternate", device);
        return PdfArray.Of(new PdfName("ICCBased"), doc.AddObject(profile));
    }
}
