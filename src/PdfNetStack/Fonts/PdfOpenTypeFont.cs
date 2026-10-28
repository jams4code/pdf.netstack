using System.Globalization;
using System.Text;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;

namespace PdfNetStack.Fonts;

/// <summary>
/// A TrueType or OpenType font, embedded in the document as a subset of the glyphs actually drawn.
/// Loads .ttf, .otf, collections (.ttc, .otc) and WOFF files. Text is searchable and copyable,
/// and pairs are kerned from the font's GPOS or kern table.
/// </summary>
/// <remarks>
/// The font program is embedded, so the PDF shows the same glyphs everywhere, including in PDF/A.
/// Variable fonts draw their default instance. CFF2 and WOFF2 fonts are not supported yet. Fonts
/// whose license forbids embedding (OS/2 fsType) are refused when first drawn.
/// </remarks>
public sealed class PdfOpenTypeFont : PdfFont
{
    private readonly OpenTypeFile _file;
    private readonly Kerning? _kerning;
    private readonly double _scale;
    private CffFont? _cff;
    private ShapingTables? _tables;

    private PdfOpenTypeFont(OpenTypeFile file)
    {
        _file = file;
        _kerning = Kerning.Load(file);
        _scale = 1000.0 / file.UnitsPerEm;

        // Line metrics as browsers take them: typo metrics when the font asks for them
        // (USE_TYPO_METRICS), else hhea, else the Windows metrics.
        double ascent, descent, gap;
        if (file.HasOs2 && (file.FsSelection & 0x80) != 0)
        {
            (ascent, descent, gap) = (file.TypoAscender, -file.TypoDescender, file.TypoLineGap);
        }
        else if (file.HheaAscender != 0 || file.HheaDescender != 0)
        {
            (ascent, descent, gap) = (file.HheaAscender, -file.HheaDescender, file.HheaLineGap);
        }
        else
        {
            (ascent, descent, gap) = (file.WinAscent, file.WinDescent, 0);
        }

        double em = file.UnitsPerEm;
        Ascent = ascent / em;
        Descent = descent / em;
        LineGap = Math.Max(0, gap) / em;
        CapHeight = (file.CapHeight > 0 ? file.CapHeight : TopOf('H') ?? 0.7 * em) / em;
        XHeight = (file.XHeight > 0 ? file.XHeight : TopOf('x') ?? 0.5 * em) / em;
        UnderlineThickness = (file.UnderlineThickness > 0 ? file.UnderlineThickness : em / 20) / em;

        // post gives the top of the underline; ours is its middle.
        UnderlinePosition = (file.UnderlinePosition != 0 ? -file.UnderlinePosition : em / 10) / em + UnderlineThickness / 2;
        StrikeoutThickness = (file.StrikeoutSize > 0 ? file.StrikeoutSize : file.UnderlineThickness > 0 ? file.UnderlineThickness : em / 20) / em;
        StrikeoutPosition = file.StrikeoutPosition > 0 ? file.StrikeoutPosition / em - StrikeoutThickness / 2 : XHeight / 2;
        ItalicAngle = file.ItalicAngle;
        Weight = file.WeightClass is >= 1 and <= 1000 ? file.WeightClass : (file.MacStyle & 1) != 0 ? 700 : 400;

        int? TopOf(char c) => file.TryGetGlyph(c, out int g) ? file.GlyphTop(g) : null;
    }

    /// <inheritdoc/>
    public override string Name => _file.PostScriptName;

    /// <inheritdoc/>
    public override string FamilyName => _file.FamilyName;

    /// <summary>Style name within the family, such as "Bold Italic".</summary>
    public string StyleName => _file.SubfamilyName;

    /// <summary>Weight from 1 to 1000: 400 is regular, 700 bold.</summary>
    public int Weight { get; }

    /// <inheritdoc/>
    public override bool IsBold => Weight >= 600;

    /// <inheritdoc/>
    public override bool IsItalic => (_file.FsSelection & 0x1) != 0 || (_file.MacStyle & 0x2) != 0 || _file.ItalicAngle != 0;

