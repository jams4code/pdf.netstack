using System.Text;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// A font dictionary as text extraction reads it (ISO 32000-1, 9.5 to 9.10): how a string splits
/// into codes, the text and the advance of each code, and the vertical extent of its glyphs.
/// Distances are thousandths of text space, as in /Widths: 1000 is the font size.
/// </summary>
/// <remarks>
/// Fonts come from untrusted files. Loading never throws: a damaged entry falls back to the
/// encoding's defaults, a damaged font to U+FFFD and default widths.
/// </remarks>
internal sealed class TextFont
{
    /// <summary>Numbers a /W or /W2 array may hold.</summary>
    private const int MaxMetrics = 1 << 18;

    /// <summary>Glyph ids a CIDToGIDMap stream may map.</summary>
    private const int MaxCidToGid = 1 << 16;

    // Simple fonts: text and width per code, worked out once.
    private readonly string?[]? _text;
    private readonly double[]? _widths;

    // Composite fonts.
    private readonly CMap? _encoding;
    private readonly CMap? _toUnicode;
    private readonly bool _identityUnicode;
    private readonly CidMetrics? _w;
    private readonly CidMetrics? _w2;
    private readonly double _dw = 1000;
    private readonly double _dw2Y = 880;
    private readonly double _dw2W1 = -1000;
    private readonly Func<int, string?>? _glyphText;
    private readonly string? _ordering;
    private PredefinedCMaps.CidText? _collectionText;
    private bool _collectionTextLoaded;

    private TextFont(string name)
    {
        Name = name;
        Ascent = 750;
        Descent = -250;
    }

    private TextFont(string name, string?[] text, double[] widths, double ascent, double descent)
    {
        Name = name;
        _text = text;
        _widths = widths;
        Ascent = ascent;
        Descent = descent;
    }

    private TextFont(string name, CMap? encoding, bool vertical, CMap? toUnicode, bool identityUnicode, CidMetrics? w, double dw, CidMetrics? w2, double[]? dw2, Func<int, string?>? glyphText, string? ordering, double ascent, double descent)
    {
        Name = name;
        IsComposite = true;
        _encoding = encoding;
        _toUnicode = toUnicode;
        _identityUnicode = identityUnicode;
        _w = w;
        _dw = dw;
        _w2 = w2;
        if (dw2 is { Length: 2 }) (_dw2Y, _dw2W1) = (dw2[0], dw2[1]);
        _glyphText = glyphText;
        _ordering = ordering;
        IsVertical = vertical;
        Ascent = ascent;
        Descent = descent;
    }

    /// <summary>The PostScript name without a subset tag, such as "Helvetica".</summary>
    public string Name { get; }

    /// <summary>True for Type 0 fonts, whose codes a CMap splits.</summary>
    public bool IsComposite { get; }

    /// <summary>True for vertical writing (WMode 1).</summary>
    public bool IsVertical { get; }

    /// <summary>Height above the baseline glyph boxes reach, positive.</summary>
    public double Ascent { get; }

    /// <summary>Depth below the baseline glyph boxes reach, zero or negative.</summary>
    public double Descent { get; }

    /// <summary>A font for a name the resources do not define: Helvetica's metrics and StandardEncoding.</summary>
    public static TextFont Missing(string name)
    {
        var text = new string?[256];
        var widths = new double[256];
        Simple(text, widths, FontEncodings.StandardEncoding, null, PdfStandardFont.Helvetica);
        return new TextFont(name, text, widths, 718, -207);
    }

    /// <summary>Reads the code at <paramref name="at"/>; returns its length in bytes, at least 1.</summary>
    public int NextCode(ReadOnlySpan<byte> s, int at, out uint code)
    {
        if (!IsComposite)
        {
            code = s[at];
            return 1;
        }

        CMap? splitter = _encoding is { HasCodespace: true } ? _encoding : _toUnicode is { HasCodespace: true } ? _toUnicode : null;
        if (splitter is not null) return splitter.NextCode(s, at, out code);

        // No code space anywhere: two bytes, as Identity-H.
        int length = Math.Min(2, s.Length - at);
        code = length == 2 ? (uint)((s[at] << 8) | s[at + 1]) : s[at];
        return length;
    }

