using PdfNetStack.Objects;

namespace PdfNetStack.Fonts;

/// <summary>
/// One of the 14 standard PDF fonts (ISO 32000-1, 9.6.2.2). Viewers supply them, so nothing is
/// embedded and files stay small. They cover Western and Central European Latin text, plus the
/// Symbol and ZapfDingbats sets; for anything else, or for PDF/A, embed a font.
/// </summary>
/// <remarks>
/// Latin text uses WinAnsiEncoding (Windows-1252). Up to 37 other characters the font has, such
/// as Ł, ő or −, get codes of their own per document. Standard fonts are not kerned. Line
/// spacing follows the metric compatible fonts viewers substitute (Liberation Sans, Serif and
/// Mono), which matches how browsers lay out the same families.
/// </remarks>
public sealed class PdfStandardFont : PdfFont
{
    private static Dictionary<int, int>? s_latinGlyphs;
    private readonly StandardFontInfo _info;
    private readonly Dictionary<int, int>? _symbolGlyphs;

    private PdfStandardFont(StandardFontInfo info, double ascent, double descent, double lineGap)
    {
        _info = info;
        Ascent = ascent;
        Descent = descent;
        LineGap = lineGap;
        CapHeight = info.CapHeight / 1000.0;
        XHeight = info.XHeight / 1000.0;
        UnderlinePosition = -info.UnderlinePosition / 1000.0;
        UnderlineThickness = info.UnderlineThickness / 1000.0;

        // AFM files have no strikeout metrics; through the middle of the lowercase letters, as browsers do.
        StrikeoutPosition = XHeight > 0 ? XHeight / 2 : CapHeight / 3;
        StrikeoutThickness = UnderlineThickness;
        ItalicAngle = info.ItalicAngle;
        if (info.Codes is not null)
        {
            _symbolGlyphs = new Dictionary<int, int>(info.Codes.Length);
            for (int i = 0; i < info.Codes.Length; i++) _symbolGlyphs[info.Unicode![i]] = i;
        }
    }

    /// <summary>Helvetica, a sans serif. Viewers usually show Arial or Liberation Sans.</summary>
    public static PdfStandardFont Helvetica { get; } = Create(0);

    /// <summary>Helvetica Bold.</summary>
    public static PdfStandardFont HelveticaBold { get; } = Create(1);

    /// <summary>Helvetica Oblique.</summary>
    public static PdfStandardFont HelveticaOblique { get; } = Create(2);

    /// <summary>Helvetica Bold Oblique.</summary>
    public static PdfStandardFont HelveticaBoldOblique { get; } = Create(3);

    /// <summary>Times Roman, a serif. Viewers usually show Times New Roman or Liberation Serif.</summary>
    public static PdfStandardFont TimesRoman { get; } = Create(4);

    /// <summary>Times Bold.</summary>
    public static PdfStandardFont TimesBold { get; } = Create(5);

    /// <summary>Times Italic.</summary>
    public static PdfStandardFont TimesItalic { get; } = Create(6);

    /// <summary>Times Bold Italic.</summary>
    public static PdfStandardFont TimesBoldItalic { get; } = Create(7);

    /// <summary>Courier, a monospace. Viewers usually show Courier New or Liberation Mono.</summary>
    public static PdfStandardFont Courier { get; } = Create(8);

    /// <summary>Courier Bold.</summary>
    public static PdfStandardFont CourierBold { get; } = Create(9);

    /// <summary>Courier Oblique.</summary>
    public static PdfStandardFont CourierOblique { get; } = Create(10);

    /// <summary>Courier Bold Oblique.</summary>
    public static PdfStandardFont CourierBoldOblique { get; } = Create(11);

    /// <summary>Symbol: Greek letters and mathematical signs, drawn from their Unicode characters.</summary>
    public static PdfStandardFont Symbol { get; } = Create(12);

    /// <summary>ZapfDingbats: the Unicode Dingbats block (✓, ✗, ❤, ➔ and more), drawn from their Unicode characters.</summary>
    public static PdfStandardFont ZapfDingbats { get; } = Create(13);

    /// <summary>All 14 fonts.</summary>
    public static IReadOnlyList<PdfStandardFont> All { get; } =
    [
        Helvetica, HelveticaBold, HelveticaOblique, HelveticaBoldOblique,
        TimesRoman, TimesBold, TimesItalic, TimesBoldItalic,
        Courier, CourierBold, CourierOblique, CourierBoldOblique,
        Symbol, ZapfDingbats,
    ];