    /// <summary>True when all glyphs have the same advance.</summary>
    public bool IsFixedPitch => _file.IsFixedPitch;

    /// <summary>Number of glyphs in the font.</summary>
    public int GlyphCount => _file.GlyphCount;

    /// <summary>True when the font's license (OS/2 fsType) allows embedding it in a document.</summary>
    public bool AllowsEmbedding => (_file.FsType & 0x000F) != 0x0002 && (_file.FsType & 0x0200) == 0;

    internal OpenTypeFile File => _file;

    internal ShapingTables Tables => _tables ??= new ShapingTables(_file);

    internal CffFont Cff => _cff ??= new CffFont(_file.Table("CFF "));

    /// <summary>Loads a font from a file: .ttf, .otf, .ttc, .otc or .woff.</summary>
    /// <param name="path">The font file.</param>
    /// <param name="index">For a collection, which of its fonts; see <see cref="CountFonts(byte[])"/>.</param>
    /// <exception cref="FontFormatException">The file is not a supported font.</exception>
    public static PdfOpenTypeFont Load(string path, int index = 0)
    {
        Compat.ThrowIfNull(path, nameof(path));
        return Load(System.IO.File.ReadAllBytes(path), index);
    }

    /// <summary>Loads a font from a stream, read to its end.</summary>
    /// <exception cref="FontFormatException">The data is not a supported font.</exception>
    public static PdfOpenTypeFont Load(Stream stream, int index = 0)
    {
        Compat.ThrowIfNull(stream, nameof(stream));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Load(copy.ToArray(), index);
    }

    /// <summary>Loads a font from its bytes. The array is used directly; do not change it afterwards.</summary>
    /// <exception cref="FontFormatException">The data is not a supported font.</exception>
    public static PdfOpenTypeFont Load(byte[] data, int index = 0)
    {
        Compat.ThrowIfNull(data, nameof(data));
        var font = new PdfOpenTypeFont(OpenTypeFile.Parse(data, index));
        if (font._file.IsCff && font.Cff.GlyphCount != font._file.GlyphCount)
        {
            throw new FontFormatException("The CFF table and maxp disagree on the number of glyphs.");
        }

        return font;
    }

    /// <summary>Number of fonts in a file's bytes: more than one for a collection.</summary>
    public static int CountFonts(byte[] data)
    {
        Compat.ThrowIfNull(data, nameof(data));
        return OpenTypeFile.CountFonts(data);
    }

    internal override bool TryGetGlyph(int codePoint, out int glyph) => _file.TryGetGlyph(codePoint, out glyph);

    /// <summary>Shapes through GSUB and GPOS when the font has them: ligatures, Arabic forms, marks, kerning.</summary>
    internal override List<ShapedGlyph> ShapeRun(string text, int start, int end, bool rightToLeft, string script)
    {
        ShapingTables tables = Tables;
        if (tables.Gsub is null && tables.Gpos is null && tables.Gdef is null) return base.ShapeRun(text, start, end, rightToLeft, script);

        List<GlyphInfo> infos = new Shaper(tables).Shape(text, start, end, rightToLeft, script);
        var glyphs = new List<ShapedGlyph>(infos.Count);
        foreach (GlyphInfo g in infos)
        {
            // Joiners, bidi marks and other format characters steer shaping but are not drawn.
            if (g.Text.Length > 0 && g.Text.Length == (char.IsSurrogatePair(g.Text, 0) ? 2 : 1) && IsInvisible(g.Text, 0)) continue;
            glyphs.Add(new ShapedGlyph(g.Glyph, g.CodePoint, Math.Round(g.XAdvance * _scale, 2))
            {
                XOffset = g.XOffset * _scale,
                YOffset = g.YOffset * _scale,
                Text = g.Text,
                Cluster = g.Cluster,
            });
        }

        return glyphs;
    }

    internal override double GetAdvance(int glyph) => Math.Round(_file.Advance(glyph) * _scale, 2);

    internal override double GetKerning(int left, int right) =>
        _kerning is null ? 0 : Math.Round(_kerning.Get(left, right) * _scale, 2);