    /// <summary>The text a code stands for, or null when nothing maps it.</summary>
    public string? Unicode(uint code, int length)
    {
        if (!IsComposite) return _text![code & 0xFF];
        if (_toUnicode?.Unicode(code, length) is string mapped) return mapped;
        if (_identityUnicode && code is > 0 and <= 0xFFFF and not (>= 0xD800 and <= 0xDFFF)) return ((char)code).ToString();
        if (_encoding?.Unicode(code, length) is string predefined) return predefined;
        return Cid(code) is int cid ? CollectionText(cid) ?? _glyphText?.Invoke(cid) : null;
    }

    /// <summary>
    /// The text of a CID of an Adobe character collection (ISO 32000-1, 9.10.2): the collection's
    /// table is read the first time a code has no ToUnicode mapping.
    /// </summary>
    private string? CollectionText(int cid)
    {
        if (_ordering is null) return null;
        if (!_collectionTextLoaded)
        {
            _collectionText = PredefinedCMaps.ForOrdering(_ordering);
            _collectionTextLoaded = true;
        }

        return _collectionText?.Text(cid);
    }

    /// <summary>The horizontal advance w0, in thousandths of text space.</summary>
    public double Width(uint code)
    {
        if (!IsComposite) return _widths![code & 0xFF];
        return Cid(code) is int cid && _w?.First(cid) is double w ? w : _dw;
    }

    /// <summary>Vertical metrics (ISO 32000-1, 9.7.4.3): the advance w1 (negative, down) and the position vector.</summary>
    public (double W1, double Vx, double Vy) VerticalMetrics(uint code)
    {
        if (Cid(code) is int cid && _w2?.Find(cid) is double[] m) return (m[0], m[1], m[2]);
        return (_dw2W1, Width(code) / 2, _dw2Y);
    }

    private int? Cid(uint code) => _encoding is { MapsCids: true } ? _encoding.Cid(code) ?? 0 : null;

    /// <summary>Reads a font dictionary. Never throws; <paramref name="budget"/> bounds the font data decoded.</summary>
    public static TextFont Load(PdfDocument document, PdfDictionary font, string resourceName, FontBudget budget)
    {
        try
        {
            return document.Resolve(font["Subtype"]) is PdfName { Value: "Type0" }
                ? LoadComposite(document, font, resourceName, budget)
                : LoadSimple(document, font, resourceName, budget);
        }
        catch (Exception e) when (e is PdfFormatException or FontFormatException or ArgumentException or InvalidOperationException
            or IndexOutOfRangeException or OverflowException or InvalidCastException or KeyNotFoundException)
        {
            return new TextFont(resourceName);
        }
    }

