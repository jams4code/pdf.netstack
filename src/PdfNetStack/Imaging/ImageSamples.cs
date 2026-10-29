using PdfNetStack.Filters;
using PdfNetStack.Objects;

namespace PdfNetStack.Imaging;

/// <summary>
/// Turns an image XObject or inline image into gray or RGB pixels (ISO 32000-1, 8.9): general
/// filters undone, DCTDecode decoded, samples of 1 to 16 bits unpacked, the Decode array applied
/// and the color space converted. Masks become black on white. Soft masks are not applied.
/// </summary>
internal static class ImageSamples
{
    /// <summary>Pixels one image may have, as for PNG (2^28).</summary>
    public const long MaxPixels = 1L << 28;

    /// <summary>The image codec left after general filters, or null for raw samples.</summary>
    public static string? Encoding(PdfDocument doc, PdfDictionary dict)
    {
        List<FilterSpec> filters = StreamCodec.GetFilters(dict, doc.Resolve);
        return filters.Count > 0 && !StreamCodec.IsDecodable(filters[^1].Name) ? filters[^1].Name : null;
    }

    /// <summary>The data with general filters undone: a JPEG file for DCTDecode, samples when unencoded.</summary>
    /// <exception cref="PdfFormatException">The data is damaged or exceeds the document's decode limit.</exception>
    public static byte[] EncodedData(PdfDocument doc, PdfDictionary dict, ReadOnlyMemory<byte> raw)
    {
        try
        {
            List<FilterSpec> filters = StreamCodec.GetFilters(dict, doc.Resolve);
            using DecodedStream decoded = StreamCodec.Decode(raw, filters, DecodeDepth.Full, doc.DecodeLimit, doc.Resolve);
            if (decoded.PendingPredictor is not null) throw new PdfFormatException("Image data has a predictor that cannot be undone.");
            return decoded.Data.ToArray();
        }
        catch (StreamDecodeException e)
        {
            throw new PdfFormatException("Image data cannot be decoded: " + e.Message, e);
        }
    }

    /// <summary>Gray or RGB pixels, or null for an encoding or color space this does not decode.</summary>
    /// <exception cref="PdfFormatException">The data is damaged or too large.</exception>
    public static Raster? Decode(PdfDocument doc, PdfDictionary dict, ReadOnlyMemory<byte> raw, PdfDictionary? resources)
    {
        int width = Int(doc, dict["Width"]), height = Int(doc, dict["Height"]);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) throw new PdfFormatException("Image size is invalid or too large.");
        bool mask = doc.Resolve(dict["ImageMask"]) is PdfBoolean { Value: true };
        string? encoding = Encoding(doc, dict);
        if (encoding is not (null or "DCTDecode")) return null;

        byte[] data = EncodedData(doc, dict, raw);
        int bpc = mask ? 1 : encoding == "DCTDecode" ? 8 : Int(doc, dict["BitsPerComponent"]);
        ColorSpace? space = mask ? ColorSpace.Gray : ColorSpace.Read(doc, doc.Resolve(dict["ColorSpace"]), resources, 0);

        if (encoding == "DCTDecode")
        {
            int? transform = (doc.Resolve(dict["DecodeParms"]) as PdfDictionary)?["ColorTransform"] is PdfNumber { IsInteger: true } t ? (int)t.IntegerValue : null;
            Raster jpeg;
            try
            {
                jpeg = JpegDecoder.Decode(data, transform, doc.DecodeLimit);
            }
            catch (JpegFormatException e)
            {
                throw new PdfFormatException("JPEG image cannot be decoded: " + e.Message, e);
            }

            // The JPEG's own components decide when the dictionary has no usable color space.
            space ??= jpeg.Channels switch { 1 => ColorSpace.Gray, 3 => ColorSpace.Rgb, _ => ColorSpace.Cmyk };
            if (space.Components != jpeg.Channels || jpeg.Width != width || jpeg.Height != height)
            {
                // The dictionary disagrees with the data; the data is what is there.
                space = jpeg.Channels switch { 1 => ColorSpace.Gray, 3 => ColorSpace.Rgb, _ => ColorSpace.Cmyk };
                width = jpeg.Width;
                height = jpeg.Height;
            }

            data = jpeg.Samples;
        }