    internal override FontResource CreateResource(PdfDocument document)
    {
        if (!AllowsEmbedding)
        {
            throw new InvalidOperationException($"The license of {Name} does not allow embedding it (OS/2 fsType {_file.FsType:X4}).");
        }

        return new OpenTypeFontResource(document, this);
    }
}

/// <summary>
/// An OpenType font in one document: a Type 0 font with Identity-H encoding over a CIDFontType2
/// (TrueType) or CIDFontType0 (CFF) descendant. Codes are CIDs: the glyph id for TrueType and
/// name-keyed CFF, the charset's CID for CID-keyed CFF.
/// </summary>
internal sealed class OpenTypeFontResource : FontResource
{
    private readonly PdfOpenTypeFont _font;
    private readonly SortedDictionary<int, int> _cidToGlyph = new();
    private readonly Dictionary<int, int> _glyphToCid = new();
    private readonly Dictionary<int, string> _unicode = new();
    private readonly PdfDictionary _cidFont = new();
    private readonly PdfDictionary _descriptor = new();
    private readonly PdfReference _fontFile;
    private readonly PdfReference _toUnicode;

    public OpenTypeFontResource(PdfDocument document, PdfOpenTypeFont font)
        : base(document)
    {
        _font = font;
        _fontFile = document.AddObject(PdfStream.FromData([]));
        _toUnicode = document.AddObject(PdfStream.FromData([]));
        Dictionary.Set("Type", new PdfName("Font"));
        Dictionary.Set("Subtype", new PdfName("Type0"));
        Dictionary.Set("Encoding", new PdfName("Identity-H"));
        Dictionary.Set("DescendantFonts", PdfArray.Of(document.AddObject(_cidFont)));
        Dictionary.Set("ToUnicode", _toUnicode);
        _cidFont.Set("FontDescriptor", document.AddObject(_descriptor));
    }

    public override bool IsTwoByte => true;

    public override int Encode(ShapedGlyph glyph)
    {
        if (!_glyphToCid.TryGetValue(glyph.Glyph, out int cid))
        {
            cid = _font.File.IsCff ? _font.Cff.GlyphToCid[glyph.Glyph] : glyph.Glyph;
            _glyphToCid[glyph.Glyph] = cid;
            _cidToGlyph[cid] = glyph.Glyph;
        }

        // The first text drawn with a glyph is what copying gives back; a ligature maps to all its characters.
        string text = glyph.Text ?? char.ConvertFromUtf32(glyph.CodePoint);
        if (text.Length > 0 && !_unicode.ContainsKey(cid)) _unicode[cid] = text;
        return cid;
    }

    public override double Width(int glyph) => _font.GetAdvance(glyph);