    /// <inheritdoc/>
    public override string Name => _info.Name;

    /// <inheritdoc/>
    public override string FamilyName => _info.FamilyName;

    /// <inheritdoc/>
    public override bool IsBold => _info.Bold;

    /// <inheritdoc/>
    public override bool IsItalic => _info.ItalicAngle != 0;

    /// <summary>True for Courier, whose glyphs all have the same width.</summary>
    public bool IsFixedPitch => _info.FixedPitch;

    internal bool IsSymbolic => _symbolGlyphs is not null;

    internal StandardFontInfo Info => _info;

    /// <summary>The standard font with this PostScript name, such as "Helvetica-Bold", or null.</summary>
    public static PdfStandardFont? FromName(string name)
    {
        Compat.ThrowIfNull(name, nameof(name));
        foreach (PdfStandardFont f in All)
        {
            if (string.Equals(f.Name, name, StringComparison.Ordinal)) return f;
        }

        return null;
    }

    internal override bool TryGetGlyph(int codePoint, out int glyph) =>
        (_symbolGlyphs ?? LatinGlyphs).TryGetValue(codePoint, out glyph);

    internal override double GetAdvance(int glyph) => _info.Widths[glyph];

    internal override FontResource CreateResource(PdfDocument document) => new StandardFontResource(document, this);

    private static Dictionary<int, int> LatinGlyphs => LazyInitializer.EnsureInitialized(ref s_latinGlyphs, static () =>
    {
        int[] unicode = StandardFontData.LatinUnicode;
        var map = new Dictionary<int, int>(unicode.Length + 8);
        for (int i = 0; i < unicode.Length; i++) map[unicode[i]] = i;
        int[] aliases = StandardFontData.LatinAliases;
        for (int i = 0; i < aliases.Length; i += 2) map[aliases[i]] = aliases[i + 1];
        return map;
    })!;

    private static PdfStandardFont Create(int index)
    {
        StandardFontInfo info = StandardFontData.Fonts[index];

        // Line metrics of Liberation Sans, Serif and Mono (hhea, 2048 units scaled to 1000).
        return info.FamilyName switch
        {
            "Helvetica" => new PdfStandardFont(info, 0.905, 0.212, 0.033),
            "Times" => new PdfStandardFont(info, 0.891, 0.216, 0.042),
            "Courier" => new PdfStandardFont(info, 0.833, 0.300, 0),
            _ => new PdfStandardFont(info, info.BBox[3] / 1000.0, -info.BBox[1] / 1000.0, 0),
        };
    }
}

/// <summary>Metrics of one standard font, from <see cref="StandardFontData"/>.</summary>
internal sealed class StandardFontInfo(
    string name, string familyName, bool bold, double italicAngle, bool fixedPitch, int[] bbox,
    int capHeight, int xHeight, int ascender, int descender, int underlinePosition, int underlineThickness,
    int stemV, ushort[] widths, byte[]? codes = null, int[]? unicode = null)
{
    public string Name { get; } = name;

    public string FamilyName { get; } = familyName;

    public bool Bold { get; } = bold;

    public double ItalicAngle { get; } = italicAngle;

    public bool FixedPitch { get; } = fixedPitch;

    public int[] BBox { get; } = bbox;

    public int CapHeight { get; } = capHeight;

    public int XHeight { get; } = xHeight;

    public int Ascender { get; } = ascender;

    public int Descender { get; } = descender;

    public int UnderlinePosition { get; } = underlinePosition;

    public int UnderlineThickness { get; } = underlineThickness;

    public int StemV { get; } = stemV;

    /// <summary>Advance widths by glyph: Latin glyph index, or index into <see cref="Codes"/>.</summary>
    public ushort[] Widths { get; } = widths;

    /// <summary>Built-in encoding of Symbol and ZapfDingbats: the code of each glyph. Null for Latin fonts.</summary>
    public byte[]? Codes { get; } = codes;

    /// <summary>Unicode code point of each glyph of <see cref="Codes"/>.</summary>
    public int[]? Unicode { get; } = unicode;
}

/// <summary>
/// A standard font in one document. Latin glyphs use their WinAnsiEncoding code when they have
/// one; others take a free code and are named in a /Differences array.
/// </summary>
internal sealed class StandardFontResource : FontResource
{
    // Codes WinAnsiEncoding leaves undefined, high ones first; 1 to 31 last.
    private static readonly byte[] FreeCodes = BuildFreeCodes();