    private static TextFont LoadSimple(PdfDocument doc, PdfDictionary font, string resourceName, FontBudget budget)
    {
        string subtype = (doc.Resolve(font["Subtype"]) as PdfName)?.Value ?? "Type1";
        bool type3 = subtype == "Type3";
        string name = BaseName(doc, font) ?? (type3 ? (doc.Resolve(font["Name"]) as PdfName)?.Value : null) ?? resourceName;
        PdfDictionary? descriptor = doc.Resolve(font["FontDescriptor"]) as PdfDictionary;
        int flags = (int)(Number(doc, descriptor?["Flags"]) ?? 0);
        bool symbolic = (flags & 4) != 0 && (flags & 32) == 0;
        PdfStandardFont? standard = Standard(name);
        bool symbolFamily = standard == PdfStandardFont.Symbol || standard == PdfStandardFont.ZapfDingbats;
        bool trueTypeSymbolic = subtype == "TrueType" && symbolic;

        // The encoding: a predefined base, or the font's built-in one, then /Differences (ISO 32000-1, 9.6.6).
        PdfObject? encoding = doc.Resolve(font["Encoding"]);
        string?[]? baseEncoding = encoding switch
        {
            PdfName n => FontEncodings.FromName(n.Value),
            PdfDictionary d => FontEncodings.FromName((doc.Resolve(d["BaseEncoding"]) as PdfName)?.Value),
            _ => null,
        };
        string?[]? builtIn = null;
        if (baseEncoding is null && !symbolFamily)
        {
            if (subtype is "Type1" or "MMType1" && doc.Resolve(descriptor?["FontFile"]) is PdfStream program)
            {
                builtIn = Type1Encoding(doc, program, budget);
            }
            else if (subtype is "Type1" or "MMType1" && doc.Resolve(descriptor?["FontFile3"]) is PdfStream compact)
            {
                builtIn = CffEncoding(doc, compact, budget);
            }

            baseEncoding = builtIn ?? (subtype == "TrueType" ? (symbolic ? null : FontEncodings.WinAnsiEncoding) : FontEncodings.StandardEncoding);
        }

        var names = new string?[256];
        baseEncoding?.CopyTo(names, 0);
        if (encoding is PdfDictionary dict && doc.Resolve(dict["Differences"]) is PdfArray differences)
        {
            int code = 0;
            foreach (PdfObject item in differences)
            {
                switch (doc.Resolve(item))
                {
                    case PdfNumber n:
                        code = n.IsInteger && n.IntegerValue is >= 0 and <= 255 ? (int)n.IntegerValue : 256;
                        break;
                    case PdfName glyph when code < 256:
                        names[code++] = glyph.Value;
                        break;
                }
            }
        }

        CMap? toUnicode = ToUnicode(doc, font, budget, out _);
        var text = new string?[256];
        var widths = new double[256];
        StandardFontInfo? symbolInfo = symbolFamily && baseEncoding is null ? standard!.Info : null;
        bool dingbats = standard == PdfStandardFont.ZapfDingbats;
        for (int c = 0; c < 256; c++)
        {
            string? t = toUnicode?.Unicode((uint)c, 1);
            if (t is null && names[c] is string glyphName)
            {
                t = GlyphList.ToUnicode(glyphName, dingbats);
                if (t is null && GlyphList.NumericName(glyphName) is int number) t = NumericText(number, c, baseEncoding);
            }

            if (t is null && symbolInfo is not null) t = BuiltIn(symbolInfo, c);
            if (trueTypeSymbolic)
            {
                // Symbolic TrueType fonts map codes through a (3,0) cmap at U+F000 plus the code
                // (ISO 32000-1, 9.6.6.4), so their text often comes back in that private range.
                if (t is { Length: 1 } && t[0] is >= '\uF000' and <= '\uF0FF') t = SymbolicText(t[0] - 0xF000, standard);
                t ??= SymbolicText(c, standard);
            }

            text[c] = t;
        }

        // Widths (ISO 32000-1, 9.6.2.1): /Widths from /FirstChar, else /MissingWidth. Type 3 widths
        // are in glyph space, which /FontMatrix maps to text space (9.6.5).
        double[]? matrix = type3 ? Numbers(doc, font["FontMatrix"], 6) : null;
        double scale = matrix is not null ? matrix[0] * 1000 : 1;
        double missing = Number(doc, descriptor?["MissingWidth"]) ?? 0;
        if (doc.Resolve(font["Widths"]) is PdfArray widthArray)
        {
            int first = (int)Math.Max(0, Math.Min(255, Number(doc, font["FirstChar"]) ?? 0));
            for (int c = 0; c < 256; c++)
            {
                int i = c - first;
                double? w = i >= 0 && i < widthArray.Count ? Number(doc, widthArray[i]) : null;
                widths[c] = (w ?? missing) * scale;
            }
        }
        else
        {
            // Without widths, the standard 14 fonts use their own metrics; anything else is
            // measured as Helvetica, which is what viewers substitute.
            PdfStandardFont metrics = standard ?? PdfStandardFont.Helvetica;
            for (int c = 0; c < 256; c++)
            {
                string? glyphText = names[c] is string n ? GlyphList.ToUnicode(n, dingbats) : symbolInfo is not null ? BuiltIn(symbolInfo, c) : text[c];
                widths[c] = glyphText is not null && metrics.TryGetGlyph(char.ConvertToUtf32(glyphText, 0), out int g) ? metrics.GetAdvance(g) : missing;
            }
        }

        (double ascent, double descent) = VerticalExtent(doc, descriptor, standard);
        if (matrix is not null && Numbers(doc, font["FontBBox"], 4) is { } box && box[3] > box[1])
        {
            double y = matrix[3] * 1000;
            (ascent, descent) = Sane(Math.Max(box[1], box[3]) * y, Math.Min(box[1], box[3]) * y) ?? (ascent, descent);
        }

        return new TextFont(name, text, widths, ascent, descent);
    }

