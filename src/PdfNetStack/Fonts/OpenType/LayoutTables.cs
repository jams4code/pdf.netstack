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
    public List<(string Tag, int[] Lookups)> Features(string script)
    {
        var result = new List<(string, int[])>();
        ReadOnlySpan<byte> d = _data;
        int langSys = -1;
        foreach (string candidate in new[] { script, "DFLT", "dflt", "latn" })
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

            if (langSys >= 0) break;
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

    /// <summary>Extension lookups: type 7 in GSUB, 9 in GPOS.</summary>
    private bool IsExtension(int type) => type == _extensionType;

    private int[] FeatureLookups(int index)
    {
        ReadOnlySpan<byte> d = _data;
        int feature = _features + U16(d, _features + 2 + 6 * index + 4);
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
    }

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