    private readonly PdfStandardFont _font;
    private readonly Dictionary<int, int> _codes = new();
    private readonly SortedDictionary<int, int> _glyphsByCode = new();
    private readonly SortedDictionary<int, string> _differences = new();
    private int _nextFree;

    public StandardFontResource(PdfDocument document, PdfStandardFont font)
        : base(document)
    {
        _font = font;
        Dictionary.Set("Type", new PdfName("Font"));
        Dictionary.Set("Subtype", new PdfName("Type1"));
        Dictionary.Set("BaseFont", new PdfName(font.Name));
    }

    public override bool IsTwoByte => false;

    public override int Encode(ShapedGlyph glyph)
    {
        if (_codes.TryGetValue(glyph.Glyph, out int code)) return code;
        StandardFontInfo info = _font.Info;
        if (info.Codes is not null)
        {
            code = info.Codes[glyph.Glyph];
        }
        else
        {
            code = WinAnsiCode(StandardFontData.LatinUnicode[glyph.Glyph]);
            if (code < 0)
            {
                if (_nextFree == FreeCodes.Length)
                {
                    throw new InvalidOperationException(
                        $"{_font.Name} can show at most {FreeCodes.Length} characters outside Windows-1252 per document. Embed a font instead.");
                }

                code = FreeCodes[_nextFree++];
                _differences[code] = StandardFontData.LatinNames[glyph.Glyph];
            }
        }

        _codes[glyph.Glyph] = code;
        _glyphsByCode[code] = glyph.Glyph;
        return code;
    }

    public override double Width(int glyph) => _font.Info.Widths[glyph];

    public override void Finish()
    {
        if (_glyphsByCode.Count == 0) return;
        int first = _glyphsByCode.Keys.First(), last = _glyphsByCode.Keys.Last();
        var widths = new PdfArray();
        for (int code = first; code <= last; code++)
        {
            widths.Add(PdfNumber.FromInteger(_glyphsByCode.TryGetValue(code, out int g) ? _font.Info.Widths[g] : 0));
        }

        Dictionary.Set("FirstChar", PdfNumber.FromInteger(first));
        Dictionary.Set("LastChar", PdfNumber.FromInteger(last));
        Dictionary.Set("Widths", widths);
        if (_font.IsSymbolic) return;

        if (_differences.Count == 0)
        {
            Dictionary.Set("Encoding", new PdfName("WinAnsiEncoding"));
            return;
        }

        var differences = new PdfArray();
        foreach (KeyValuePair<int, string> d in _differences)
        {
            differences.Add(PdfNumber.FromInteger(d.Key));
            differences.Add(new PdfName(d.Value));
        }

        Dictionary.Set("Encoding", new PdfDictionary
        {
            ["Type"] = new PdfName("Encoding"),
            ["BaseEncoding"] = new PdfName("WinAnsiEncoding"),
            ["Differences"] = differences,
        });
    }

    /// <summary>The WinAnsiEncoding code of a Unicode code point (ISO 32000-1, D.2), or -1.</summary>
    internal static int WinAnsiCode(int unicode) => unicode switch
    {
        (>= 0x20 and <= 0x7E) or (>= 0xA1 and <= 0xFF) => unicode,
        0x20AC => 0x80,
        0x201A => 0x82,
        0x0192 => 0x83,
        0x201E => 0x84,
        0x2026 => 0x85,
        0x2020 => 0x86,
        0x2021 => 0x87,
        0x02C6 => 0x88,
        0x2030 => 0x89,
        0x0160 => 0x8A,
        0x2039 => 0x8B,
        0x0152 => 0x8C,
        0x017D => 0x8E,
        0x2018 => 0x91,
        0x2019 => 0x92,
        0x201C => 0x93,
        0x201D => 0x94,
        0x2022 => 0x95,
        0x2013 => 0x96,
        0x2014 => 0x97,
        0x02DC => 0x98,
        0x2122 => 0x99,
        0x0161 => 0x9A,
        0x203A => 0x9B,
        0x0153 => 0x9C,
        0x017E => 0x9E,
        0x0178 => 0x9F,
        _ => -1,
    };

    private static byte[] BuildFreeCodes()
    {
        var codes = new List<byte> { 0x7F, 0x81, 0x8D, 0x8F, 0x90, 0x9D };
        for (int c = 1; c < 0x20; c++) codes.Add((byte)c);
        return [.. codes];
    }
}
