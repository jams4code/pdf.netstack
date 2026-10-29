using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Content;

/// <summary>
/// Where the data of an inline image ends (ISO 32000-1, 8.9.7). Filtered data has no length, so
/// readers search for EI, and some check that what follows looks like content; a /L entry or the
/// size of unfiltered samples gives the end directly. Passes that rewrite content around an
/// inline image only do so when every one of these rules finds the same end.
/// </summary>
internal static class InlineImageBounds
{
    /// <param name="s">The whole content stream.</param>
    /// <param name="dataStart">First byte after the single white-space character that follows ID.</param>
    /// <param name="image">The image dictionary, keys abbreviated or not.</param>
    /// <returns>
    /// The end of the data, the end of the EI keyword that closes the image, and whether the rules
    /// agree. When no EI follows, both ends are the end of the stream and the result is ambiguous.
    /// </returns>
    public static (int DataEnd, int KeywordEnd, bool Unambiguous) Find(ReadOnlySpan<byte> s, int dataStart, PdfDictionary image)
    {
        dataStart = Math.Min(dataStart, s.Length);
        int search = FirstKeyword(s, dataStart);
        int? declared = Declared(image, s.Length - dataStart);
        int dataEnd;
        if (declared is { } length)
        {
            dataEnd = dataStart + length;
        }
        else if (search >= 0)
        {
            dataEnd = search > dataStart ? search - 1 : search;
        }
        else
        {
            return (s.Length, s.Length, false);
        }

        // The keyword the declared length points at, after optional white space.
        int k = dataEnd;
        while (k < s.Length && PdfSyntax.IsWhitespace(s[k])) k++;
        if (!IsKeyword(s, k, dataStart)) return (dataEnd, Math.Max(dataEnd, k), false);

        bool agree = search == k && LooksLikeContent(s, k + 2);
        return (dataEnd, k + 2, agree);
    }

    /// <summary>The first EI between white space (or at the data start) and a delimiter, or -1.</summary>
    private static int FirstKeyword(ReadOnlySpan<byte> s, int start)
    {
        for (int i = start; i + 1 < s.Length; i++)
        {
            if (IsKeyword(s, i, start)) return i;
        }

        return -1;
    }

    private static bool IsKeyword(ReadOnlySpan<byte> s, int i, int dataStart) =>
        i + 1 < s.Length && s[i] == (byte)'E' && s[i + 1] == (byte)'I'
        && (i == dataStart || PdfSyntax.IsWhitespace(s[i - 1]))
        && (i + 2 == s.Length || PdfSyntax.IsWhitespace(s[i + 2]) || PdfSyntax.IsDelimiter(s[i + 2]));

    /// <summary>A /L or /Length entry, else the size of unfiltered samples, else null.</summary>
    private static int? Declared(PdfDictionary image, int available)
    {
        PdfObject? length = image["L"] ?? image["Length"];
        if (length is PdfNumber { IsInteger: true } l)
        {
            return l.IntegerValue >= 0 && l.IntegerValue <= available ? (int)l.IntegerValue : available;
        }

        if (image["F"] is not null || image["Filter"] is not null) return null;
        long? size = Size(image);
        return size is { } n && n <= available ? (int)n : null;
    }

    private static long? Size(PdfDictionary image)
    {
        if ((image["W"] ?? image["Width"]) is not PdfNumber { IsInteger: true } w) return null;
        if ((image["H"] ?? image["Height"]) is not PdfNumber { IsInteger: true } h) return null;
        if (w.IntegerValue is <= 0 or > 1 << 20 || h.IntegerValue is <= 0 or > 1 << 20) return null;
        bool mask = (image["IM"] ?? image["ImageMask"]) is PdfBoolean { Value: true };
        long bpc = mask ? 1 : (image["BPC"] ?? image["BitsPerComponent"]) is PdfNumber { IsInteger: true } b ? b.IntegerValue : 8;
        int colors = mask ? 1 : (image["CS"] ?? image["ColorSpace"]) switch
        {
            PdfName { Value: "DeviceRGB" or "RGB" or "CalRGB" or "Lab" } => 3,
            PdfName { Value: "DeviceCMYK" or "CMYK" } => 4,
            PdfName { Value: "DeviceGray" or "G" or "CalGray" } => 1,
            PdfArray a when a.Count > 0 && a[0] is PdfName { Value: "Indexed" or "I" } => 1,
            _ => -1,
        };
        if (colors < 0 || bpc is not (1 or 2 or 4 or 8 or 16)) return null;
        return ((w.IntegerValue * colors * bpc) + 7) / 8 * h.IntegerValue;
    }

    /// <summary>
    /// Readers that search for EI reject a match followed by binary data. The bytes after a
    /// good match are operators and operands, so printable ASCII or white space.
    /// </summary>
    private static bool LooksLikeContent(ReadOnlySpan<byte> s, int from)
    {
        int end = Math.Min(s.Length, from + 32);
        for (int i = from; i < end; i++)
        {
            byte b = s[i];
            if (b >= 0x7F || (b < 0x20 && !PdfSyntax.IsWhitespace(b))) return false;
        }

        return true;
    }
}
