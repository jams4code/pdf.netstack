using PdfNetStack.Fonts;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>The fonts for a style: the first available family, then fallbacks for characters it lacks.</summary>
internal sealed record FontSet(PdfFont Primary, IReadOnlyList<PdfFont> Fallbacks)
{
    /// <summary>The first font with a glyph for the code point, or the primary font.</summary>
    public PdfFont For(int codePoint)
    {
        if (Primary.HasGlyph(codePoint)) return Primary;
        foreach (PdfFont f in Fallbacks)
        {
            if (f.HasGlyph(codePoint)) return f;
        }

        return Primary;
    }
}

/// <summary>
/// Matches CSS font properties to fonts (CSS Fonts 4, 5): @font-face rules first, then fonts
/// the caller supplies, then installed fonts. Generic families map to the standard 14 fonts, so
/// documents that only ask for serif or sans-serif need no embedded font. Installed fonts with
/// wide coverage back up characters the chosen fonts lack, such as Arabic or symbols.
/// </summary>
internal sealed class FontResolver
{
    // Installed families tried, in order, for characters no requested font has.
    private static readonly string[] SystemFallbacks =
    [
        "Segoe UI", "Arial", "Tahoma", "Noto Sans", "Noto Naskh Arabic", "Noto Sans Arabic", "DejaVu Sans", "Liberation Sans",
        "Geeza Pro", "Arial Unicode MS", "Segoe UI Symbol", "Noto Sans Symbols", "Noto Sans Symbols 2", "Microsoft YaHei",
        "Noto Sans CJK SC", "PingFang SC", "Segoe UI Emoji", "Noto Color Emoji",
    ];

    private readonly List<Face> _faces = [];
    private readonly PdfFontCollection? _fonts;
    private readonly bool _useSystemFonts;
    private readonly Dictionary<(string Families, int Weight, bool Italic), FontSet> _cache = [];
    private List<PdfFont>? _systemFallbacks;

    private sealed record Face(string Family, int MinWeight, int MaxWeight, bool Italic, PdfOpenTypeFont Font);

    public FontResolver(PdfFontCollection? fonts, bool useSystemFonts)
    {
        _fonts = fonts;
        _useSystemFonts = useSystemFonts;
    }

    /// <summary>Loads @font-face rules; <paramref name="load"/> returns the bytes behind a URL. Faces that fail to load are skipped.</summary>
    public void AddFontFaces(IEnumerable<CssFontFaceRule> rules, Func<string, byte[]?>? load)
    {
        foreach (CssFontFaceRule rule in rules)
        {
            string? family = null;
            List<CssValue>? src = null;
            int min = 400, max = 400;
            bool italic = false;
            foreach (CssDeclaration d in rule.Declarations)
            {
                switch (d.Property)
                {
                    case "font-family":
                        family = FamilyNames(d.Value).FirstOrDefault();
                        break;
                    case "src":
                        src = d.Value;
                        break;
                    case "font-weight":
                        (min, max) = WeightRange(d.Value);
                        break;
                    case "font-style":
                        italic = d.Value.Any(v => v.IsIdent("italic") || v.IsIdent("oblique"));
                        break;
                }
            }

            if (family is null || src is null) continue;
            PdfOpenTypeFont? font = LoadSource(src, load);
            if (font is not null) _faces.Add(new Face(family, min, max, italic, font));
        }
    }

    private PdfOpenTypeFont? LoadSource(List<CssValue> src, Func<string, byte[]?>? load)
    {
        // A comma separated list: the first source that loads wins.
        foreach (List<CssValue> source in Split(src))
        {
            foreach (CssValue v in source)
            {
                if (v.Function == "local")
                {
                    string name = string.Join(" ", v.Children.Where(c => c.IsToken && c.Token.Type is CssTokenType.Ident or CssTokenType.String).Select(c => c.Token.Value));
                    PdfOpenTypeFont? local = _fonts?.Find(name) ?? (_useSystemFonts ? PdfFontCollection.System.Find(name) : null);
                    if (local is not null) return local;
                }

                string? url = v.IsToken && v.Token.Type == CssTokenType.Url ? v.Token.Value
                    : v.Function == "url" ? v.Children.FirstOrDefault(c => c.IsToken && c.Token.Type == CssTokenType.String)?.Token.Value
                    : null;
                if (url is null || load?.Invoke(url) is not byte[] data) continue;
                try
                {
                    return PdfOpenTypeFont.Load(data);
                }
                catch (FontFormatException)
                {
                    // WOFF2 and other unsupported formats: try the next source.
                }
            }
        }

        return null;
    }

