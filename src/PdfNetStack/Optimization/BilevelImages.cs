using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// 1 bit images for the Group 4 pass and its proof: which images qualify, their exact pixels
/// whether stored as samples or as CCITT data, and the Group 4 encoding the pass writes. CCITT
/// data counts only when every reader takes it the same way: parameters that leave nothing to a
/// reader's defaults, and data that decodes without a flaw.
/// </summary>
internal static class BilevelImages
{
    /// <summary>
    /// The size of an image whose samples are one bit of one component: a stencil mask, or a 1 bit
    /// gray, calibrated gray, palette, separation or single colorant image. Null for anything else.
    /// </summary>
    public static (int Width, int Height)? Size(PdfFile file, PdfDictionary d)
    {
        if (d.GetName("Subtype") != "Image") return null;
        int width = Int(file, d["Width"]), height = Int(file, d["Height"]);
        if (width <= 0 || height <= 0 || width > CcittDecoder.MaxColumns || (long)width * height > ImageSamples.MaxPixels) return null;
        if (file.Resolve(d["ImageMask"]) is PdfBoolean { Value: true }) return (width, height);
        return Int(file, d["BitsPerComponent"]) == 1 && OneComponent(file, d["ColorSpace"]) ? (width, height) : null;
    }

    private static bool OneComponent(PdfFile file, PdfObject? colorSpace)
    {
        PdfObject? cs = file.Resolve(colorSpace);
        if (cs is PdfName n) return n.Value is "DeviceGray" or "G" or "CalGray";
        if (cs is not PdfArray { Count: > 0 } a || file.Resolve(a[0]) is not PdfName family) return false;
        return family.Value switch
        {
            "CalGray" or "Indexed" or "I" or "Separation" => true,
            "ICCBased" => a.Count > 1 && file.Resolve(a[1]) is PdfStream icc && file.Resolve(icc.Dictionary["N"]) is PdfNumber { IsInteger: true, IntegerValue: 1 },
            "DeviceN" => a.Count > 1 && file.Resolve(a[1]) is PdfArray { Count: 1 },
            _ => false,
        };
    }

    /// <summary>
    /// CCITTFaxDecode parameters for an image of the given size; null when any value is not of its
    /// type, the columns differ from the width (the default is 1728, which some readers apply and
    /// others replace by the width), or the rows are neither unset nor the height.
    /// </summary>
    public static CcittParameters? Parameters(PdfObject? parms, int width, int height, Func<PdfObject?, PdfObject?> resolve)
    {
        parms = resolve(parms);
        if (parms is not (null or PdfNull or PdfDictionary)) return null;
        PdfDictionary d = parms as PdfDictionary ?? new PdfDictionary();
        int? k = Integer(resolve(d["K"]), 0), columns = Integer(resolve(d["Columns"]), 1728), rows = Integer(resolve(d["Rows"]), 0);
        bool? endOfLine = Flag(resolve(d["EndOfLine"]), false), byteAlign = Flag(resolve(d["EncodedByteAlign"]), false);
        bool? endOfBlock = Flag(resolve(d["EndOfBlock"]), true), blackIs1 = Flag(resolve(d["BlackIs1"]), false);
        if (k is null || columns != width || rows is null || (rows != 0 && rows != height)) return null;
        if (endOfLine is null || byteAlign is null || endOfBlock is null || blackIs1 is null) return null;
        return new CcittParameters
        {
            K = k.Value,
            EndOfLine = endOfLine.Value,
            EncodedByteAlign = byteAlign.Value,
            Columns = width,
            Rows = rows.Value,
            EndOfBlock = endOfBlock.Value,
            BlackIs1 = blackIs1.Value,
        };

        static int? Integer(PdfObject? value, int fallback) => value switch
        {
            null or PdfNull => fallback,
            PdfNumber { IsInteger: true } n when n.IntegerValue is >= int.MinValue and <= int.MaxValue => (int)n.IntegerValue,
            _ => null,
        };

        static bool? Flag(PdfObject? value, bool fallback) => value switch
        {
            null or PdfNull => fallback,
            PdfBoolean b => b.Value,
            _ => null,
        };
    }

    /// <summary>
    /// The image's pixels as packed rows padded to a whole byte: the samples themselves when
    /// <paramref name="ccitt"/> is null, else the CCITT data decoded. Null when the samples are
    /// short or the data does not decode without a flaw.
    /// </summary>
    public static byte[]? Pixels(ReadOnlySpan<byte> data, CcittParameters? ccitt, int width, int height, long decodeLimit)
    {
        long size = (long)((width + 7) / 8) * height;
        if (size > Math.Min(decodeLimit, Compat.MaxArrayLength)) return null;
        if (ccitt is null) return data.Length < size ? null : data.Slice(0, (int)size).ToArray();
        return CcittDecoder.DecodeExact(data, ccitt, height);
    }

    /// <summary>True when both hold the same pixels; the padding bits at the end of each row are not pixels.</summary>
    public static bool SamePixels(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height)
    {
        int rowBytes = (width + 7) / 8;
        int whole = width >> 3;
        byte last = (byte)(0xFF << (8 - (width & 7)));
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> ra = a.Slice(y * rowBytes, rowBytes), rb = b.Slice(y * rowBytes, rowBytes);
            if (!ra.Slice(0, whole).SequenceEqual(rb.Slice(0, whole))) return false;
            if (whole < rowBytes && ((ra[whole] ^ rb[whole]) & last) != 0) return false;
        }

        return true;
    }

    /// <summary>
    /// Group 4 data for packed rows and its /DecodeParms, or null when it would not fit in
    /// <paramref name="maxBytes"/>. The more common value is coded as white, which keeps long runs
    /// in the shorter white codes; /BlackIs1 says which value that is, so the samples stay as they were.
    /// </summary>
    public static (byte[] Data, PdfDictionary Parms)? Group4(ReadOnlySpan<byte> rows, int width, int height, int maxBytes)
    {
        bool blackIs1 = BilevelResampler.OnesAreForeground(rows, width, height);
        byte[]? data = CcittEncoder.EncodeGroup4(rows, width, height, blackIs1, maxBytes);
        if (data is null) return null;
        var parms = new PdfDictionary();
        parms.Set("K", PdfNumber.FromInteger(-1));
        parms.Set("Columns", PdfNumber.FromInteger(width));
        parms.Set("Rows", PdfNumber.FromInteger(height));
        if (blackIs1) parms.Set("BlackIs1", PdfBoolean.True);
        return (data, parms);
    }

    private static int Int(PdfFile file, PdfObject? o) =>
        file.Resolve(o) is PdfNumber { IsInteger: true } n && n.IntegerValue is > 0 and < int.MaxValue ? (int)n.IntegerValue : 0;
}
