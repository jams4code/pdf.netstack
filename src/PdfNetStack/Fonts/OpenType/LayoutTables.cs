using System.Collections.Concurrent;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>GDEF glyph classes (ISO/IEC 14496-22, GDEF).</summary>
internal enum GlyphClass : byte
{
    Unclassified,
    Base,
    Ligature,
    Mark,
    Component,
}

/// <summary>One lookup of a GSUB or GPOS table, with its subtables resolved through extension lookups.</summary>
internal sealed record Lookup(int Type, int Flag, int[] Subtables, int MarkFilteringSet);

/// <summary>
/// The script, feature and lookup lists shared by GSUB and GPOS. Offsets are absolute in the
/// font data. Lookups are read once and cached.
/// </summary>
internal sealed class LayoutTable
{
    private readonly byte[] _data;
    private readonly int _scripts, _features, _lookups;
    private readonly Lookup?[] _cache;
    private readonly int _extensionType;
    private readonly Dictionary<int, int> _substitutions = [];

    // Features per list of script tags: every run a font shapes asks again. Fonts are shared
    // between threads.
    private readonly ConcurrentDictionary<string, (List<(string Tag, int[] Lookups)> Features, string? Chosen)> _featureCache = new(StringComparer.Ordinal);

    private LayoutTable(OpenTypeFile font, string tag)
    {
        _data = font.Data;
        _extensionType = tag == "GSUB" ? 7 : 9;
        ReadOnlySpan<byte> d = _data;
        int table = font.TableOffset(tag);
        _scripts = table + U16(d, table + 4);
        _features = table + U16(d, table + 6);
        _lookups = table + U16(d, table + 8);
        _cache = new Lookup?[U16(d, _lookups)];
        if (U16(d, table + 2) >= 1 && Offset32(d, table + 10) is var variations and > 0 && font.Coordinates.Length > 0)
        {
            try
            {
                ReadFeatureVariations(table + variations, font.Coordinates);
            }
            catch (FontFormatException)
            {
                _substitutions.Clear(); // damaged variations leave the default features
            }
        }
    }

    /// <summary>
    /// Feature variations (OpenType 1.8): the first record whose conditions hold at the instance
    /// swaps some features for alternate feature tables, such as heavier glyphs at bold weights.
    /// </summary>
    private void ReadFeatureVariations(int at, float[] coordinates)
    {
        ReadOnlySpan<byte> d = _data;
        long count = U32(d, at + 4);
        if (count > (d.Length - at) / 8) throw new FontFormatException("Feature variations are truncated.");
        for (int r = 0; r < count; r++)
        {
            int record = at + 8 + 8 * r;
            int conditions = Offset32(d, record), substitutions = Offset32(d, record + 4);
            if (!Holds(at + conditions, conditions == 0)) continue;
            if (substitutions == 0) return;
            int table = at + substitutions;
            int n = U16(d, table + 4);
            for (int k = 0; k < n; k++)
            {
                int entry = table + 6 + 6 * k;
                _substitutions[U16(d, entry)] = table + Offset32(d, entry + 2);
            }

            return;
        }

        bool Holds(int set, bool empty)
        {
            if (empty) return true;
            ReadOnlySpan<byte> s = _data;
            int n = U16(s, set);
            for (int c = 0; c < n; c++)
            {
                int condition = set + Offset32(s, set + 2 + 4 * c);
                if (U16(s, condition) != 1) return false;
                int axis = U16(s, condition + 2);
                double v = axis < coordinates.Length ? coordinates[axis] : 0;
                if (v < FontVariations.F2Dot14(s, condition + 4) || v > FontVariations.F2Dot14(s, condition + 6)) return false;
            }

            return true;
        }
    }

    public byte[] Data => _data;

    public int LookupCount => _cache.Length;