    private static IEnumerable<List<CssValue>> Split(List<CssValue> value)
    {
        var part = new List<CssValue>();
        foreach (CssValue v in value)
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Comma)
            {
                yield return part;
                part = [];
            }
            else
            {
                part.Add(v);
            }
        }

        yield return part;
    }

    private static (int Min, int Max) WeightRange(List<CssValue> value)
    {
        var weights = value.Where(v => !v.IsWhitespace).Select(v =>
            v.IsIdent("bold") ? 700 : v.IsToken && v.Token.Type == CssTokenType.Number ? (int)Math.Min(1000, Math.Max(1, v.Token.Number)) : 400).ToList();
        return weights.Count == 0 ? (400, 400) : (weights.Min(), weights.Max());
    }

    /// <summary>Family names from a font-family value: strings, or identifiers joined by spaces.</summary>
    public static List<string> FamilyNames(List<CssValue> value) =>
        Split(value)
            .Select(part => string.Join(" ", part.Where(v => v.IsToken && v.Token.Type is CssTokenType.Ident or CssTokenType.String).Select(v => v.Token.Value)))
            .Where(name => name.Length > 0)
            .ToList();

    /// <summary>The fonts for a computed style.</summary>
    public FontSet Resolve(ComputedStyle style)
    {
        int weight = (int)Math.Round(style.Number("font-weight"));
        if (weight is < 1 or > 1000) weight = 400;
        bool italic = style.Keyword("font-style") is "italic" or "oblique";
        string families = string.Join(",", FamilyNames(style["font-family"]));
        if (_cache.TryGetValue((families, weight, italic), out FontSet? cached)) return cached;

        var found = new List<PdfFont>();
        PdfFont? generic = null;
        foreach (string family in families.Split(','))
        {
            if (family.Length == 0) continue;
            if (Generic(family, weight, italic) is PdfFont standard)
            {
                generic ??= standard;
                found.Add(standard);
                continue;
            }

            PdfFont? font = Find(family, weight, italic) ?? Alias(family, weight, italic);
            if (font is not null && !found.Contains(font)) found.Add(font);
        }

        // The default family is serif, as in browsers.
        PdfFont fallback = generic ?? Generic("serif", weight, italic)!;
        if (!found.Contains(fallback)) found.Add(fallback);
        foreach (PdfFont f in SystemFallbackFonts())
        {
            if (!found.Contains(f)) found.Add(f);
        }

        var set = new FontSet(found[0], found.Skip(1).ToList());
        _cache[(families, weight, italic)] = set;
        return set;
    }

    private PdfFont? Find(string family, int weight, bool italic)
    {
        // @font-face: same style first, then the weight closest per CSS (a weight inside a variable range matches exactly).
        Face? face = _faces
            .Where(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Italic == italic ? 0 : 1)
            .ThenBy(f => weight >= f.MinWeight && weight <= f.MaxWeight ? 0 : 1 + PdfFontCollection.WeightRank(weight, weight < f.MinWeight ? f.MinWeight : f.MaxWeight))
            .FirstOrDefault();
        if (face is not null) return face.Font;
        return _fonts?.Find(family, weight, italic) ?? (_useSystemFonts ? PdfFontCollection.System.Find(family, weight, italic) : null);
    }

    /// <summary>Generic families (CSS Fonts 4, 4.2) as standard 14 fonts.</summary>
    private static PdfFont? Generic(string family, int weight, bool italic) => family.ToLowerInvariant() switch
    {
        "serif" or "ui-serif" or "cursive" or "fantasy" or "math" => Times(weight, italic),
        "sans-serif" or "system-ui" or "ui-sans-serif" or "ui-rounded" or "-apple-system" or "blinkmacsystemfont" => Helvetica(weight, italic),
        "monospace" or "ui-monospace" => Courier(weight, italic),
        _ => null,
    };

    /// <summary>Common families that are not installed, replaced by the standard font with the same metrics.</summary>
    private static PdfFont? Alias(string family, int weight, bool italic) => family.ToLowerInvariant() switch
    {
        "arial" or "helvetica" or "helvetica neue" or "liberation sans" or "arimo" or "segoe ui" or "roboto" or "inter" or "open sans" => Helvetica(weight, italic),
        "times" or "times new roman" or "liberation serif" or "tinos" or "georgia" => Times(weight, italic),
        "courier" or "courier new" or "liberation mono" or "cousine" or "consolas" or "menlo" or "monaco" or "sfmono-regular" => Courier(weight, italic),
        _ => null,
    };

    private static PdfFont Times(int weight, bool italic) => weight >= 600
        ? (italic ? PdfStandardFont.TimesBoldItalic : PdfStandardFont.TimesBold)
        : (italic ? PdfStandardFont.TimesItalic : PdfStandardFont.TimesRoman);

    private static PdfFont Helvetica(int weight, bool italic) => weight >= 600
        ? (italic ? PdfStandardFont.HelveticaBoldOblique : PdfStandardFont.HelveticaBold)
        : (italic ? PdfStandardFont.HelveticaOblique : PdfStandardFont.Helvetica);

    private static PdfFont Courier(int weight, bool italic) => weight >= 600
        ? (italic ? PdfStandardFont.CourierBoldOblique : PdfStandardFont.CourierBold)
        : (italic ? PdfStandardFont.CourierOblique : PdfStandardFont.Courier);

    /// <summary>Installed fonts with wide coverage, regular style: they only stand in for missing characters.</summary>
    private List<PdfFont> SystemFallbackFonts()
    {
        if (!_useSystemFonts) return [];
        if (_systemFallbacks is not null) return _systemFallbacks;
        var fonts = new List<PdfFont>();
        foreach (string name in SystemFallbacks)
        {
            if (PdfFontCollection.System.Find(name) is PdfFont f) fonts.Add(f);
        }

        return _systemFallbacks = fonts;
    }
}
