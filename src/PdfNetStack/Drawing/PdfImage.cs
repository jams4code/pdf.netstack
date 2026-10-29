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

    /// <summary>GIF, its first frame embedded as palette samples, its transparent color as a soft mask.</summary>
    Gif,

    /// <summary>BMP, decoded and embedded as Flate compressed samples; palette images keep their palette.</summary>
    Bmp,

    /// <summary>
    /// TIFF, one page of it. A single strip of CCITT fax or JPEG data is embedded as is
    /// (CCITTFaxDecode, DCTDecode); other pages are decoded and Flate compressed.
    /// </summary>
    Tiff,

    /// <summary>WebP, lossy or lossless, its first frame decoded and Flate compressed, its alpha as a soft mask.</summary>
    WebP,
}

/// <summary>How an image fills a box of another shape (the CSS object-fit values).</summary>
public enum PdfImageFit
{
    /// <summary>Stretched to the box, its aspect ratio ignored.</summary>
    Fill,

    /// <summary>Scaled to fit inside the box, keeping its aspect ratio: the whole image shows.</summary>
    Contain,

    /// <summary>Scaled to cover the box, keeping its aspect ratio: what spills over is clipped.</summary>
    Cover,

    /// <summary>At its natural size, clipped to the box.</summary>
    None,

    /// <summary>At its natural size, or as <see cref="Contain"/> when that is smaller.</summary>
    ScaleDown,
}

/// <summary>Where an image sits in its box when it does not fill it exactly.</summary>
public enum PdfImageAlignment
{
    /// <summary>Against the top left corner.</summary>
    TopLeft,

    /// <summary>Centered along the top edge.</summary>
    Top,

    /// <summary>Against the top right corner.</summary>
    TopRight,

    /// <summary>Centered along the left edge.</summary>
    Left,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>Centered along the right edge.</summary>
    Right,

    /// <summary>Against the bottom left corner.</summary>
    BottomLeft,

    /// <summary>Centered along the bottom edge.</summary>
    Bottom,

    /// <summary>Against the bottom right corner.</summary>
    BottomRight,
}

