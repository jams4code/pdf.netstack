using System.Globalization;
using System.Text;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Glyph names to Unicode, as the Adobe Glyph List specification maps them: the AGL itself,
/// uniXXXX and uXXXX[XX] names, ligatures joined with underscores, and suffixes after a period.
/// </summary>
internal static partial class GlyphList
{
    private static Dictionary<string, string>? s_agl;
    private static Dictionary<string, string>? s_zapf;

    private static Dictionary<string, string> Agl => LazyInitializer.EnsureInitialized(ref s_agl, static () => Load(AglData))!;

    private static Dictionary<string, string> Zapf => LazyInitializer.EnsureInitialized(ref s_zapf, static () => Load(ZapfDingbatsData))!;

    /// <summary>
    /// The text a glyph name stands for, or null. <paramref name="dingbats"/> looks the ITC Zapf
    /// Dingbats names (a1 to a191) up first, for that font.
    /// </summary>
    public static string? ToUnicode(string? name, bool dingbats = false)
    {
        if (string.IsNullOrEmpty(name)) return null;

        // Everything after the first period is a variant suffix: "a.sc" is "a".
        int dot = name!.IndexOf('.');
        if (dot == 0) return null;
        if (dot > 0) name = name[..dot];
        if (dingbats && Zapf.TryGetValue(name, out string? d)) return d;
        if (Agl.TryGetValue(name, out string? text)) return text;
        if (name.IndexOf('_') < 0) return Component(name) ?? (dingbats ? null : Zapf.GetValueOrDefault(name));

        // A ligature names its components: f_f_i.
        var sb = new StringBuilder();
        foreach (string part in name.Split('_'))
        {
            string? c = part.Length == 0 ? null : Agl.GetValueOrDefault(part) ?? Component(part);
            if (c is null) return null;
            sb.Append(c);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Producers that name glyphs by number: Gxx and gxxxx in hex, Cdd and cdd in decimal, cidN and
    /// gN in decimal. The number is usually the character code; null when the name is not one of these.
    /// </summary>
    public static int? NumericName(string? name)
    {
        if (name is null || name.Length < 2) return null;
        string digits;
        bool hex;
        if (name.StartsWith("cid", StringComparison.Ordinal))
        {
            (digits, hex) = (name[3..], false);
        }
        else if (name[0] is 'G' && name.Length == 3)
        {
            (digits, hex) = (name[1..], true);
        }
        else if (name[0] is 'g' && name.Length == 5)
        {
            (digits, hex) = (name[1..], true);
        }
        else if (name[0] is 'C' or 'c' or 'g' && name.Length is >= 3 and <= 4)
        {
            (digits, hex) = (name[1..], false);
        }
        else
        {
            return null;
        }

        if (digits.Length is 0 or > 6) return null;
        NumberStyles style = hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
        return int.TryParse(digits, style, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    /// <summary>uniXXXX (one or more groups of four hex digits) and uXXXX to uXXXXXX names.</summary>
    private static string? Component(string name)
    {
        if (name.Length >= 7 && (name.Length - 3) % 4 == 0 && name.StartsWith("uni", StringComparison.Ordinal))
        {
            var sb = new StringBuilder((name.Length - 3) / 4);
            for (int i = 3; i < name.Length; i += 4)
            {
                if (Hex(name, i, 4) is not int unit || unit is >= 0xD800 and <= 0xDFFF) return null;
                sb.Append((char)unit);
            }

            return sb.ToString();
        }

        if (name.Length is >= 5 and <= 7 && name[0] == 'u' && Hex(name, 1, name.Length - 1) is int scalar
            && scalar <= 0x10FFFF && scalar is not (>= 0xD800 and <= 0xDFFF))
        {
            return char.ConvertFromUtf32(scalar);
        }

        return null;
    }

    private static int? Hex(string s, int start, int length)
    {
        int value = 0;
        for (int i = start; i < start + length; i++)
        {
            char c = s[i];
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'F' => c - 'A' + 10,
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => -1,
            };
            if (digit < 0) return null;
            value = (value << 4) | digit;
        }

        return value;
    }

    private static Dictionary<string, string> Load(ReadOnlySpan<byte> data)
    {
        var map = new Dictionary<string, string>(4400, StringComparer.Ordinal);
        while (data.Length > 0)
        {
            int end = data.IndexOf((byte)'\n');
            if (end < 0) end = data.Length;
            ReadOnlySpan<byte> line = data[..end];
            data = end < data.Length ? data[(end + 1)..] : default;
            int space = line.IndexOf((byte)' ');
            if (space <= 0) continue;
            string name = Encoding.ASCII.GetString(line[..space].ToArray());
            var text = new StringBuilder(1);
            for (int i = space + 1; i + 4 <= line.Length; i += 5)
            {
                int unit = 0;
                for (int k = i; k < i + 4; k++) unit = (unit << 4) | (line[k] <= '9' ? line[k] - '0' : line[k] - 'A' + 10);
                text.Append((char)unit);
            }

            map[name] = text.ToString();
        }

        return map;
    }
}