    private static TextFont LoadComposite(PdfDocument doc, PdfDictionary font, string resourceName, FontBudget budget)
    {
        PdfDictionary? cidFont = doc.Resolve(font["DescendantFonts"]) is PdfArray { Count: > 0 } descendants
            ? doc.Resolve(descendants[0]) as PdfDictionary
            : null;
        string name = (cidFont is null ? null : BaseName(doc, cidFont)) ?? BaseName(doc, font) ?? resourceName;
        PdfObject? encodingObject = doc.Resolve(font["Encoding"]);
        CMap? encoding = encodingObject switch
        {
            PdfName n => CMap.Predefined(n.Value),
            PdfStream s => EmbeddedCMap(doc, s, budget, 0),
            _ => null,
        };

        // A predefined CMap the library has no data for still says its writing mode in its name.
        bool vertical = encoding?.WMode == 1 || encodingObject is PdfName { Value: var cmapName } && cmapName.EndsWith("-V", StringComparison.Ordinal);
        CMap? toUnicode = ToUnicode(doc, font, budget, out bool identityUnicode);
        PdfDictionary? descriptor = doc.Resolve(cidFont?["FontDescriptor"]) as PdfDictionary;
        double dw = Number(doc, cidFont?["DW"]) ?? 1000;
        CidMetrics? w = CidMetrics.Parse(doc, doc.Resolve(cidFont?["W"]) as PdfArray, 1);
        CidMetrics? w2 = CidMetrics.Parse(doc, doc.Resolve(cidFont?["W2"]) as PdfArray, 3);
        double[]? dw2 = Numbers(doc, cidFont?["DW2"], 2);

        // Without a ToUnicode map, an embedded TrueType program's own cmap can still name the
        // characters: CID to glyph id through CIDToGIDMap, glyph id back to a code point.
        Func<int, string?>? glyphText = null;
        if (toUnicode is null && !identityUnicode && cidFont is not null && doc.Resolve(cidFont["Subtype"]) is PdfName { Value: "CIDFontType2" }
            && doc.Resolve(descriptor?["FontFile2"]) is PdfStream program)
        {
            glyphText = GlyphText(doc, cidFont, program, budget);
        }

        // The collection whose CIDs these are: the one the CMap names, else the CIDFont's.
        string? ordering = encoding?.Ordering ?? AdobeOrdering(doc, cidFont);

        (double ascent, double descent) = VerticalExtent(doc, descriptor, null);
        return new TextFont(name, encoding, vertical, toUnicode, identityUnicode, w, dw, w2, dw2, glyphText, ordering, ascent, descent);
    }