/// <summary>
/// A JPEG, PNG, GIF, BMP, TIFF or WebP image to draw with
/// <see cref="PdfGraphics.DrawImage(PdfImage, double, double, double, double)"/>. The image is
/// written once per document however often it is drawn, and can be shared between documents and
/// threads.
/// </summary>
/// <remarks>
/// JPEGs are copied without recompression; EXIF orientation is honored, so photos stand upright.
/// PNGs keep their compressed data when that is possible (no alpha channel, not interlaced) and
/// otherwise are decoded once, their alpha becoming a soft mask. BMP, TIFF and WebP images are
/// decoded when loaded, except a TIFF page stored as one strip of fax or JPEG data, which is
/// copied. Embedded ICC profiles are kept when they describe the image's color space. Multi-page
/// TIFFs load one page at a time: see <see cref="GetPageCount(byte[])"/>.
/// Decoding is bounded: images over 2^28 pixels are refused.
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

    /// <summary>Loads an image file: JPEG, PNG, GIF, BMP, TIFF (its first page) or WebP.</summary>
    /// <exception cref="FormatException">The data is not a supported image, or is damaged.</exception>
    public static PdfImage Load(string path)
    {
        Compat.ThrowIfNull(path, nameof(path));
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>Loads an image from a stream, read to its end.</summary>
    /// <exception cref="FormatException">The data is not a supported image, or is damaged.</exception>
    public static PdfImage Load(Stream stream)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Load(copy.ToArray());
    }

    /// <summary>Loads an image from its bytes. The array is used directly; do not change it afterwards.</summary>
    /// <exception cref="FormatException">The data is not a supported image, or is damaged.</exception>
    public static PdfImage Load(byte[] data) => Load(data, 0);

    /// <summary>Loads one page of a multi-page TIFF file. Other formats have only page 0.</summary>
    /// <param name="path">The file.</param>
    /// <param name="page">The page, from 0.</param>
    /// <exception cref="FormatException">The data is not a supported image, or is damaged.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The image has no such page.</exception>
    public static PdfImage Load(string path, int page)
    {
        Compat.ThrowIfNull(path, nameof(path));
        return Load(File.ReadAllBytes(path), page);
    }

    /// <summary>Loads one page of a multi-page TIFF from its bytes. Other formats have only page 0.</summary>
    /// <param name="data">The image file's bytes, used directly; do not change them afterwards.</param>
    /// <param name="page">The page, from 0.</param>
    /// <exception cref="FormatException">The data is not a supported image, or is damaged.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The image has no such page.</exception>
    public static PdfImage Load(byte[] data, int page)
    {
        Compat.ThrowIfNull(data, nameof(data));
        int pages = GetPageCount(data);
        if (page < 0 || page >= pages) throw new ArgumentOutOfRangeException(nameof(page), page, pages == 1 ? "The image has one page." : $"The image has {pages} pages.");
        if (IsJpeg(data)) return Jpeg(data, PdfImageFormat.Jpeg);
        if (IsPng(data)) return Png(data, PdfImageFormat.Png);
        if (GifDecoder.IsGif(data)) return Gif(data);
        if (BmpDecoder.IsBmp(data)) return Bmp(data);
        if (TiffDecoder.IsTiff(data)) return Tiff(data, page);
        return Decoded(PdfImageFormat.WebP, WebPDecoder.Read(data));
    }

    /// <summary>The number of pages in a TIFF file; 1 for the other formats.</summary>
    /// <exception cref="FormatException">The data is not a supported image.</exception>
    public static int GetPageCount(string path)
    {
        Compat.ThrowIfNull(path, nameof(path));
        return GetPageCount(File.ReadAllBytes(path));
    }

    /// <summary>
    /// The number of pages in a TIFF image; 1 for the other formats. Animated GIF and WebP images
    /// count as one page: their first frame is what loads.
    /// </summary>
    /// <exception cref="FormatException">The data is not a supported image.</exception>
    public static int GetPageCount(byte[] data)
    {
        Compat.ThrowIfNull(data, nameof(data));
        if (TiffDecoder.IsTiff(data)) return TiffDecoder.CountPages(data);
        if (IsJpeg(data) || IsPng(data) || GifDecoder.IsGif(data) || BmpDecoder.IsBmp(data) || WebPDecoder.IsWebP(data)) return 1;
        throw new FormatException("Only JPEG, PNG, GIF, BMP, TIFF and WebP images are supported.");
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

    private static bool IsJpeg(byte[] data) => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8;

    private static bool IsPng(byte[] data) => data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G';

    private static PdfImage Jpeg(byte[] data, PdfImageFormat format)
    {
        JpegHeader h = JpegHeader.Read(data);
        return new PdfImage(format, h.Width, h.Height, h.DpiX, h.DpiY, h.Orientation, false, doc =>
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

    /// <summary>A GIF's first frame: 8 bit palette indexes under an Indexed color space, transparency as a soft mask.</summary>
    private static PdfImage Gif(byte[] data)
    {
        GifImage gif = GifDecoder.Read(data);
        return new PdfImage(PdfImageFormat.Gif, gif.Width, gif.Height, 0, 0, 1, gif.Alpha is not null, doc =>
        {
            int entries = Math.Max(1, Math.Min(256, gif.Palette.Length / 3));
            var dict = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(gif.Width),
                ["Height"] = PdfNumber.FromInteger(gif.Height),
                ["ColorSpace"] = PdfArray.Of(new PdfName("Indexed"), new PdfName("DeviceRGB"), PdfNumber.FromInteger(entries - 1), new PdfString(gif.Palette.AsSpan(0, 3 * entries))),
                ["BitsPerComponent"] = PdfNumber.FromInteger(8),
            };
            if (gif.Alpha is byte[] alpha)
            {
                var mask = new PdfDictionary
                {
                    ["Type"] = new PdfName("XObject"),
                    ["Subtype"] = new PdfName("Image"),
                    ["Width"] = PdfNumber.FromInteger(gif.Width),
                    ["Height"] = PdfNumber.FromInteger(gif.Height),
                    ["ColorSpace"] = new PdfName("DeviceGray"),
                    ["BitsPerComponent"] = PdfNumber.FromInteger(8),
                };
                dict.Set("SMask", doc.AddObject(PdfStream.FromData(alpha, mask)));
            }

            return doc.AddObject(PdfStream.FromData(gif.Indexes, dict));
        });
    }

    private static PdfImage Png(byte[] data, PdfImageFormat format)
    {
        PngImage png = PngImage.Read(data);
        byte[]? trns = png.Transparency;
        bool partialPaletteAlpha = png.ColorType == 3 && trns is not null && trns.Any(t => t is not (0 or 255));
        bool transparent = png.HasAlphaChannel || (trns is not null && (png.ColorType != 3 || trns.Any(t => t != 255)));
        bool passThrough = !png.Interlaced && !png.HasAlphaChannel && !partialPaletteAlpha;

        // Decode lazily, once, and only when the samples must be rewritten.
        Lazy<(byte[] Color, byte[]? Alpha)> samples = new(() => Split(png));
        return new PdfImage(format, png.Width, png.Height, png.DpiX, png.DpiY, 1, transparent, doc =>
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

    /// <summary>A bitmap; one wrapping a JPEG or PNG (compression 4 or 5) loads as that image.</summary>
    private static PdfImage Bmp(byte[] data)
    {
        if (BmpDecoder.Embedded(data) is byte[] inner)
        {
            if (IsJpeg(inner)) return Jpeg(inner, PdfImageFormat.Bmp);
            if (IsPng(inner)) return Png(inner, PdfImageFormat.Bmp);
            throw new FormatException("BMP embedded image is not a JPEG or PNG.");
        }

        return Decoded(PdfImageFormat.Bmp, BmpDecoder.Read(data));
    }

    /// <summary>A TIFF page: fax and JPEG strips copied when their parameters map to a PDF filter, else decoded.</summary>
    private static PdfImage Tiff(byte[] data, int page)
    {
        TiffPage tiff = TiffDecoder.ReadPage(data, page);
        if (tiff.PassThrough() is not TiffEncodedImage encoded) return Decoded(PdfImageFormat.Tiff, tiff.Decode());

        return new PdfImage(PdfImageFormat.Tiff, encoded.Width, encoded.Height, tiff.DpiX, tiff.DpiY, tiff.Orientation, false, doc =>
        {
            var dict = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(encoded.Width),
                ["Height"] = PdfNumber.FromInteger(encoded.Height),
                ["ColorSpace"] = ColorSpace(doc, encoded.Colors, tiff.Icc),
                ["BitsPerComponent"] = PdfNumber.FromInteger(encoded.Filter == "CCITTFaxDecode" ? 1 : 8),
                ["Filter"] = new PdfName(encoded.Filter),
            };
            if (encoded.InvertedGray) dict.Set("Decode", PdfArray.Of(1, 0));
            if (encoded.Ccitt is CcittParameters fax)
            {
                var parms = new PdfDictionary
                {
                    ["K"] = PdfNumber.FromInteger(fax.K),
                    ["Columns"] = PdfNumber.FromInteger(fax.Columns),
                    ["Rows"] = PdfNumber.FromInteger(fax.Rows),
                    ["EndOfBlock"] = PdfBoolean.False,
                };
                if (fax.EndOfLine) parms.Set("EndOfLine", PdfBoolean.True);
                if (fax.EncodedByteAlign) parms.Set("EncodedByteAlign", PdfBoolean.True);
                dict.Set("DecodeParms", parms);
            }
            else if (encoded.ColorTransform is int transform)
            {
                dict.Set("DecodeParms", new PdfDictionary { ["ColorTransform"] = PdfNumber.FromInteger(transform) });
            }

            return doc.AddObject(new PdfStream(dict, encoded.Data));
        });
    }

    /// <summary>A decoded image as Flate compressed samples, its palette as an Indexed color space, its alpha as a soft mask.</summary>
    private static PdfImage Decoded(PdfImageFormat format, DecodedImage image)
    {
        return new PdfImage(format, image.Width, image.Height, image.DpiX, image.DpiY, image.Orientation, image.Alpha is not null, doc =>
        {
            PdfObject space = image.Palette is byte[] palette
                ? PdfArray.Of(new PdfName("Indexed"), ColorSpace(doc, 3, image.IccProfile), PdfNumber.FromInteger(palette.Length / 3 - 1), new PdfString(palette.AsSpan()))
                : ColorSpace(doc, image.Colors, image.IccProfile);
            var dict = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(image.Width),
                ["Height"] = PdfNumber.FromInteger(image.Height),
                ["ColorSpace"] = space,
                ["BitsPerComponent"] = PdfNumber.FromInteger(image.BitsPerComponent),
            };
            if (image.InvertedGray && image.Palette is null) dict.Set("Decode", PdfArray.Of(1, 0));
            if (image.Alpha is byte[] alpha)
            {
                var mask = new PdfDictionary
                {
                    ["Type"] = new PdfName("XObject"),
                    ["Subtype"] = new PdfName("Image"),
                    ["Width"] = PdfNumber.FromInteger(image.Width),
                    ["Height"] = PdfNumber.FromInteger(image.Height),
                    ["ColorSpace"] = new PdfName("DeviceGray"),
                    ["BitsPerComponent"] = PdfNumber.FromInteger(image.AlphaBits),
                };
                dict.Set("SMask", doc.AddObject(PdfStream.FromData(alpha, mask)));
            }

            return doc.AddObject(PdfStream.FromData(image.Samples, dict));
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

    /// <summary>
    /// A device color space for the component count, or ICCBased when the image carries a profile
    /// for that color space. DeviceCMYK is noted on the document, for the PDF/A check.
    /// </summary>
    private static PdfObject ColorSpace(PdfDocument doc, int components, byte[]? icc)
    {
        var device = new PdfName(components switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" });

        // A profile for another color space (its header names it at byte 16) would leave the image unreadable.
        string space = components switch { 1 => "GRAY", 4 => "CMYK", _ => "RGB " };
        if (icc is null || icc.Length < 128 || icc[16] != space[0] || icc[17] != space[1] || icc[18] != space[2] || icc[19] != space[3])
        {
            if (components == 4) doc.UsesDeviceCmyk = true;
            return device;
        }
        var profile = PdfStream.FromData(icc);
        profile.Dictionary.Set("N", PdfNumber.FromInteger(components));
        profile.Dictionary.Set("Alternate", device);
        return PdfArray.Of(new PdfName("ICCBased"), doc.AddObject(profile));
    }
}