    public override void Finish()
    {
        OpenTypeFile file = _font.File;
        var glyphs = new SortedSet<int>(_cidToGlyph.Values);
        bool cff = file.IsCff;
        byte[] program;
        SortedSet<int> keep;
        if (cff)
        {
            keep = new SortedSet<int>(glyphs) { 0 };
            program = _font.Cff.Subset(keep);
        }
        else
        {
            keep = TrueTypeSubsetter.Closure(file, glyphs);
            program = TrueTypeSubsetter.Subset(file, keep);
        }

        string name = SubsetTag(keep) + "+" + _font.Name;
        Dictionary.Set("BaseFont", new PdfName(name));

        _cidFont.Set("Type", new PdfName("Font"));
        _cidFont.Set("Subtype", new PdfName(cff ? "CIDFontType0" : "CIDFontType2"));
        _cidFont.Set("BaseFont", new PdfName(name));
        _cidFont.Set("CIDSystemInfo", new PdfDictionary
        {
            ["Registry"] = PdfString.FromText("Adobe"),
            ["Ordering"] = PdfString.FromText("Identity"),
            ["Supplement"] = PdfNumber.FromInteger(0),
        });
        if (!cff) _cidFont.Set("CIDToGIDMap", new PdfName("Identity"));
        _cidFont.Set("DW", PdfNumber.FromInteger(0));
        _cidFont.Set("W", Widths());

        double em = file.UnitsPerEm;
        double Scale(double v) => Math.Round(v * 1000 / em, 2);
        int flags = 4 | (_font.IsFixedPitch ? 1 : 0) | (_font.IsItalic ? 64 : 0);
        _descriptor.Set("Type", new PdfName("FontDescriptor"));
        _descriptor.Set("FontName", new PdfName(name));
        _descriptor.Set("FontFamily", PdfString.FromText(_font.FamilyName));
        _descriptor.Set("Flags", PdfNumber.FromInteger(flags));
        _descriptor.Set("FontBBox", PdfArray.Of(file.BBox.Select(v => Scale(v)).ToArray()));
        _descriptor.Set("ItalicAngle", PdfNumber.FromDouble(_font.ItalicAngle));
        _descriptor.Set("Ascent", PdfNumber.FromDouble(Math.Round(_font.Ascent * 1000, 2)));
        _descriptor.Set("Descent", PdfNumber.FromDouble(-Math.Round(_font.Descent * 1000, 2)));
        _descriptor.Set("CapHeight", PdfNumber.FromDouble(Math.Round(_font.CapHeight * 1000, 2)));
        _descriptor.Set("XHeight", PdfNumber.FromDouble(Math.Round(_font.XHeight * 1000, 2)));
        _descriptor.Set("FontWeight", PdfNumber.FromInteger(Math.Max(100, Math.Min(900, (_font.Weight + 50) / 100 * 100))));

        // No font says its stem width; this is the usual estimate from the weight class.
        _descriptor.Set("StemV", PdfNumber.FromInteger(10 + 220 * Math.Max(0, _font.Weight - 50) / 900));
        _descriptor.Set(cff ? "FontFile3" : "FontFile2", _fontFile);

        var stream = PdfStream.FromData(program);
        if (cff) stream.Dictionary.Set("Subtype", new PdfName("CIDFontType0C"));
        else stream.Dictionary.Set("Length1", PdfNumber.FromInteger(program.Length));
        Document.Objects[_fontFile.Number] = stream;
        Document.Objects[_toUnicode.Number] = PdfStream.FromData(ToUnicode());
    }

    /// <summary>The W array: runs of consecutive CIDs with their widths.</summary>
    private PdfArray Widths()
    {
        var w = new PdfArray();
        PdfArray? run = null;
        int previous = int.MinValue;
        foreach (KeyValuePair<int, int> e in _cidToGlyph)
        {
            if (e.Key != previous + 1 || run is null)
            {
                run = new PdfArray();
                w.Add(PdfNumber.FromInteger(e.Key));
                w.Add(run);
            }

            run.Add(PdfNumber.FromDouble(_font.GetAdvance(e.Value)));
            previous = e.Key;
        }

        return w;
    }

    /// <summary>A ToUnicode CMap (Adobe Technical Note 5411) from each CID to the text it was drawn for.</summary>
    private byte[] ToUnicode()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        KeyValuePair<int, string>[] entries = [.. _unicode.OrderBy(e => e.Key)];
        for (int i = 0; i < entries.Length; i += 100)
        {
            int n = Math.Min(100, entries.Length - i);
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int k = i; k < i + n; k++)
            {
                sb.Append('<').Append(entries[k].Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (char c in entries[k].Value) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }

            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Six capital letters naming the subset (ISO 32000-1, 9.6.4). Derived from the font and its
    /// glyphs, so saving the same document twice gives the same file.
    /// </summary>
    private string SubsetTag(SortedSet<int> glyphs)
    {
        var data = new List<byte>(Encoding.UTF8.GetBytes(_font.Name));
        foreach (int g in glyphs)
        {
            data.Add((byte)(g >> 8));
            data.Add((byte)g);
        }

        byte[] hash = Compat.Sha256([.. data]);
        var tag = new char[6];
        for (int i = 0; i < 6; i++) tag[i] = (char)('A' + hash[i] % 26);
        return new string(tag);
    }
}