    /// <summary>The /Ordering of a CIDFont's /CIDSystemInfo when its /Registry is Adobe, else null.</summary>
    private static string? AdobeOrdering(PdfDocument doc, PdfDictionary? cidFont)
    {
        if (doc.Resolve(cidFont?["CIDSystemInfo"]) is not PdfDictionary info) return null;
        if (doc.Resolve(info["Registry"]) is not PdfString { Value: [(byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e'] }) return null;
        return doc.Resolve(info["Ordering"]) is PdfString { Value.Length: < 16 } ordering ? Compat.Latin1.GetString(ordering.Value) : null;
    }

    /// <summary>
    /// Text from the glyph ids of an embedded TrueType program, read lazily: the cmap is inverted
    /// the first time a code has no other mapping.
    /// </summary>
    private static Func<int, string?> GlyphText(PdfDocument doc, PdfDictionary cidFont, PdfStream program, FontBudget budget)
    {
        Dictionary<int, int>? glyphs = null;
        byte[]? cidToGid = doc.Resolve(cidFont["CIDToGIDMap"]) is PdfStream map ? budget.Read(doc, map) : null;
        return cid =>
        {
            glyphs ??= InvertCmap(budget.Read(doc, program));
            int gid = cid;
            if (cidToGid is not null)
            {
                if (cid >= MaxCidToGid || (2 * cid) + 1 >= cidToGid.Length) return null;
                gid = (cidToGid[2 * cid] << 8) | cidToGid[(2 * cid) + 1];
            }

            return glyphs.TryGetValue(gid, out int cp) ? char.ConvertFromUtf32(cp) : null;
        };
    }

    private static Dictionary<int, int> InvertCmap(byte[]? data)
    {
        var glyphs = new Dictionary<int, int>();
        if (data is null) return glyphs;
        OpenTypeFile file;
        try
        {
            file = OpenTypeFile.Parse(data, 0);
        }
        catch (Exception e) when (e is FontFormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return glyphs;
        }

        // The lowest code point wins: a space before a no-break space drawn with the same glyph.
        for (int cp = 0x20; cp <= 0xFFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF) continue;
            if (file.TryGetGlyph(cp, out int glyph) && glyph != 0 && !glyphs.ContainsKey(glyph)) glyphs[glyph] = cp;
        }

        return glyphs;
    }

    /// <summary>/ToUnicode (ISO 32000-1, 9.10.3): a CMap stream, or in some files the name Identity-H.</summary>
    private static CMap? ToUnicode(PdfDocument doc, PdfDictionary font, FontBudget budget, out bool identity)
    {
        PdfObject? value = doc.Resolve(font["ToUnicode"]);
        identity = value is PdfName { Value: "Identity-H" or "Identity-V" };
        return value is PdfStream s && budget.Read(doc, s) is byte[] data ? CMap.Parse(data) : null;
    }

    /// <summary>An embedded encoding CMap, with the CMap /UseCMap or usecmap names under it.</summary>
    private static CMap? EmbeddedCMap(PdfDocument doc, PdfStream stream, FontBudget budget, int depth)
    {
        if (depth > CMap.MaxDepth || budget.Read(doc, stream) is not byte[] data) return null;
        PdfObject? use = doc.Resolve(stream.Dictionary["UseCMap"]);
        CMap? Resolve(string name, int at) => CMap.Predefined(name) ?? (use is PdfStream parent ? EmbeddedCMap(doc, parent, budget, at) : null);
        CMap cmap = CMap.Parse(data, Resolve, depth);
        if (!cmap.HasParent && depth < CMap.MaxDepth)
        {
            cmap.Inherit(use switch
            {
                PdfName n => CMap.Predefined(n.Value),
                PdfStream s => EmbeddedCMap(doc, s, budget, depth + 1),
                _ => null,
            });
        }

        return cmap;
    }

    /// <summary>
    /// The built-in encoding of an embedded Type 1 program: its clear text part sets /Encoding to
    /// StandardEncoding or fills an array with "dup code /name put". Null when neither is found.
    /// </summary>
    private static string?[]? Type1Encoding(PdfDocument doc, PdfStream program, FontBudget budget)
    {
        if (budget.Read(doc, program) is not byte[] data) return null;
        int length = doc.Resolve(program.Dictionary["Length1"]) is PdfNumber { IsInteger: true } l && l.IntegerValue > 0 && l.IntegerValue < data.Length
            ? (int)l.IntegerValue
            : data.Length;
        int start = data.Length >= 6 && data[0] == 0x80 && data[1] == 0x01 ? 6 : 0;
        ReadOnlySpan<byte> clear = data.AsSpan(start, Math.Max(0, length - start));
        int at = clear.IndexOf("/Encoding"u8);
        if (at < 0) return null;
        var parser = new PdfParser(data.AsMemory(start, clear.Length), at + 9);
        var names = new string?[256];
        bool any = false;
        long? code = null;
        string? glyph = null;
        for (int i = 0; i < 10_000; i++)
        {
            Token t;
            try
            {
                t = parser.NextToken();
            }
            catch (PdfFormatException)
            {
                break;
            }

            if (t.Kind == TokenKind.EndOfInput) break;
            ReadOnlySpan<byte> s = parser.Span[t.Start..t.End];
            if (t.Kind == TokenKind.Keyword)
            {
                if (s.SequenceEqual("StandardEncoding"u8) && !any) return FontEncodings.StandardEncoding;
                if (s.SequenceEqual("def"u8) && any) break;
                if (s.SequenceEqual("put"u8) && code is >= 0 and <= 255 && glyph is not null)
                {
                    names[code.Value] = glyph;
                    any = true;
                }

                if (s.SequenceEqual("dup"u8) || s.SequenceEqual("put"u8)) (code, glyph) = (null, null);
            }
            else if (t.Kind == TokenKind.Number && PdfParser.TryParseInteger(s, out long n))
            {
                code = n;
            }
            else if (t.Kind == TokenKind.Name)
            {
                glyph = PdfParser.DecodeName(s[1..]);
            }
        }

        return any ? names : null;
    }

    /// <summary>
    /// The built-in encoding of an embedded CFF program: a FontFile3 of subtype Type1C, or the CFF
    /// table of one of subtype OpenType. Null when the program is damaged.
    /// </summary>
    private static string?[]? CffEncoding(PdfDocument doc, PdfStream program, FontBudget budget)
    {
        if (budget.Read(doc, program) is not byte[] data) return null;
        try
        {
            if (doc.Resolve(program.Dictionary["Subtype"]) is PdfName { Value: "OpenType" })
            {
                OpenTypeFile file = OpenTypeFile.ParseSubset(data);
                return file.IsCff ? new CffFont(file.Table("CFF ")).BuiltInEncoding() : null;
            }

            return new CffFont(data).BuiltInEncoding();
        }
        catch (Exception e) when (e is FontFormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Text and widths for a simple font from glyph names and a standard font's metrics.</summary>
    private static void Simple(string?[] text, double[] widths, string?[] names, string?[]? fallback, PdfStandardFont metrics)
    {
        for (int c = 0; c < 256; c++)
        {
            text[c] = names[c] is string n ? GlyphList.ToUnicode(n) : fallback?[c];
            widths[c] = text[c] is string t && metrics.TryGetGlyph(char.ConvertToUtf32(t, 0), out int g) ? metrics.GetAdvance(g) : 0;
        }
    }

    /// <summary>Gxx, cidNN and similar names: the number is a code; a predefined encoding names it better when it is this code.</summary>
    private static string? NumericText(int number, int code, string?[]? baseEncoding)
    {
        if (number == code && baseEncoding?[code] is string name && GlyphList.ToUnicode(name) is string text) return text;
        return number is >= 0x20 and <= 0xFFFF and not (>= 0x7F and <= 0x9F) and not (>= 0xD800 and <= 0xDFFF) ? ((char)number).ToString() : null;
    }

    private static string? SymbolicText(int code, PdfStandardFont? standard)
    {
        if (standard is not null && standard.IsSymbolic && BuiltIn(standard.Info, code) is string s) return s;
        if (code < 0x20) return null;

        // Codes 0x80 to 0x9F read as Windows-1252, the rest as Latin-1.
        string? name = code is >= 0x80 and <= 0x9F ? FontEncodings.WinAnsiEncoding[code] : null;
        return name is not null ? GlyphList.ToUnicode(name) : code is 0x7F ? null : ((char)code).ToString();
    }

    private static string? BuiltIn(StandardFontInfo info, int code)
    {
        int i = code is >= 0 and <= 255 ? Array.IndexOf(info.Codes!, (byte)code) : -1;
        return i >= 0 ? char.ConvertFromUtf32(info.Unicode![i]) : null;
    }

    /// <summary>
    /// Ascent and descent: the descriptor's when sane, else the standard font's, else the font
    /// bounding box, else 750 and -250 so glyph boxes are one font size tall.
    /// </summary>
    private static (double Ascent, double Descent) VerticalExtent(PdfDocument doc, PdfDictionary? descriptor, PdfStandardFont? standard)
    {
        if (Sane(Number(doc, descriptor?["Ascent"]), Number(doc, descriptor?["Descent"])) is { } d) return d;
        if (standard is not null) return (standard.Info.Ascender, standard.Info.Descender);
        if (Numbers(doc, descriptor?["FontBBox"], 4) is { } box && Sane(Math.Max(box[1], box[3]), Math.Min(box[1], box[3])) is { } b) return b;
        return (750, -250);
    }

    private static (double, double)? Sane(double? ascent, double? descent)
    {
        if (ascent is not double a || descent is not double d) return null;

        // Some producers write the descent as a positive number.
        if (d > 0) d = -d;
        return a > 0 && a <= 3000 && d >= -3000 && a - d >= 100 ? (a, d) : null;
    }

    /// <summary>The standard 14 font a name stands for, by exact name or by the usual Windows and Mac names.</summary>
    internal static PdfStandardFont? Standard(string name)
    {
        if (PdfStandardFont.FromName(name) is { } exact) return exact;
        string n = name.Replace(" ", "").Replace("-", "").Replace(",", "");
        bool bold = Compat.Contains(n, "Bold", StringComparison.OrdinalIgnoreCase);
        bool italic = Compat.Contains(n, "Italic", StringComparison.OrdinalIgnoreCase) || Compat.Contains(n, "Oblique", StringComparison.OrdinalIgnoreCase);
        int style = (bold ? 1 : 0) + (italic ? 2 : 0);
        IReadOnlyList<PdfStandardFont> all = PdfStandardFont.All;
        if (n.StartsWith("Arial", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase)) return all[style];
        if (n.StartsWith("Times", StringComparison.OrdinalIgnoreCase)) return all[4 + style];
        if (n.StartsWith("Courier", StringComparison.OrdinalIgnoreCase)) return all[8 + style];
        if (n.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase)) return PdfStandardFont.Symbol;
        if (Compat.Contains(n, "Dingbats", StringComparison.OrdinalIgnoreCase)) return PdfStandardFont.ZapfDingbats;
        return null;
    }

    /// <summary>/BaseFont without its subset tag (ISO 32000-1, 9.6.4): ABCDEF+Name is Name.</summary>
    private static string? BaseName(PdfDocument doc, PdfDictionary font)
    {
        if (doc.Resolve(font["BaseFont"]) is not PdfName { Value.Length: > 0 } n) return null;
        string name = n.Value;
        if (name.Length > 7 && name[6] == '+')
        {
            bool tag = true;
            for (int i = 0; i < 6; i++) tag &= name[i] is >= 'A' and <= 'Z';
            if (tag) name = name[7..];
        }

        return name;
    }

    private static double? Number(PdfDocument doc, PdfObject? value) =>
        doc.Resolve(value) is PdfNumber n && n.ToDouble() is var v && !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e9 ? v : null;

    private static double[]? Numbers(PdfDocument doc, PdfObject? value, int count)
    {
        if (doc.Resolve(value) is not PdfArray a || a.Count < count) return null;
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (Number(doc, a[i]) is not double v) return null;
            result[i] = v;
        }

        return result;
    }

    /// <summary>
    /// /W or /W2 (ISO 32000-1, 9.7.4.3): runs of CIDs with <c>stride</c> numbers each, "c [v ...]"
    /// listing them and "first last v" repeating one set. Ranges are never expanded.
    /// </summary>
    private sealed class CidMetrics
    {
        private readonly List<(int First, int Last, double[] Values, bool Repeat)> _runs = [];
        private readonly int _stride;

        private CidMetrics(int stride) => _stride = stride;

        public static CidMetrics? Parse(PdfDocument doc, PdfArray? array, int stride)
        {
            if (array is null) return null;
            var metrics = new CidMetrics(stride);
            int total = 0;
            for (int i = 0; i + 1 < array.Count && total < MaxMetrics;)
            {
                if (Number(doc, array[i]) is not double firstValue || firstValue < 0 || firstValue > int.MaxValue) break;
                int first = (int)firstValue;
                if (doc.Resolve(array[i + 1]) is PdfArray list)
                {
                    int count = Math.Min(list.Count / stride, (MaxMetrics - total) / stride);
                    var values = new double[count * stride];
                    for (int k = 0; k < values.Length; k++) values[k] = Number(doc, list[k]) ?? 0;
                    if (count > 0) metrics._runs.Add((first, (int)Math.Min(int.MaxValue, (long)first + count - 1), values, false));
                    total += values.Length;
                    i += 2;
                }
                else
                {
                    if (i + 2 + stride > array.Count || Number(doc, array[i + 1]) is not double lastValue || lastValue < first) break;
                    var values = new double[stride];
                    for (int k = 0; k < stride; k++) values[k] = Number(doc, array[i + 2 + k]) ?? 0;
                    metrics._runs.Add((first, (int)Math.Min(lastValue, int.MaxValue), values, true));
                    total += stride;
                    i += 2 + stride;
                }
            }

            // Sorted by first CID, the order of definition kept among equals.
            var ordered = metrics._runs.Select((r, index) => (r, index)).OrderBy(e => e.r.First).ThenBy(e => e.index).Select(e => e.r).ToList();
            metrics._runs.Clear();
            metrics._runs.AddRange(ordered);
            return metrics;
        }

        /// <summary>The numbers for a CID, or null.</summary>
        public double[]? Find(int cid)
        {
            if (Locate(cid) is not (double[] values, int offset)) return null;
            var result = new double[_stride];
            Array.Copy(values, offset, result, 0, _stride);
            return result;
        }

        /// <summary>The first number for a CID, or null: <see cref="Find"/> without a copy, for every glyph's width.</summary>
        public double? First(int cid) => Locate(cid) is (double[] values, int offset) ? values[offset] : null;

        /// <summary>The run holding a CID's numbers and where they start in it, or null.</summary>
        private (double[] Values, int Offset)? Locate(int cid)
        {
            int lo = 0, hi = _runs.Count - 1, at = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (_runs[mid].First <= cid)
                {
                    at = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            for (int i = at; i >= 0 && i > at - 16; i--)
            {
                (int first, int last, double[] values, bool repeat) = _runs[i];
                if (cid > last) continue;
                return (values, repeat ? 0 : (cid - first) * _stride);
            }

            return null;
        }
    }
}

/// <summary>Font data bytes one extraction may decode, shared by every font it loads.</summary>
internal sealed class FontBudget
{
    /// <summary>Total font program and CMap bytes per page.</summary>
    public const long MaxBytes = 64L << 20;

    private readonly Dictionary<PdfStream, byte[]?> _read = [];
    private long _used;

    /// <summary>The decoded stream, or null when it is damaged or the budget is spent.</summary>
    public byte[]? Read(PdfDocument document, PdfStream stream)
    {
        if (_read.TryGetValue(stream, out byte[]? cached)) return cached;
        byte[]? data = null;
        if (_used < MaxBytes)
        {
            try
            {
                data = document.GetStreamData(stream);
                _used += data.Length;
                if (_used > MaxBytes) data = null;
            }
            catch (PdfFormatException)
            {
                data = null;
            }
        }

        _read[stream] = data;
        return data;
    }
}