        if (space is null || bpc is not (1 or 2 or 4 or 8 or 16) || (space.IsIndexed && bpc == 16)) return null;
        double[] decode = DecodeArray(doc, dict, space, bpc, mask);

        // The pixel cap alone allows 32 components of 16 bits: the bytes are what must fit.
        long rowBytes = (((long)width * space.Components * bpc) + 7) / 8;
        long outputBytes = (long)width * height * (space.IsGray ? 1 : 3);
        long ceiling = Math.Min(doc.DecodeLimit, Compat.MaxArrayLength);
        if (rowBytes * height > ceiling || outputBytes > ceiling) throw new PdfFormatException("Image exceeds the decode limit.");
        return Convert(data, width, height, bpc, rowBytes, space, decode, mask);
    }

    private static int Int(PdfDocument doc, PdfObject? value) =>
        doc.Resolve(value) is PdfNumber n ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(n.ToDouble()))) : 0;

    /// <summary>The Decode array, or the color space's default (ISO 32000-1, Table 90).</summary>
    private static double[] DecodeArray(PdfDocument doc, PdfDictionary dict, ColorSpace space, int bpc, bool mask)
    {
        int n = space.Components;
        if (doc.Resolve(dict["Decode"]) is PdfArray a && a.Count >= 2 * n && a.Take(2 * n).All(v => doc.Resolve(v) is PdfNumber))
        {
            double[] given = [.. a.Take(2 * n).Select(v => ((PdfNumber)doc.Resolve(v)!).ToDouble())];
            if (given.All(v => !double.IsNaN(v) && !double.IsInfinity(v))) return given;
        }

        return mask ? [0, 1] : space.DefaultDecode(bpc);
    }

    /// <remarks>
    /// Short data is common, and viewers draw what is there: samples past the end read as zero,
    /// without padding the data out to its declared size.
    /// </remarks>
    private static Raster Convert(byte[] data, int width, int height, int bpc, long rowBytes, ColorSpace space, double[] decode, bool mask)
    {
        int n = space.Components, channels = space.IsGray ? 1 : 3;
        var output = new byte[(long)width * height * channels];
        int max = (1 << bpc) - 1;

        // The common case, 8 bit gray or RGB read as stored, needs no arithmetic.
        if (!mask && bpc == 8 && (space == ColorSpace.Gray || space == ColorSpace.Rgb) && decode.Select((d, i) => d == i % 2).All(same => same))
        {
            long stride = (long)width * n;
            for (int y = 0; y < height; y++)
            {
                long from = y * rowBytes;
                if (from >= data.Length) break;
                data.AsSpan((int)from, (int)Math.Min(stride, data.Length - from)).CopyTo(output.AsSpan((int)(y * stride)));
            }

            return new Raster(width, height, channels, output);
        }

        // For 8 bits or less every sample value maps through a table per component.
        double[][]? tables = null;
        if (bpc <= 8)
        {
            tables = new double[n][];
            for (int c = 0; c < n; c++)
            {
                tables[c] = new double[max + 1];
                for (int v = 0; v <= max; v++) tables[c][v] = decode[2 * c] + (v * (decode[(2 * c) + 1] - decode[2 * c]) / max);
            }
        }

        Span<double> components = stackalloc double[Math.Max(n, 1)];
        Span<byte> rgb = stackalloc byte[3];
        for (int y = 0; y < height; y++)
        {
            long row = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < n; c++)
                {
                    long index = ((long)x * n) + c;
                    int v = Sample(data, row, index, bpc);
                    components[c] = tables is null ? decode[2 * c] + (v * (decode[(2 * c) + 1] - decode[2 * c]) / max) : tables[c][v];
                }

                long o = (((long)y * width) + x) * channels;
                if (mask)
                {
                    // Sample value 0 paints (ISO 32000-1, 8.9.6.2); Decode [1 0] swaps that.
                    output[o] = components[0] < 0.5 ? (byte)0 : (byte)255;
                }
                else
                {
                    space.ToRgb(components, rgb);
                    if (channels == 1)
                    {
                        output[o] = rgb[0];
                    }
                    else
                    {
                        output[o] = rgb[0];
                        output[o + 1] = rgb[1];
                        output[o + 2] = rgb[2];
                    }
                }
            }
        }

        return new Raster(width, height, channels, output);
    }

    private static int Sample(byte[] data, long row, long index, int bpc)
    {
        switch (bpc)
        {
            case 8:
                return Byte(data, row + index);
            case 16:
                return (Byte(data, row + (2 * index)) << 8) | Byte(data, row + (2 * index) + 1);
            default:
                long bit = index * bpc;
                int shift = 8 - bpc - (int)(bit & 7);
                return (Byte(data, row + (bit >> 3)) >> shift) & ((1 << bpc) - 1);
        }
    }

    private static int Byte(byte[] data, long at) => at < data.Length ? data[at] : 0;
}