    /// <summary>The table, or null when the font lacks it or it is damaged.</summary>
    public static LayoutTable? Load(OpenTypeFile font, string tag)
    {
        if (!font.HasTable(tag)) return null;
        try
        {
            return new LayoutTable(font, tag);
        }
        catch (FontFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The features of the default language system of <paramref name="script"/>, falling back to
    /// DFLT and then latn as HarfBuzz does: tag and lookup indices of each.
    /// </summary>
    public List<(string Tag, int[] Lookups)> Features(string script) => Features([script], out _);

    /// <summary>
    /// The features of the default language system of the first of <paramref name="scripts"/> the
    /// table has, else of DFLT, dflt or latn. <paramref name="chosen"/> is the script tag used, or
    /// null when there is none. The list is shared by every caller asking for the same scripts:
    /// read it, never change it.
    /// </summary>
    public List<(string Tag, int[] Lookups)> Features(IReadOnlyList<string> scripts, out string? chosen)
    {
        string key = string.Join(" ", scripts);
        if (!_featureCache.TryGetValue(key, out var cached))
        {
            List<(string Tag, int[] Lookups)> read = ReadFeatures(scripts, out string? found);
            cached = _featureCache.GetOrAdd(key, (read, found));
        }

        chosen = cached.Chosen;
        return cached.Features;
    }

    private List<(string Tag, int[] Lookups)> ReadFeatures(IReadOnlyList<string> scripts, out string? chosen)
    {
        var result = new List<(string, int[])>();
        ReadOnlySpan<byte> d = _data;
        int langSys = -1;
        chosen = null;
        foreach (string candidate in scripts.Concat(["DFLT", "dflt", "latn"]))
        {
            int count = U16(d, _scripts);
            for (int i = 0; i < count && langSys < 0; i++)
            {
                int record = _scripts + 2 + 6 * i;
                if (Tag(d, record) != candidate) continue;
                int scriptTable = _scripts + U16(d, record + 4);
                int defaultLangSys = U16(d, scriptTable);
                if (defaultLangSys != 0) langSys = scriptTable + defaultLangSys;
            }

            if (langSys < 0) continue;
            chosen = candidate;
            break;
        }

        if (langSys < 0) return result;
        int featureCount = U16(d, _features);
        int n = U16(d, langSys + 4);
        for (int i = 0; i < n; i++)
        {
            int index = U16(d, langSys + 6 + 2 * i);
            if (index >= featureCount) continue;
            result.Add((Tag(d, _features + 2 + 6 * index), FeatureLookups(index)));
        }

        return result;
    }

    public Lookup? GetLookup(int index)
    {
        if ((uint)index >= (uint)_cache.Length) return null;
        if (_cache[index] is Lookup cached) return cached;
        ReadOnlySpan<byte> d = _data;
        int lookup = _lookups + U16(d, _lookups + 2 + 2 * index);
        int type = U16(d, lookup), flag = U16(d, lookup + 2), count = U16(d, lookup + 4);
        var subtables = new int[count];
        int actualType = type;
        for (int s = 0; s < count; s++)
        {
            int subtable = lookup + U16(d, lookup + 6 + 2 * s);
            if (IsExtension(type))
            {
                actualType = U16(d, subtable + 2);
                subtable += Offset32(d, subtable + 4);
            }

            subtables[s] = subtable;
        }

        int markSet = (flag & 0x10) != 0 ? U16(d, lookup + 6 + 2 * count) : -1;
        return _cache[index] = new Lookup(actualType, flag, subtables, markSet);
    }

    /// <summary>
    /// True when a GSUB lookup would substitute exactly this glyph sequence, read as written in
    /// the font: no glyphs skipped, and with <paramref name="zeroContext"/> no rule that needs
    /// glyphs before or after the sequence. A damaged subtable does not apply.
    /// </summary>
    public bool WouldSubstitute(int lookupIndex, ReadOnlySpan<int> glyphs, bool zeroContext)
    {
        Lookup? lookup;
        try
        {
            lookup = GetLookup(lookupIndex);
        }
        catch (FontFormatException)
        {
            return false;
        }

        if (lookup is null || glyphs.Length == 0) return false;
        foreach (int subtable in lookup.Subtables)
        {
            try
            {
                if (WouldApply(lookup.Type, subtable, glyphs, zeroContext)) return true;
            }
            catch (FontFormatException)
            {
                // Try the next subtable.
            }
        }

        return false;
    }

    private bool WouldApply(int type, int sub, ReadOnlySpan<int> glyphs, bool zeroContext)
    {
        ReadOnlySpan<byte> d = _data;
        int format = U16(d, sub);
        bool covered = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), glyphs[0]) is var c && c >= 0;
        switch (type)
        {
            case 1 or 2 or 3:
                return glyphs.Length == 1 && covered;
            case 4:
            {
                if (!covered || c >= U16(d, sub + 4)) return false;
                int set = sub + U16(d, sub + 6 + 2 * c);
                int count = U16(d, set);
                for (int l = 0; l < count; l++)
                {
                    int lig = set + U16(d, set + 2 + 2 * l);
                    if (U16(d, lig + 2) != glyphs.Length) continue;
                    bool match = true;
                    for (int k = 1; k < glyphs.Length && match; k++) match = U16(d, lig + 4 + 2 * (k - 1)) == glyphs[k];
                    if (match) return true;
                }

                return false;
            }

            case 5 or 6:
                return WouldMatchContext(type == 6, format, sub, glyphs, zeroContext);
            case 8:
            {
                if (glyphs.Length != 1 || !covered) return false;
                int backtrack = U16(d, sub + 4);
                int lookahead = U16(d, sub + 6 + 2 * backtrack);
                return !zeroContext || (backtrack == 0 && lookahead == 0);
            }

            default:
                return false;
        }
    }

