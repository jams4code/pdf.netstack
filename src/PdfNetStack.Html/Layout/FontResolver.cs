using PdfNetStack.Fonts;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>The fonts for a style: the first available family, then fallbacks for characters it lacks.</summary>
internal sealed record FontSet(PdfFont Primary, IReadOnlyList<PdfFont> Fallbacks)
{
    /// <summary>
    /// The first font that can draw the character at <paramref name="index"/>, with its own glyph
    /// or after Unicode normalization, or the primary font.
    /// </summary>
    public PdfFont For(string text, int index)
    {
        if (Primary.Covers(text, index)) return Primary;
        foreach (PdfFont f in Fallbacks)
        {
            if (f.Covers(text, index)) return f;
        }

        return Primary;
    }
}

/// <summary>
/// Matches CSS font properties to fonts (CSS Fonts 4, 5): @font-face rules first, then fonts
/// the caller supplies, then installed fonts. A variable font draws at the instance its weight,
/// width, style, optical size and font-variation-settings select (CSS Fonts 4, 7.2). Generic families map to the standard 14 fonts, so
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

    // Bounds for values from CSS, wider than any font's axes: widths in percent, optical sizes in
    // CSS px, and axis values as fvar's 16.16 numbers hold them.
    private const double MaxWidth = 1000;
    private const double MaxOpticalSize = 1000;
    private const double MaxAxisValue = 32767;

    private static readonly Dictionary<string, double> WidthKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ultra-condensed"] = 50, ["extra-condensed"] = 62.5, ["condensed"] = 75, ["semi-condensed"] = 87.5, ["normal"] = 100,
        ["semi-expanded"] = 112.5, ["expanded"] = 125, ["extra-expanded"] = 150, ["ultra-expanded"] = 200,
    };

    private readonly List<Face> _faces = [];
    private readonly Dictionary<string, PdfOpenTypeFont?> _loaded = new(StringComparer.Ordinal);
    private readonly PdfFontCollection? _fonts;
    private readonly bool _useSystemFonts;
    private readonly Dictionary<Request, FontSet> _cache = [];

    // Font sizes and font-variation-settings could otherwise ask for a new instance, and an
    // embedded font program, per distinct value.
    private readonly InstanceBudget _budget = new();
    private List<PdfFont>? _systemFallbacks;

    /// <summary>
    /// An @font-face rule: the traits it matches with (its descriptors, auto counting as normal),
    /// the ranges the variations it applies are clamped to (its descriptors, or the font's own
    /// for auto), and the axis values of its font-named-instance and font-variation-settings.
    /// </summary>
    private sealed record Face(string Family, FontTraits Selection, FontTraits Applied, PdfOpenTypeFont Font, List<KeyValuePair<string, double>> Settings);

    /// <summary>What a computed style asks of its fonts; equal requests share a font set.</summary>
    private readonly record struct Request(string Families, double Weight, double Width, PdfFontStyle Style, double Angle, double? OpticalSize, string Variations);

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
            string? family = null, namedInstance = null;
            List<CssValue>? src = null;
            FontRange? weight = null, width = null;
            (FontRange? Italic, FontRange? Oblique)? style = null;
            List<KeyValuePair<string, double>>? settings = null;
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
                        weight = Range(d.Value, WeightValue);
                        break;
                    case "font-stretch" or "font-width":
                        width = Range(d.Value, WidthValue);
                        break;
                    case "font-style":
                        style = StyleDescriptor(d.Value);
                        break;
                    case "font-variation-settings":
                        settings = VariationSettings(d.Value);
                        break;
                    case "font-named-instance":
                        namedInstance = d.Value.FirstOrDefault(v => v.IsToken && v.Token.Type == CssTokenType.String)?.Token.Value;
                        break;
                }
            }

            if (family is null || src is null) continue;
            PdfOpenTypeFont? font = LoadSource(src, load);
            if (font is null) continue;

            // Auto descriptors match as normal and leave the variations to the font's own ranges.
            FontTraits own = font.Traits;
            var selection = new FontTraits(weight ?? FontTraits.Normal.Weight, width ?? FontTraits.Normal.Width,
                style is null ? FontTraits.Normal.Italic : style.Value.Italic, style is null ? FontTraits.Normal.Oblique : style.Value.Oblique);
            var applied = new FontTraits(weight ?? own.Weight, width ?? own.Width, style is null ? own.Italic : style.Value.Italic, style is null ? own.Oblique : style.Value.Oblique);

            // CSS Fonts 4, 7.2: the named instance, then the descriptor's own settings.
            var faceSettings = new List<KeyValuePair<string, double>>();
            if (namedInstance is not null && font.IsVariable && font.NamedInstanceLocations.FirstOrDefault(i => string.Equals(i.Style, namedInstance, StringComparison.OrdinalIgnoreCase)) is { Values: not null } instance)
            {
                IReadOnlyList<PdfFontAxis> axes = font.VariationAxes;
                for (int i = 0; i < axes.Count && i < instance.Values.Length; i++) faceSettings.Add(new(axes[i].Tag, instance.Values[i]));
            }

            if (settings is not null) faceSettings.AddRange(settings);
            _faces.Add(new Face(family, selection, applied, font, faceSettings));
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
                if (url is null) continue;

                // Rules that share a file share its font, and so the instances made of it.
                if (!_loaded.TryGetValue(url, out PdfOpenTypeFont? font))
                {
                    font = null;
                    if (load?.Invoke(url) is byte[] data)
                    {
                        try
                        {
                            font = PdfOpenTypeFont.Load(data);
                        }
                        catch (FontFormatException)
                        {
                            // Damaged or unsupported formats: try the next source.
                        }
                    }

                    _loaded[url] = font;
                }

                if (font is not null) return font;
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

    /// <summary>A descriptor of one or two values as a range (CSS Fonts 4, 4.4); null for auto or an invalid value, which counts as auto.</summary>
    private static FontRange? Range(List<CssValue> value, Func<CssValue, double?> parse)
    {
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        if (items.Count is 0 or > 2) return null;
        double? a = parse(items[0]), b = parse(items[^1]);
        return a is double x && b is double y ? FontRange.Of(x, y) : null;
    }

    private static double? WeightValue(CssValue v)
    {
        if (v.IsIdent("normal")) return 400;
        if (v.IsIdent("bold")) return 700;
        return Number(v) is double n && n is >= 1 and <= 1000 ? n : null;
    }

    /// <summary>A width keyword or percentage, clamped to <see cref="MaxWidth"/>; null for anything else.</summary>
    private static double? WidthValue(CssValue v)
    {
        if (v.IsToken && v.Token.Type == CssTokenType.Ident) return WidthKeywords.TryGetValue(v.Token.Value, out double w) ? w : null;
        return v.IsToken && v.Token.Type == CssTokenType.Percentage && v.Token.Number >= 0 && CssProperties.IsFinite(v.Token.Number) ? Math.Min(MaxWidth, v.Token.Number) : null;
    }

    /// <summary>
    /// The font-style descriptor as italic values and oblique angles: normal, italic (left and
    /// right too), or oblique with up to two angles (14 degrees without). Null for auto or invalid.
    /// </summary>
    private static (FontRange? Italic, FontRange? Oblique)? StyleDescriptor(List<CssValue> value)
    {
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        if (items.Count == 1 && items[0].IsIdent("normal")) return (FontRange.Of(0), FontRange.Of(0));
        if (items.Count == 1 && (items[0].IsIdent("italic") || items[0].IsIdent("left") || items[0].IsIdent("right"))) return (FontRange.Of(1), null);
        if (items.Count is < 1 or > 3 || !items[0].IsIdent("oblique")) return null;
        if (items.Count == 1) return (null, FontRange.Of(14));
        double? a = CssProperties.Angle(items[1]), b = CssProperties.Angle(items[^1]);
        return a is double x && b is double y && x is >= -90 and <= 90 && y is >= -90 and <= 90 ? (null, FontRange.Of(x, y)) : null;
    }

    /// <summary>
    /// The axis values of a font-variation-settings value, in order; null for normal or an invalid
    /// value. Values that are not finite are dropped, the rest clamped to what fvar can hold.
    /// </summary>
    private static List<KeyValuePair<string, double>>? VariationSettings(List<CssValue> value)
    {
        if (!CssProperties.VariationSettings(value) || value.Any(v => v.IsIdent("normal"))) return null;
        var settings = new List<KeyValuePair<string, double>>();
        foreach (List<CssValue> setting in Split(value))
        {
            List<CssValue> pair = setting.Where(v => !v.IsWhitespace).ToList();
            if (Number(pair[1]) is double n && CssProperties.IsFinite(n)) settings.Add(new(pair[0].Token.Value, Math.Max(-MaxAxisValue, Math.Min(MaxAxisValue, n))));
        }

        return settings;
    }

    private static double? Number(CssValue v)
    {
        if (CssGrammar.IsMath(v)) return CssMath.Evaluate(v, null, out bool isLength) is double n && !isLength ? n : null;
        return v.IsToken && v.Token.Type == CssTokenType.Number ? v.Token.Number : null;
    }

    /// <summary>Family names from a font-family value: strings, or identifiers joined by spaces.</summary>
    public static List<string> FamilyNames(List<CssValue> value) =>
        Split(value)
            .Select(part => string.Join(" ", part.Where(v => v.IsToken && v.Token.Type is CssTokenType.Ident or CssTokenType.String).Select(v => v.Token.Value)))
            .Where(name => name.Length > 0)
            .ToList();

    /// <summary>
    /// The fonts for a computed style. Values come from untrusted CSS, so each is checked before it
    /// reaches font matching: weight 1 to 1000 (else 400), width 0% to <see cref="MaxWidth"/>
    /// (else 100%), oblique angle -90 to 90 degrees, optical size above 0 up to
    /// <see cref="MaxOpticalSize"/>, and only finite axis values.
    /// </summary>
    public FontSet Resolve(ComputedStyle style)
    {
        double weight = style.Number("font-weight");
        if (!(weight is >= 1 and <= 1000)) weight = 400;
        List<CssValue> stretch = style["font-stretch"];
        double width = stretch.Count == 1 ? WidthValue(stretch[0]) ?? 100 : 100;
        (PdfFontStyle slant, double angle) = Style(style["font-style"]);
        double? opticalSize = style.Keyword("font-optical-sizing") == "auto" && style.FontSize > 0 && CssProperties.IsFinite(style.FontSize)
            ? Math.Min(MaxOpticalSize, style.FontSize)
            : null;
        List<KeyValuePair<string, double>>? settings = VariationSettings(style["font-variation-settings"]);
        string families = string.Join(",", FamilyNames(style["font-family"]));
        var request = new Request(families, weight, width, slant, angle, opticalSize,
            settings is null ? "" : string.Join(",", settings.Select(s => s.Key + "=" + s.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        if (_cache.TryGetValue(request, out FontSet? cached)) return cached;

        Dictionary<string, double>? variations = null;
        if (settings is not null)
        {
            // A tag given twice: the last value wins.
            variations = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> s in settings) variations[s.Key] = s.Value;
        }

        PdfFontQuery query;
        try
        {
            query = new PdfFontQuery { Weight = weight, Width = width, Style = slant, ObliqueAngle = angle, OpticalSize = opticalSize, Variations = variations };
        }
        catch (ArgumentException)
        {
            query = new PdfFontQuery();
        }

        int standardWeight = (int)Math.Round(weight);
        bool italic = slant != PdfFontStyle.Normal;
        var found = new List<PdfFont>();
        PdfFont? generic = null;
        foreach (string family in families.Split(','))
        {
            if (family.Length == 0) continue;
            if (Generic(family, standardWeight, italic) is PdfFont standard)
            {
                generic ??= standard;
                found.Add(standard);
                continue;
            }

            PdfFont? font = FindSafely(family, query) ?? Alias(family, standardWeight, italic);
            if (font is not null && !found.Contains(font)) found.Add(font);
        }

        // The default family is serif, as in browsers.
        PdfFont fallback = generic ?? Generic("serif", standardWeight, italic)!;
        if (!found.Contains(fallback)) found.Add(fallback);
        foreach (PdfFont f in SystemFallbackFonts())
        {
            if (!found.Contains(f)) found.Add(f);
        }

        var set = new FontSet(found[0], found.Skip(1).ToList());
        _cache[request] = set;
        return set;
    }

    /// <summary>The computed font-style as a style and an oblique angle in degrees.</summary>
    private static (PdfFontStyle Style, double Angle) Style(List<CssValue> value)
    {
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        if (items.Count == 0 || items[0].IsIdent("normal")) return (PdfFontStyle.Normal, 14);
        if (items[0].IsIdent("italic")) return (PdfFontStyle.Italic, 14);
        double angle = items.Count > 1 ? CssProperties.Angle(items[1]) ?? 14 : 14;
        return (PdfFontStyle.Oblique, Math.Max(-90, Math.Min(90, angle)));
    }

    /// <summary>
    /// <see cref="Find"/>, and should a style value still upset font matching, the family's font
    /// for a plain query instead, else none: a style value never stops the conversion.
    /// </summary>
    private PdfFont? FindSafely(string family, PdfFontQuery query)
    {
        foreach (PdfFontQuery q in new[] { query, new PdfFontQuery() })
        {
            try
            {
                return Find(family, q);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or ArithmeticException or FontFormatException)
            {
                // Try the plain query, then give up on the family.
            }
        }

        return null;
    }

    private PdfFont? Find(string family, PdfFontQuery query)
    {
        // @font-face first: the best match by its descriptors, the last rule among equals (CSS Fonts 4, 4.1).
        List<Face> faces = FontMatcher.Select(_faces.Where(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)), f => f.Selection, query);
        if (faces.Count > 0)
        {
            Face face = faces[^1];
            return FontMatcher.Instance(face.Font, face.Applied, query, face.Font.Location, face.Settings, _budget);
        }

        return _fonts?.Find(family, query, _budget) ?? (_useSystemFonts ? PdfFontCollection.System.Find(family, query, _budget) : null);
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