/// <summary>A color space as far as reading image samples needs it: component count, default Decode and conversion.</summary>
internal abstract class ColorSpace
{
    private const int MaxNesting = 8;

    public static readonly ColorSpace Gray = new DeviceSpace(1);
    public static readonly ColorSpace Rgb = new DeviceSpace(3);
    public static readonly ColorSpace Cmyk = new DeviceSpace(4);

    public abstract int Components { get; }

    /// <summary>True for a palette, whose samples are indexes.</summary>
    public virtual bool IsIndexed => false;

    /// <summary>True when the result is gray, so one channel holds it all.</summary>
    public virtual bool IsGray => false;

    public virtual double[] DefaultDecode(int bpc)
    {
        var d = new double[2 * Components];
        for (int c = 0; c < Components; c++) d[(2 * c) + 1] = 1;
        return d;
    }

    /// <summary>Components (after Decode) to 8 bit sRGB-ish values; gray spaces fill only the first.</summary>
    public abstract void ToRgb(ReadOnlySpan<double> components, Span<byte> rgb);

    protected static byte Byte(double v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)((v * 255) + 0.5);

    /// <summary>Reads a color space: a name, a resource name, or an array (ISO 32000-1, 8.6).</summary>
    public static ColorSpace? Read(PdfDocument doc, PdfObject? value, PdfDictionary? resources, int depth)
    {
        if (depth > MaxNesting) return null;
        value = doc.Resolve(value);
        string? family = value switch
        {
            PdfName n => n.Value,
            PdfArray { Count: > 0 } a => (doc.Resolve(a[0]) as PdfName)?.Value,
            _ => null,
        };
        PdfArray? args = value as PdfArray;
        switch (family)
        {
            case "DeviceGray" or "CalGray" or "G":
                return Gray;
            case "DeviceRGB" or "CalRGB" or "RGB":
                return Rgb;
            case "DeviceCMYK" or "CMYK":
                return Cmyk;
            case "ICCBased" when args is { Count: >= 2 } && doc.Resolve(args[1]) is PdfStream profile:
                ColorSpace? alternate = Read(doc, profile.Dictionary["Alternate"], resources, depth + 1);
                return doc.Resolve(profile.Dictionary["N"]) is PdfNumber { IsInteger: true } n
                    ? n.IntegerValue switch { 1 => Gray, 3 => Rgb, 4 => Cmyk, _ => alternate }
                    : alternate;
            case "Indexed" or "I" when args is { Count: >= 4 }:
                return IndexedSpace.Read(doc, args, resources, depth);
            case "Lab" when args is { Count: >= 2 }:
                return LabSpace.Read(doc, doc.Resolve(args[1]) as PdfDictionary);
            case "Separation" when args is { Count: >= 4 }:
                return new TintSpace(1, Function.Read(doc, args[3]), Read(doc, args[2], resources, depth + 1));
            case "DeviceN" when args is { Count: >= 4 } && doc.Resolve(args[1]) is PdfArray names:
                return names.Count is > 0 and <= 32 ? new TintSpace(names.Count, Function.Read(doc, args[3]), Read(doc, args[2], resources, depth + 1)) : null;
            case not null when value is PdfName name && resources is not null
                && doc.Resolve(resources["ColorSpace"]) is PdfDictionary spaces && spaces[name.Value] is PdfObject named:
                // A resource name (inline images and some writers).
                return Read(doc, named, null, depth + 1);
            default:
                return null;
        }
    }

    private sealed class DeviceSpace(int components) : ColorSpace
    {
        public override int Components => components;

        public override bool IsGray => components == 1;