    private bool WouldMatchContext(bool chained, int format, int sub, ReadOnlySpan<int> glyphs, bool zeroContext)
    {
        ReadOnlySpan<byte> d = _data;
        if (format == 3)
        {
            int at = sub + 2;
            int backtrack = 0;
            if (chained)
            {
                backtrack = U16(d, at);
                at += 2 + 2 * backtrack;
            }

            int inputCount = U16(d, at);
            int inputs = chained ? at + 2 : at + 4;
            if (inputCount != glyphs.Length) return false;
            if (chained && zeroContext && (backtrack != 0 || U16(d, inputs + 2 * inputCount) != 0)) return false;
            for (int k = 0; k < inputCount; k++)
            {
                if (Kerning.CoverageIndex(d, sub + U16(d, inputs + 2 * k), glyphs[k]) < 0) return false;
            }

            return true;
        }

        if (format is not (1 or 2)) return false;
        int inputDef = format == 2 ? sub + U16(d, sub + (chained ? 6 : 4)) : 0;
        int sets = format == 1 ? sub + 4 : sub + (chained ? 10 : 6);
        int setIndex = format == 1 ? Kerning.CoverageIndex(d, sub + U16(d, sub + 2), glyphs[0]) : Kerning.ClassOf(d, inputDef, glyphs[0]);
        if (setIndex < 0 || setIndex >= U16(d, sets)) return false;
        int setOffset = U16(d, sets + 2 + 2 * setIndex);
        if (setOffset == 0) return false;
        int set = sub + setOffset;
        int rules = U16(d, set);
        for (int r = 0; r < rules; r++)
        {
            int at = set + U16(d, set + 2 + 2 * r);
            int backtrack = 0;
            if (chained)
            {
                backtrack = U16(d, at);
                at += 2 + 2 * backtrack;
            }

            int inputCount = U16(d, at);
            int inputs = chained ? at + 2 : at + 4;
            if (inputCount != glyphs.Length) continue;
            if (chained && zeroContext && (backtrack != 0 || U16(d, inputs + 2 * (inputCount - 1)) != 0)) continue;
            bool match = true;
            for (int k = 1; k < inputCount && match; k++)
            {
                int value = U16(d, inputs + 2 * (k - 1));
                match = format == 1 ? glyphs[k] == value : Kerning.ClassOf(d, inputDef, glyphs[k]) == value;
            }

            if (match) return true;
        }

        return false;
    }

    /// <summary>Extension lookups: type 7 in GSUB, 9 in GPOS.</summary>
    private bool IsExtension(int type) => type == _extensionType;

    private int[] FeatureLookups(int index)
    {
        ReadOnlySpan<byte> d = _data;
        int feature = _substitutions.TryGetValue(index, out int alternate) ? alternate : _features + U16(d, _features + 2 + 6 * index + 4);
        int n = U16(d, feature + 2);
        var lookups = new int[n];
        for (int k = 0; k < n; k++) lookups[k] = U16(d, feature + 4 + 2 * k);
        return lookups;
    }
}

/// <summary>Glyph classes, mark attachment classes and mark filtering sets from GDEF.</summary>
internal sealed class GlyphDefinitions
{
    private readonly byte[] _data;
    private readonly int _classes, _markAttach, _markSets;

    private GlyphDefinitions(OpenTypeFile font)
    {
        _data = font.Data;
        ReadOnlySpan<byte> d = _data;
        int table = font.TableOffset("GDEF");
        int minor = U16(d, table + 2);
        _classes = U16(d, table + 4) is var c and > 0 ? table + c : -1;
        _markAttach = U16(d, table + 10) is var m and > 0 ? table + m : -1;
        _markSets = minor >= 2 && U16(d, table + 12) is var s and > 0 ? table + s : -1;
        if (minor >= 3 && Offset32(d, table + 14) is var v and > 0)
        {
            try
            {
                Variations = ItemVariationStore.Read(_data, table + v);
            }
            catch (FontFormatException)
            {
                // A damaged store adds no deltas.
            }
        }
    }

    /// <summary>The item variation store of a variable font (GDEF 1.3), which GPOS device tables index.</summary>
    public ItemVariationStore? Variations { get; }

    public static GlyphDefinitions? Load(OpenTypeFile font)
    {
        if (!font.HasTable("GDEF")) return null;
        try
        {
            return new GlyphDefinitions(font);
        }
        catch (FontFormatException)
        {
            return null;
        }
    }

    /// <summary>True when the table classifies glyphs as bases, ligatures, marks and components.</summary>
    public bool HasClasses => _classes >= 0;

    public GlyphClass ClassOf(int glyph) => _classes < 0 ? GlyphClass.Unclassified : (GlyphClass)Math.Min(4, Kerning.ClassOf(_data, _classes, glyph));

    public int MarkAttachClass(int glyph) => _markAttach < 0 ? 0 : Kerning.ClassOf(_data, _markAttach, glyph);

    public bool InMarkSet(int set, int glyph)
    {
        if (_markSets < 0 || set < 0) return false;
        ReadOnlySpan<byte> d = _data;
        if (set >= U16(d, _markSets + 2)) return false;
        int coverage = _markSets + Offset32(d, _markSets + 4 + 4 * set);
        return Kerning.CoverageIndex(d, coverage, glyph) >= 0;
    }
}
