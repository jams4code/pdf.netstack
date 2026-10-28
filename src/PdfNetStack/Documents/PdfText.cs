using System.Globalization;
using System.Text;
using PdfNetStack.Objects;

namespace PdfNetStack.Documents;

/// <summary>Conversion between PDF text strings, dates and .NET values (ISO 32000-1, 7.9.2 and 7.9.4).</summary>
internal static class PdfText
{
    // PDFDocEncoding differs from Latin-1 in 0x18-0x1F and 0x80-0xA0.
    private static readonly char[] DocEncoding = BuildDocEncoding();

    private static char[] BuildDocEncoding()
    {
        var map = new char[256];
        for (int i = 0; i < 256; i++) map[i] = (char)i;
        "\u02D8\u02C7\u02C6\u02D9\u02DD\u02DB\u02DA\u02DC".CopyTo(0, map, 0x18, 8);
        ("\u2022\u2020\u2021\u2026\u2014\u2013\u0192\u2044\u2039\u203A\u2212\u2030\u201E\u201C\u201D\u2018"
         + "\u2019\u201A\u2122\uFB01\uFB02\u0141\u0152\u0160\u0178\u017D\u0131\u0142\u0153\u0161\u017E\uFFFD\u20AC")
            .CopyTo(0, map, 0x80, 33);
        return map;
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) chars[i] = DocEncoding[bytes[i]];
        return new string(chars);
    }

    /// <summary>Encodes as PDFDocEncoding when every character fits, otherwise as UTF-16BE with a BOM.</summary>
    public static PdfString Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int index = Array.IndexOf(DocEncoding, text[i]);
            if (index < 0 || text[i] == '�')
            {
                return new PdfString([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)]);
            }

            bytes[i] = (byte)index;
        }

        return new PdfString(bytes);
    }

    public static string? GetString(PdfDictionary dict, string key, Func<PdfObject?, PdfObject?> resolve) =>
        resolve(dict[key]) is PdfString s ? Decode(s.Value) : null;

    /// <summary>Parses D:YYYYMMDDHHmmSSOHH'mm'. Every field after the year is optional.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        int p = 0;

        int Field(int width, int fallback)
        {
            if (p + width > s.Length || !int.TryParse(s.AsSpan(p, width), NumberStyles.None, CultureInfo.InvariantCulture, out int v)) return fallback;
            p += width;
            return v;
        }

        int year = Field(4, -1);
        if (year < 1) return null;
        int month = Field(2, 1), day = Field(2, 1), hour = Field(2, 0), minute = Field(2, 0), second = Field(2, 0);
        TimeSpan offset = TimeSpan.Zero;
        if (p < s.Length && s[p] is '+' or '-')
        {
            int sign = s[p] == '-' ? -1 : 1;
            p++;
            int oh = Field(2, 0);
            if (p < s.Length && s[p] == '\'') p++;
            int om = Field(2, 0);
            offset = TimeSpan.FromMinutes(sign * (oh * 60 + om));
        }

        try
        {
            return new DateTimeOffset(year, Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 31), Math.Min(hour, 23), Math.Min(minute, 59), Math.Min(second, 59), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string FormatDate(DateTimeOffset value)
    {
        string date = value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (value.Offset == TimeSpan.Zero) return "D:" + date + "Z";
        char sign = value.Offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan o = value.Offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"D:{date}{sign}{o.Hours:D2}'{o.Minutes:D2}'");
    }
}