        public override void ToRgb(ReadOnlySpan<double> c, Span<byte> rgb)
        {
            switch (components)
            {
                case 1:
                    rgb[0] = rgb[1] = rgb[2] = Byte(c[0]);
                    break;
                case 3:
                    rgb[0] = Byte(c[0]);
                    rgb[1] = Byte(c[1]);
                    rgb[2] = Byte(c[2]);
                    break;
                default:
                    // The naive conversion without a profile (ISO 32000-1, 10.3.5).
                    double k = 1 - Math.Min(1, Math.Max(0, c[3]));
                    rgb[0] = Byte((1 - c[0]) * k);
                    rgb[1] = Byte((1 - c[1]) * k);
                    rgb[2] = Byte((1 - c[2]) * k);
                    break;
            }
        }
    }

    /// <summary>Indexed (ISO 32000-1, 8.6.6.3): a palette of colors in a base space.</summary>
    private sealed class IndexedSpace : ColorSpace
    {
        private readonly byte[] _palette;
        private readonly int _count;
        private readonly bool _gray;

        private IndexedSpace(byte[] palette, int count, bool gray)
        {
            _palette = palette;
            _count = count;
            _gray = gray;
        }

        public override int Components => 1;

        public override bool IsIndexed => true;

        public override bool IsGray => _gray;

        public override double[] DefaultDecode(int bpc) => [0, (1 << bpc) - 1];

        public static ColorSpace? Read(PdfDocument doc, PdfArray args, PdfDictionary? resources, int depth)
        {
            ColorSpace? baseSpace = ColorSpace.Read(doc, args[1], resources, depth + 1);
            if (baseSpace is null or IndexedSpace || doc.Resolve(args[2]) is not PdfNumber hival) return null;
            int count = (int)Math.Max(1, Math.Min(256, hival.ToDouble() + 1));
            byte[]? lookup = doc.Resolve(args[3]) switch
            {
                PdfString s => s.Bytes.ToArray(),
                PdfStream st => Bytes(doc, st),
                _ => null,
            };
            if (lookup is null) return null;

            // Each entry, through the base space, as RGB.
            int n = baseSpace.Components;
            var palette = new byte[count * 3];
            Span<double> components = stackalloc double[n];
            Span<byte> rgb = stackalloc byte[3];
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < n; c++)
                {
                    int at = (i * n) + c;
                    double v = at < lookup.Length ? lookup[at] / 255.0 : 0;
                    double[] range = baseSpace.DefaultDecode(8);
                    components[c] = range[2 * c] + (v * (range[(2 * c) + 1] - range[2 * c]));
                }

                baseSpace.ToRgb(components, rgb);
                palette[3 * i] = rgb[0];
                palette[(3 * i) + 1] = rgb[1];
                palette[(3 * i) + 2] = rgb[2];
            }

            return new IndexedSpace(palette, count, baseSpace.IsGray);
        }

        private static byte[]? Bytes(PdfDocument doc, PdfStream stream)
        {
            try
            {
                return doc.GetStreamData(stream);
            }
            catch (PdfFormatException)
            {
                return null;
            }
        }

        public override void ToRgb(ReadOnlySpan<double> c, Span<byte> rgb)
        {
            // Out of range indexes clamp to the palette (ISO 32000-1, 8.6.6.3).
            int i = (int)Math.Max(0, Math.Min(_count - 1, Math.Round(c[0])));
            rgb[0] = _palette[3 * i];
            rgb[1] = _palette[(3 * i) + 1];
            rgb[2] = _palette[(3 * i) + 2];
        }
    }

    /// <summary>CIE L*a*b* (ISO 32000-1, 8.6.5.4) to sRGB through XYZ, adapted from its white point.</summary>
    private sealed class LabSpace : ColorSpace
    {
        private readonly double[] _white;
        private readonly double[] _range;

        private LabSpace(double[] white, double[] range)
        {
            _white = white;
            _range = range;
        }

        public override int Components => 3;

        public override double[] DefaultDecode(int bpc) => [0, 100, _range[0], _range[1], _range[2], _range[3]];

        public static ColorSpace Read(PdfDocument doc, PdfDictionary? dict)
        {
            double[] white = Numbers(doc, dict?["WhitePoint"], 3) ?? [0.9505, 1, 1.089];
            double[] range = Numbers(doc, dict?["Range"], 4) ?? [-100, 100, -100, 100];
            if (white[1] <= 0) white = [0.9505, 1, 1.089];
            return new LabSpace(white, range);
        }

        public override void ToRgb(ReadOnlySpan<double> c, Span<byte> rgb)
        {
            double l = c[0], a = Math.Max(_range[0], Math.Min(_range[1], c[1])), b = Math.Max(_range[2], Math.Min(_range[3], c[2]));
            double m = (l + 16) / 116;
            double x = _white[0] * G(m + (a / 500)), y = _white[1] * G(m), z = _white[2] * G(m - (b / 200));

            // XYZ (scaled so the white point is D65's) to linear sRGB, then companded.
            x *= 0.9505 / _white[0];
            z *= 1.089 / _white[2];
            rgb[0] = Byte(Gamma((3.2406 * x) - (1.5372 * y) - (0.4986 * z)));
            rgb[1] = Byte(Gamma((-0.9689 * x) + (1.8758 * y) + (0.0415 * z)));
            rgb[2] = Byte(Gamma((0.0557 * x) - (0.2040 * y) + (1.0570 * z)));
        }

        private static double G(double t) => t >= 6.0 / 29 ? t * t * t : 108.0 / 841 * (t - (4.0 / 29));

        private static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : (1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4)) - 0.055;
    }

    /// <summary>
    /// Separation and DeviceN (ISO 32000-1, 8.6.6.4 and 8.6.6.5): tints through the tint transform
    /// into the alternate space when it is an exponential function, else gray by the darkest tint.
    /// </summary>
    private sealed class TintSpace(int components, Function? transform, ColorSpace? alternate) : ColorSpace
    {
        public override int Components => components;

        // A Type 2 function has one input, so it can only be a Separation's transform.
        private bool Transforms => components == 1 && transform is not null && alternate is not null && transform.Outputs == alternate.Components;

        public override bool IsGray => !Transforms || alternate!.IsGray;

        public override void ToRgb(ReadOnlySpan<double> c, Span<byte> rgb)
        {
            if (Transforms)
            {
                Span<double> output = stackalloc double[alternate!.Components];
                transform!.Evaluate(c, output);
                alternate.ToRgb(output, rgb);
                return;
            }

            double tint = 0;
            foreach (double v in c) tint = Math.Max(tint, v);
            rgb[0] = rgb[1] = rgb[2] = Byte(1 - tint);
        }
    }

    private static double[]? Numbers(PdfDocument doc, PdfObject? value, int count)
    {
        if (doc.Resolve(value) is not PdfArray a || a.Count < count) return null;
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (doc.Resolve(a[i]) is not PdfNumber n) return null;
            result[i] = n.ToDouble();
            if (double.IsNaN(result[i]) || double.IsInfinity(result[i])) return null;
        }

        return result;
    }

    /// <summary>A Type 2 (exponential interpolation) function, the common tint transform (ISO 32000-1, 7.10.3).</summary>
    internal sealed class Function
    {
        private readonly double[] _c0;
        private readonly double[] _c1;
        private readonly double _exponent;

        private Function(double[] c0, double[] c1, double exponent)
        {
            _c0 = c0;
            _c1 = c1;
            _exponent = exponent;
        }

        public int Outputs => _c0.Length;

        public static Function? Read(PdfDocument doc, PdfObject? value)
        {
            PdfDictionary? dict = doc.Resolve(value) switch
            {
                PdfDictionary d => d,
                PdfStream s => s.Dictionary,
                _ => null,
            };
            if (dict is null || doc.Resolve(dict["FunctionType"]) is not PdfNumber { IsInteger: true, IntegerValue: 2 }) return null;
            if (doc.Resolve(dict["N"]) is not PdfNumber n) return null;
            double[] c0 = ArrayOf(doc, dict["C0"]) ?? [0];
            double[] c1 = ArrayOf(doc, dict["C1"]) ?? [1];
            return c0.Length == c1.Length && c0.Length is > 0 and <= 32 ? new Function(c0, c1, n.ToDouble()) : null;
        }

        private static double[]? ArrayOf(PdfDocument doc, PdfObject? value) =>
            doc.Resolve(value) is PdfArray a ? Numbers(doc, a, a.Count) : null;

        public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
        {
            double x = Math.Max(0, Math.Min(1, input[0]));
            double t = Math.Pow(x, _exponent);
            for (int i = 0; i < output.Length; i++) output[i] = _c0[i] + (t * (_c1[i] - _c0[i]));
        }
    }
}
