using System.Collections.Concurrent;
using System.Globalization;
using PdfNetStack.Text;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>The layout tables of a font, loaded once and shared by every shaping call.</summary>
internal sealed class ShapingTables(OpenTypeFile font)
{
    public OpenTypeFile Font { get; } = font;

    public LayoutTable? Gsub { get; } = LayoutTable.Load(font, "GSUB");

    public LayoutTable? Gpos { get; } = LayoutTable.Load(font, "GPOS");

    public GlyphDefinitions? Gdef { get; } = GlyphDefinitions.Load(font);

    /// <summary>The legacy kern table, used when GPOS has no kern feature for the script.</summary>
    public Kerning? Kerning { get; } = Kerning.LoadLegacy(font);

    /// <summary>What the Indic shaper learns about the font, once per script.</summary>
    internal ConcurrentDictionary<string, IndicPlan> IndicPlans { get; } = new(StringComparer.Ordinal);

    /// <summary>The GSUB features, the GSUB script chosen and the GPOS features, by the script tags searched.</summary>
    internal ConcurrentDictionary<string, (List<(string Tag, int[] Lookups)> Gsub, string? Script, List<(string Tag, int[] Lookups)> Gpos)> Features { get; } =
        new(StringComparer.Ordinal);
}

/// <summary>What happened to a glyph during shaping, and what kind of character it came from.</summary>
[Flags]
internal enum GlyphFlags : ushort
{
    None = 0,

    /// <summary>A GSUB lookup replaced the glyph.</summary>
    Substituted = 1,

    /// <summary>The glyph is the result of a ligature substitution.</summary>
    Ligated = 2,

    /// <summary>The glyph is one of several a multiple substitution produced.</summary>
    Multiplied = 4,

    /// <summary>A default ignorable character: joiners, variation selectors, bidi controls.</summary>
    Ignorable = 8,

    /// <summary>A default ignorable that GSUB does not skip: the grapheme joiner, variation selectors of Mongolian, tags.</summary>
    Hidden = 16,

    Zwj = 32,

    Zwnj = 64,

    /// <summary>A GSUB lookup replaced the glyph since the shaper last cleared this flag; shapers read it between stages.</summary>
    Changed = 128,
}

/// <summary>A glyph being shaped. Positions are in font units; offsets are relative to the pen.</summary>
internal sealed class GlyphInfo
{
    public int Glyph;
    public int Cluster;
    public string Text = "";
    public int CodePoint;
    public uint Mask;
    public GlyphClass Class;
    public GlyphFlags Flags;
    public UnicodeCategory Category;
    public byte CombiningClass;
    public byte IndicCategory;
    public byte IndicPosition;
    public byte UseCategory;
    public byte SpaceFallback;
    public int Syllable;
    public int LigatureId;
    public int Component;
    public int XAdvance;
    public int XOffset;
    public int YOffset;
    public int AttachTo = -1;
    public int AttachDx;
    public int CursiveParent = -1;

    public bool IsMark => Category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>A default ignorable that no lookup has replaced.</summary>
    public bool IsIgnorable => (Flags & (GlyphFlags.Ignorable | GlyphFlags.Substituted)) == GlyphFlags.Ignorable;

    public GlyphInfo Copy() => (GlyphInfo)MemberwiseClone();
}

/// <summary>A feature to apply: its tag and how its lookups match.</summary>
/// <param name="Tag">The feature tag.</param>
/// <param name="ManualJoiners">Joiners are not skipped: the font's rules spell them out.</param>
/// <param name="PerSyllable">Matching stays inside the syllable of the first glyph.</param>
/// <param name="ManualZwj">ZWJ is not skipped; ZWNJ still is.</param>
/// <param name="Global">The feature applies to every glyph, even when its tag has a mask of its own (rphf, pref, blwf...).</param>
internal readonly record struct ShapingFeature(string Tag, bool ManualJoiners = false, bool PerSyllable = false, bool ManualZwj = false, bool Global = false);

/// <summary>
/// OpenType shaping of one run (one font, script and direction): character to glyph mapping,
/// Unicode normalization against the font's coverage, Arabic joining forms, Indic syllables and
/// reordering, GSUB substitutions (single, multiple, alternate, ligature, contextual, chained,
/// reverse chained) and GPOS positioning (single, pair, cursive, mark to base, ligature and mark,
/// contextual). Feature selection, ordering and lookup matching follow HarfBuzz's default,
/// Arabic, Hebrew, Indic, Universal Shaping Engine, Thai and Lao, Khmer, Myanmar and Hangul
/// shapers. Written from ISO/IEC 14496-22 and the Microsoft script development specifications.
/// </summary>
internal sealed partial class Shaper
{
    internal const uint Global = 1, Isol = 2, Fina = 4, Medi = 8, Init = 16, Rphf = 32, Pref = 64, Blwf = 128, Abvf = 256, Half = 512, Pstf = 1024,
        Cfar = 2048, Ljmo = 4096, Vjmo = 8192, Tjmo = 16384;
    private const int MaxNesting = 6;

    /// <summary>A context sequence never grows past this many glyphs (HarfBuzz's limit too).</summary>
    private const int MaxContextLength = 64;

    private static readonly ShapingFeature[][] ArabicStages =
    [
        [new("ccmp"), new("locl")], [new("isol")], [new("fina")], [new("medi")], [new("init")], [new("rlig")],
        [new("calt"), new("rclt")], [new("mset")], [new("liga"), new("clig"), new("rvrn")],
    ];

    private static readonly ShapingFeature[][] DefaultStages =
    [
        [new("rvrn"), new("ccmp"), new("locl"), new("rlig"), new("calt"), new("clig"), new("liga"), new("rclt")],
    ];

    // As in HarfBuzz, mark and mkmk see joiners: a ZWJ between a base and its mark keeps them apart.
    private static readonly ShapingFeature[] PositionFeatures =
        [new("abvm"), new("blwm"), new("curs"), new("dist"), new("kern"), new("mark", true), new("mkmk", true)];

    /// <summary>
    /// OpenType tags of the Indic scripts by their ISO 15924 tags, in the order a font is searched:
    /// the Universal Shaping Engine's version 3, the version 2 specifications, then the old tag.
    /// </summary>
    private static readonly Dictionary<string, string[]> IndicTags = new(StringComparer.Ordinal)
    {
        ["deva"] = ["dev3", "dev2", "deva"], ["beng"] = ["bng3", "bng2", "beng"], ["guru"] = ["gur3", "gur2", "guru"],
        ["gujr"] = ["gjr3", "gjr2", "gujr"], ["orya"] = ["ory3", "ory2", "orya"], ["taml"] = ["tml3", "tml2", "taml"],
        ["telu"] = ["tel3", "tel2", "telu"], ["knda"] = ["knd3", "knd2", "knda"], ["mlym"] = ["mlm3", "mlm2", "mlym"],
    };

    /// <summary>Scripts of the Universal Shaping Engine (as HarfBuzz assigns them), by their OpenType tags.</summary>
    private static readonly HashSet<string> UseScripts = new(StringComparer.Ordinal)
    {
        "tibt", "mong", "sinh", "buhd", "hano", "tglg", "tagb", "limb", "tale", "bugi", "khar", "sylo", "tfng", "bali",
        "nko ", "phag", "cham", "kali", "lepc", "rjng", "saur", "sund", "egyp", "java", "kthi", "mtei", "lana", "tavt",
        "batk", "brah", "mand", "cakm", "plrd", "shrd", "takr", "dupl", "gran", "khoj", "sind", "mahj", "mani", "modi",
        "hmng", "phlp", "sidd", "tirh", "ahom", "mult", "adlm", "bhks", "marc", "newa", "gonm", "soyo", "zanb", "dogr",
        "gong", "rohg", "maka", "medf", "sogo", "sogd", "elym", "nand", "hmnp", "wcho", "chrs", "diak", "kits", "yezi",
        "cpmn", "ougr", "tnsa", "toto", "vith", "kawi", "nagm", "gara", "gukh", "krai", "onao", "sunu", "todr", "tutg",
    };

    /// <summary>Scripts of the Universal Shaping Engine that join like Arabic, with init, medi and fina forms.</summary>
    private static readonly HashSet<string> JoiningScripts = new(StringComparer.Ordinal)
    {
        "mong", "nko ", "phag", "mand", "mani", "phlp", "adlm", "rohg", "sogd", "chrs", "ougr",
    };

    private readonly ShapingTables _tables;
    private readonly OpenTypeFile _font;
    private readonly LayoutTable? _gsub;
    private readonly LayoutTable? _gpos;
    private readonly GlyphDefinitions? _gdef;
    private readonly ItemVariationStore? _variations;
    private readonly float[] _coordinates;
    private readonly Kerning? _legacyKerning;
    private List<GlyphInfo> _buffer = [];
    private List<(string Tag, int[] Lookups)> _gsubFeatures = [];
    private List<(string Tag, int[] Lookups)> _gposFeatures = [];
    private string? _gsubScript;
    private bool _rtl;
    private int _operations;
    private int _nextLigatureId = 1;
    private int _maxLength;

    // The lookup being applied: its feature mask, how it treats joiners, and the syllable it is held to.
    private uint _lookupMask;
    private bool _autoZwj;
    private bool _autoZwnj;
    private bool _perSyllable;
    private bool _positioning;
    private int _syllable;

    /// <summary>A shaper for one call; the tables are shared and read only.</summary>
    public Shaper(ShapingTables tables)
    {
        _tables = tables;
        _font = tables.Font;
        _gsub = tables.Gsub;
        _gpos = tables.Gpos;
        _gdef = tables.Gdef;
        _coordinates = tables.Font.Coordinates;
        _variations = _coordinates.Any(c => c != 0) ? _gdef?.Variations : null;
        _legacyKerning = tables.Kerning;
    }

    private enum ShaperKind
    {
        Default,
        Arabic,
        Hebrew,
        Indic,
        Use,
        Thai,
        Khmer,
        Myanmar,
        Hangul,
    }

    /// <summary>
    /// Shapes code points <paramref name="start"/> to <paramref name="end"/> of <paramref name="text"/>.
    /// Glyphs come back in logical order. Characters without a glyph map to .notdef.
    /// </summary>
    public List<GlyphInfo> Shape(string text, int start, int end, bool rightToLeft, string script)
    {
        _rtl = rightToLeft;
        _buffer = new List<GlyphInfo>(end - start);
        _nextLigatureId = 1;
        for (int i = start; i < end; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int cp = char.IsSurrogate(text[i]) && !char.IsSurrogatePair(text, i) ? 0xFFFD : char.ConvertToUtf32(text, i);

            // In right to left runs, a mirrored character takes its mirror's glyph when the font has one.
            if (rightToLeft && UnicodeData.Mirror(cp) is int mirror && mirror != cp && _font.TryGetGlyph(mirror, out _)) cp = mirror;
            var g = new GlyphInfo
            {
                Cluster = i,
                Text = text.Substring(i, char.IsSurrogatePair(text, i) ? 2 : 1),
                Mask = Global,
            };
            SetCharacter(g, cp);
            _buffer.Add(g);
        }

        FormClusters();
        ShaperKind kind = SelectShaper(script);
        if (kind == ShaperKind.Thai) PreprocessThai(script == "thai");
        else if (kind == ShaperKind.Hangul) PreprocessHangul();
        else if (kind is ShaperKind.Indic or ShaperKind.Use) PreprocessVowelConstraints();
        Normalize(kind);

        // Lookups visit every glyph, and context matching, ligatures and nesting cost steps on top;
        // a crafted font cannot run away.
        int lookups = (_gsub?.LookupCount ?? 0) + (_gpos?.LookupCount ?? 0);
        _operations = 16384 + _buffer.Count * (64 + 4 * lookups);

        // Multiple substitutions may grow the buffer, but never past this (HarfBuzz caps it too).
        _maxLength = Math.Max(64, _buffer.Count * 32);
        bool joining = kind == ShaperKind.Arabic || (kind == ShaperKind.Use && JoiningScripts.Contains(script));
        if (joining) SetJoiningForms(script == "mong");
        Classify();

        switch (kind)
        {
            case ShaperKind.Indic:
                SubstituteIndic(script);
                break;
            case ShaperKind.Use:
                SubstituteUse(joining);
                break;
            case ShaperKind.Khmer:
                SubstituteKhmer();
                break;
            case ShaperKind.Myanmar:
                SubstituteMyanmar();
                break;
            default:
                IReadOnlyList<ShapingFeature[]> stages = kind switch
                {
                    ShaperKind.Arabic => ArabicStages,
                    ShaperKind.Thai => ThaiStages,
                    ShaperKind.Hangul => HangulStages,
                    _ => DefaultStages,
                };
                foreach (ShapingFeature[] stage in stages) ApplyStage(_gsub, _gsubFeatures, stage);
                break;
        }

        foreach (GlyphInfo g in _buffer) g.XAdvance = _font.Advance(g.Glyph);
        PositionFallbackSpaces();

        // The Universal Shaping Engine and Myanmar take mark advances away before positioning, so
        // positioning adds to zero; most shapers do it after; Indic, Khmer and Hangul never do.
        if (kind is ShaperKind.Use or ShaperKind.Myanmar) ZeroMarks();
        bool gposKern = false;
        if (_gpos is not null)
        {
            gposKern = _gposFeatures.Any(f => f.Tag == "kern");
            ApplyStage(_gpos, _gposFeatures, PositionFeatures);
        }

        // As HarfBuzz: the kern table applies when GPOS does not kern this script.
        if (!gposKern && _legacyKerning is not null) LegacyKern();
        HideDefaultIgnorables();
        if (kind is ShaperKind.Default or ShaperKind.Arabic or ShaperKind.Hebrew or ShaperKind.Thai) ZeroMarks();
        ResolveAttachments();
        return _buffer;
    }

    /// <summary>
    /// Finds the font's features for the script and picks the shaper, as HarfBuzz does: Indic
    /// fonts with version 3 tags, and the scripts of the Universal Shaping Engine, go to it; a
    /// font that has the script only under DFLT or latn gets the default shaper, as does Myanmar
    /// under its old mymr tag.
    /// </summary>
    private ShaperKind SelectShaper(string script)
    {
        string[] tags = IndicTags.TryGetValue(script, out string[]? indic) ? indic : script == "mymr" ? ["mym2", "mymr"] : [script];
        (_gsubFeatures, _gsubScript, _gposFeatures) = _tables.Features.GetOrAdd(string.Join(",", tags), _ =>
        {
            string? chosen = null;
            List<(string Tag, int[] Lookups)> gsub = _gsub?.Features(tags, out chosen) ?? [];
            List<(string Tag, int[] Lookups)> gpos = _gpos?.Features(tags, out string? _) ?? [];
            return (gsub, chosen, gpos);
        });
        bool fallback = _gsubScript is "DFLT" or "latn";
        return script switch
        {
            "arab" or "syrc" => ShaperKind.Arabic,
            "hebr" => ShaperKind.Hebrew,
            "thai" or "lao " => ShaperKind.Thai,
            "khmr" => ShaperKind.Khmer,
            "hang" => ShaperKind.Hangul,
            "mymr" => fallback || _gsubScript == "mymr" ? ShaperKind.Default : ShaperKind.Myanmar,
            _ when indic is not null => fallback ? ShaperKind.Default : _gsubScript is { Length: 4 } && _gsubScript[3] == '3' ? ShaperKind.Use : ShaperKind.Indic,
            _ when UseScripts.Contains(script) => fallback ? ShaperKind.Default : ShaperKind.Use,
            _ => ShaperKind.Default,
        };
    }

    /// <summary>Sets a glyph's character and the Unicode properties shaping reads from it.</summary>
    private static void SetCharacter(GlyphInfo g, int cp)
    {
        g.CodePoint = cp;
        g.Category = ShapingData.Category(cp);
        g.CombiningClass = (byte)ShapingData.CombiningClass(cp);
        GlyphFlags flags = g.Flags & ~(GlyphFlags.Ignorable | GlyphFlags.Hidden | GlyphFlags.Zwj | GlyphFlags.Zwnj);
        if (IsDefaultIgnorable(cp))
        {
            flags |= GlyphFlags.Ignorable;
            if (cp == 0x200C) flags |= GlyphFlags.Zwnj;
            else if (cp == 0x200D) flags |= GlyphFlags.Zwj;
            else if (cp is 0x034F or (>= 0x180B and <= 0x180D) or 0x180F or (>= 0xE0020 and <= 0xE007F)) flags |= GlyphFlags.Hidden;
        }

        g.Flags = flags;
    }

    /// <summary>
    /// Combining marks and zero width joiners belong to the cluster of the character before them,
    /// so a base and its marks are selected, copied and broken as one (HarfBuzz cluster level 0).
    /// </summary>
    private void FormClusters()
    {
        for (int i = 1; i < _buffer.Count; i++)
        {
            GlyphInfo g = _buffer[i];
            if (g.IsMark || g.CodePoint == 0x200D) g.Cluster = _buffer[i - 1].Cluster;
        }
    }

    /// <summary>
    /// Gives glyphs <paramref name="start"/> to <paramref name="end"/> one cluster, the smallest of
    /// theirs, along with neighbours that shared a cluster with them.
    /// </summary>
    internal void MergeClusters(int start, int end) => MergeClusters(_buffer, start, end);

    private static void MergeClusters(List<GlyphInfo> buffer, int start, int end)
    {
        if (end - start < 2) return;
        int cluster = int.MaxValue;
        for (int i = start; i < end; i++) cluster = Math.Min(cluster, buffer[i].Cluster);
        if (cluster != buffer[end - 1].Cluster)
        {
            while (end < buffer.Count && buffer[end - 1].Cluster == buffer[end].Cluster) end++;
        }

        if (cluster != buffer[start].Cluster)
        {
            while (start > 0 && buffer[start - 1].Cluster == buffer[start].Cluster) start--;
        }

        for (int i = start; i < end; i++) buffer[i].Cluster = cluster;
    }

    /// <summary>
    /// For preprocessing that builds a new buffer: merges the clusters of glyphs
    /// <paramref name="start"/> to <paramref name="end"/> of <paramref name="output"/>, with the
    /// neighbours that shared them; when that reaches the end of the output, the glyphs of
    /// <paramref name="input"/> from <paramref name="next"/> on that shared the last cluster
    /// join too (HarfBuzz's merge_out_clusters).
    /// </summary>
    private static void MergeOutClusters(List<GlyphInfo> output, int start, int end, List<GlyphInfo> input, int next)
    {
        if (end - start < 2) return;
        int cluster = int.MaxValue;
        for (int i = start; i < end; i++) cluster = Math.Min(cluster, output[i].Cluster);
        while (start > 0 && output[start - 1].Cluster == output[start].Cluster) start--;
        while (end < output.Count && output[end - 1].Cluster == output[end].Cluster) end++;
        if (end == output.Count)
        {
            int last = output[end - 1].Cluster;
            for (int i = next; i < input.Count && input[i].Cluster == last; i++) input[i].Cluster = cluster;
        }

        for (int i = start; i < end; i++) output[i].Cluster = cluster;
    }

    /// <summary>
    /// Arabic joining (Unicode chapter 9.2): each letter takes its isolated, final, medial or
    /// initial form. A Mongolian free variation selector takes the form of the letter before it.
    /// </summary>
    private void SetJoiningForms(bool mongolian)
    {
        int n = _buffer.Count;
        var types = new JoiningType[n];
        for (int i = 0; i < n; i++) types[i] = UnicodeData.Joining(_buffer[i].CodePoint);
        for (int i = 0; i < n; i++)
        {
            JoiningType t = types[i];
            if (t is not (JoiningType.D or JoiningType.R or JoiningType.L)) continue;
            int prev = i - 1, next = i + 1;
            while (prev >= 0 && types[prev] == JoiningType.T) prev--;
            while (next < n && types[next] == JoiningType.T) next++;
            bool joinsPrevious = t is JoiningType.D or JoiningType.R && prev >= 0 && types[prev] is JoiningType.D or JoiningType.L or JoiningType.C;
            bool joinsNext = t is JoiningType.D or JoiningType.L && next < n && types[next] is JoiningType.D or JoiningType.R or JoiningType.C;
            _buffer[i].Mask |= (joinsPrevious, joinsNext) switch
            {
                (true, true) => Medi,
                (true, false) => Fina,
                (false, true) => Init,
                _ => Isol,
            };
        }

        if (!mongolian) return;
        for (int i = 1; i < n; i++)
        {
            if (_buffer[i].CodePoint is (>= 0x180B and <= 0x180D) or 0x180F) _buffer[i].Mask |= _buffer[i - 1].Mask & (Isol | Fina | Medi | Init);
        }
    }

    /// <summary>
    /// Glyph classes from GDEF. A font without them gets HarfBuzz's guess: nonspacing marks are
    /// marks, everything else a base.
    /// </summary>
    private void Classify()
    {
        foreach (GlyphInfo g in _buffer)
        {
            if (_gdef?.HasClasses == true) g.Class = _gdef.ClassOf(g.Glyph);
            else g.Class = g.Category == UnicodeCategory.NonSpacingMark && !g.IsIgnorable ? GlyphClass.Mark : GlyphClass.Base;
        }
    }

    /// <summary>True when the font's GSUB has <paramref name="tag"/> for the run's script.</summary>
    internal bool HasSubstitution(string tag) => _gsubFeatures.Any(f => f.Tag == tag);

    private static uint MaskOf(string tag) => tag switch
    {
        "isol" => Isol,
        "fina" => Fina,
        "medi" => Medi,
        "init" => Init,
        "rphf" => Rphf,
        "pref" => Pref,
        "blwf" => Blwf,
        "abvf" => Abvf,
        "half" => Half,
        "pstf" => Pstf,
        "cfar" => Cfar,
        "ljmo" => Ljmo,
        "vjmo" => Vjmo,
        "tjmo" => Tjmo,
        _ => Global,
    };

    /// <summary>
    /// Applies the lookups of <paramref name="stage"/>'s features together, in lookup order. A
    /// lookup in several features takes all their masks.
    /// </summary>
    internal void ApplyStage(LayoutTable? table, List<(string Tag, int[] Lookups)> fontFeatures, IReadOnlyList<ShapingFeature> stage)
    {
        if (table is null) return;
        var lookups = new SortedDictionary<int, (uint Mask, bool AutoZwj, bool AutoZwnj, bool PerSyllable)>();
        foreach ((string tag, int[] indices) in fontFeatures)
        {
            int f = 0;
            while (f < stage.Count && stage[f].Tag != tag) f++;
            if (f == stage.Count) continue;
            ShapingFeature feature = stage[f];
            uint mask = feature.Global ? Global : MaskOf(tag);
            bool autoZwj = !feature.ManualJoiners && !feature.ManualZwj, autoZwnj = !feature.ManualJoiners;
            foreach (int l in indices)
            {
                lookups[l] = lookups.TryGetValue(l, out var e)
                    ? (e.Mask | mask, e.AutoZwj && autoZwj, e.AutoZwnj && autoZwnj, e.PerSyllable || feature.PerSyllable)
                    : (mask, autoZwj, autoZwnj, feature.PerSyllable);
            }
        }

        bool substitute = ReferenceEquals(table, _gsub);
        foreach (KeyValuePair<int, (uint Mask, bool AutoZwj, bool AutoZwnj, bool PerSyllable)> l in lookups)
        {
            _lookupMask = l.Value.Mask;
            _autoZwj = l.Value.AutoZwj;
            _autoZwnj = l.Value.AutoZwnj;
            _perSyllable = l.Value.PerSyllable && substitute;
            _positioning = !substitute;
            ApplyLookup(table, l.Key, substitute);
        }
    }

    private void ApplyLookup(LayoutTable table, int index, bool substitute)
    {
        Lookup? lookup = table.GetLookup(index);
        if (lookup is null) return;
        if (substitute && lookup.Type == 8)
        {
            for (int i = _buffer.Count - 1; i >= 0; i--)
            {
                if (Applies(_buffer[i], lookup) && Budget()) ApplyAt(table, lookup, i, substitute, 0);
            }

            return;
        }

        for (int i = 0; i < _buffer.Count;)
        {
            if (!Applies(_buffer[i], lookup) || !Budget())
            {
                i++;
                continue;
            }

            int next = ApplyAt(table, lookup, i, substitute, 0);
            i = next > i ? next : i + 1;
        }
    }

    /// <summary>A lookup starts at a glyph that carries its feature's mask and that its flags do not skip.</summary>
    private bool Applies(GlyphInfo g, Lookup lookup) => (g.Mask & _lookupMask) != 0 && !Skip(g, lookup);

    private bool Budget() => --_operations > 0;

    /// <summary>Applies the first subtable that matches at <paramref name="i"/>. Returns the index to continue at, or -1.</summary>
    private int ApplyAt(LayoutTable table, Lookup lookup, int i, bool substitute, int depth)
    {
        int saved = _syllable;
        _syllable = _buffer[i].Syllable;
        try
        {
            foreach (int subtable in lookup.Subtables)
            {
                int next;
                try
                {
                    next = substitute ? Substitute(table, lookup, subtable, i, depth) : Position(table, lookup, subtable, i, depth);
                }
                catch (FontFormatException)
                {
                    next = -1; // a damaged subtable does not apply
                }

                if (next >= 0) return next;
            }

            return -1;
        }
        finally
        {
            _syllable = saved;
        }
    }

    /// <summary>True when the lookup's flags skip the glyph: ignored classes, other mark sets or attachment classes.</summary>
    private bool Skip(GlyphInfo g, Lookup lookup) => Skip(g, lookup.Flag, lookup.MarkFilteringSet);

    private bool Skip(GlyphInfo g, int flag, int markSet)
    {
        switch (g.Class)
        {
            case GlyphClass.Base when (flag & 2) != 0:
            case GlyphClass.Ligature when (flag & 4) != 0:
            case GlyphClass.Mark when (flag & 8) != 0:
                return true;
            case GlyphClass.Mark:
                if ((flag & 0x10) != 0 && !(_gdef?.InMarkSet(markSet, g.Glyph) ?? false)) return true;
                int attach = flag >> 8;
                return attach != 0 && _gdef?.MarkAttachClass(g.Glyph) != attach;
            default:
                return false;
        }
    }

    /// <summary>
    /// The next (or previous) glyph a lookup matches against, as HarfBuzz walks: glyphs the flags
    /// skip are passed over; a default ignorable is passed over unless it matches, except that
    /// substitutions see joiners their features handle by hand, and input matching sees ZWNJ.
    /// Input glyphs must carry the lookup's mask, and per syllable lookups stay in the syllable.
    /// Returns -1 when the walk is blocked.
    /// </summary>
    private int Walk(int i, int step, int flag, int markSet, bool context, Func<int, bool>? match)
    {
        for (int j = i + step; j >= 0 && j < _buffer.Count; j += step)
        {
            if (!Budget()) return -1;
            GlyphInfo g = _buffer[j];
            if (Skip(g, flag, markSet)) continue;
            bool maybeSkip = g.IsIgnorable
                && (_positioning || (context && _autoZwnj) || (g.Flags & GlyphFlags.Zwnj) == 0)
                && (context || _autoZwj || (g.Flags & GlyphFlags.Zwj) == 0)
                && (_positioning || (g.Flags & GlyphFlags.Hidden) == 0);

            bool? matched = (!context && (g.Mask & _lookupMask) == 0) || (_perSyllable && _syllable != 0 && g.Syllable != _syllable)
                ? false
                : match?.Invoke(g.Glyph);
            if (matched == true || (matched is null && !maybeSkip)) return j;
            if (!maybeSkip) return -1;
        }

        return -1;
    }

    private int NextInput(int i, Lookup lookup, Func<int, bool>? match = null) => Walk(i, 1, lookup.Flag, lookup.MarkFilteringSet, false, match);

    private int NextContext(int i, Lookup lookup, Func<int, bool> match) => Walk(i, 1, lookup.Flag, lookup.MarkFilteringSet, true, match);

    private int PreviousContext(int i, Lookup lookup, Func<int, bool> match) => Walk(i, -1, lookup.Flag, lookup.MarkFilteringSet, true, match);

    // ---------------------------------------------------------------- GSUB

    private int Substitute(LayoutTable table, Lookup lookup, int sub, int i, int depth)
    {
        ReadOnlySpan<byte> d = table.Data;
        GlyphInfo g = _buffer[i];
        int format = U16(d, sub);
        switch (lookup.Type)
        {
            case 1:
            {
                int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), g.Glyph);
                if (c < 0) return -1;
                Replace(g, format == 1 ? (g.Glyph + I16(d, sub + 4)) & 0xFFFF : U16(d, sub + 6 + 2 * c));
                return i + 1;
            }

            case 2 or 3:
            {
                int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), g.Glyph);
                if (c < 0 || c >= U16(d, sub + 4)) return -1;
                int seq = sub + U16(d, sub + 6 + 2 * c);
                int count = U16(d, seq);
                if (lookup.Type == 3 || count == 1)
                {
                    if (count == 0) return -1;
                    Replace(g, U16(d, seq + 2));
                    return i + 1;
                }

                if (count == 0)
                {
                    _buffer.RemoveAt(i);
                    return i;
                }

                if (_buffer.Count + count - 1 > _maxLength) return -1;
                _operations -= count;
                var replacement = new List<GlyphInfo>(count);
                bool ligature = g.Class == GlyphClass.Ligature;
                for (int k = 0; k < count; k++)
                {
                    GlyphInfo copy = g.Copy();
                    copy.Glyph = U16(d, seq + 2 + 2 * k);
                    copy.Flags |= GlyphFlags.Substituted | GlyphFlags.Changed | GlyphFlags.Multiplied;
                    copy.LigatureId = 0;
                    copy.Component = k;
                    if (k > 0) copy.Text = "";
                    if (_gdef?.HasClasses == true) copy.Class = _gdef.ClassOf(copy.Glyph);
                    else if (ligature) copy.Class = GlyphClass.Base;
                    replacement.Add(copy);
                }

                _buffer.RemoveAt(i);
                _buffer.InsertRange(i, replacement);
                return i + count;
            }

            case 4:
                return Ligature(d, lookup, sub, i);
            case 5 or 6:
                return Context(table, lookup, sub, i, depth, substitute: true);
            case 8:
                return ReverseChain(table.Data, lookup, sub, i);
            default:
                return -1;
        }
    }

    private void Replace(GlyphInfo g, int glyph)
    {
        g.Glyph = glyph;
        g.Flags |= GlyphFlags.Substituted | GlyphFlags.Changed;
        if (_gdef?.HasClasses == true) g.Class = _gdef.ClassOf(glyph);
    }

    private int Ligature(ReadOnlySpan<byte> d, Lookup lookup, int sub, int i)
    {
        int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), _buffer[i].Glyph);
        if (c < 0 || c >= U16(d, sub + 4)) return -1;
        int set = sub + U16(d, sub + 6 + 2 * c);
        int count = U16(d, set);
        for (int l = 0; l < count; l++)
        {
            int lig = set + U16(d, set + 2 + 2 * l);
            int components = U16(d, lig + 2);
            if (components == 0 || !Budget()) continue;
            var positions = new int[components];
            positions[0] = i;
            bool match = true;
            for (int k = 1; k < components && match; k++)
            {
                int expected = U16(d, lig + 4 + 2 * (k - 1));
                int j = NextInput(positions[k - 1], lookup, glyph => glyph == expected);
                match = j >= 0;
                if (match) positions[k] = j;
            }

            if (!match) continue;

            // The first glyph becomes the ligature; marks skipped between components remember which component they belong to.
            GlyphInfo first = _buffer[i];
            int id = _nextLigatureId++;
            MergeClusters(i, positions[^1] + 1);
            first.Text = string.Concat(positions.Select(p => _buffer[p].Text));
            first.Glyph = U16(d, lig);
            first.LigatureId = id;
            first.Component = 0;
            first.Flags = (first.Flags | GlyphFlags.Substituted | GlyphFlags.Changed | GlyphFlags.Ligated) & ~GlyphFlags.Multiplied;
            if (_gdef?.HasClasses == true) first.Class = _gdef.ClassOf(first.Glyph);
            else first.Class = GlyphClass.Ligature;
            for (int k = 1; k < components; k++)
            {
                for (int m = positions[k - 1] + 1; m < positions[k]; m++)
                {
                    _buffer[m].LigatureId = id;
                    _buffer[m].Component = k;
                }
            }

            for (int k = components - 1; k >= 1; k--) _buffer.RemoveAt(positions[k]);
            return i + 1;
        }

        return -1;
    }

    private int ReverseChain(byte[] d, Lookup lookup, int sub, int i)
    {
        int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), _buffer[i].Glyph);
        if (c < 0) return -1;
        int at = sub + 4;
        int backtrack = U16(d, at);
        int j = i;
        for (int k = 0; k < backtrack; k++)
        {
            int coverage = sub + U16(d, at + 2 + 2 * k);
            j = PreviousContext(j, lookup, glyph => Kerning.CoverageIndex(d, coverage, glyph) >= 0);
            if (j < 0) return -1;
        }

        at += 2 + 2 * backtrack;
        int lookahead = U16(d, at);
        j = i;
        for (int k = 0; k < lookahead; k++)
        {
            int coverage = sub + U16(d, at + 2 + 2 * k);
            j = NextContext(j, lookup, glyph => Kerning.CoverageIndex(d, coverage, glyph) >= 0);
            if (j < 0) return -1;
        }

        at += 2 + 2 * lookahead;
        if (c >= U16(d, at)) return -1;
        Replace(_buffer[i], U16(d, at + 2 + 2 * c));
        return i + 1;
    }

    // ---------------------------------------------------------------- Contexts (GSUB 5, 6; GPOS 7, 8)

    private int Context(LayoutTable table, Lookup lookup, int sub, int i, int depth, bool substitute)
    {
        byte[] d = table.Data;
        int format = U16(d, sub);
        bool chained = lookup.Type == (substitute ? 6 : 8);
        int glyph = _buffer[i].Glyph;
        int[]? positions = null;
        int records = -1, recordCount = 0;

        if (format == 3)
        {
            int at = sub + 2;
            if (chained)
            {
                int back = U16(d, at);
                if (!MatchBackward(i, back, k => CoverageAt(d, sub, at + 2 + 2 * k), lookup)) return -1;
                at += 2 + 2 * back;
            }

            int inputCount = U16(d, at);
            int lookupCount = chained ? -1 : U16(d, at + 2);
            int inputs = chained ? at + 2 : at + 4;
            if (inputCount == 0) return -1;
            positions = MatchForward(i, inputCount, k => CoverageAt(d, sub, inputs + 2 * k), lookup, first: true);
            if (positions is null) return -1;
            at = inputs + 2 * inputCount;
            if (chained)
            {
                int ahead = U16(d, at);
                if (!MatchAhead(positions[^1], ahead, k => CoverageAt(d, sub, at + 2 + 2 * k), lookup)) return -1;
                at += 2 + 2 * ahead;
                lookupCount = U16(d, at);
                at += 2;
            }

            records = at;
            recordCount = lookupCount;
        }
        else if (format is 1 or 2)
        {
            int coverage = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), glyph);
            if (coverage < 0) return -1;
            int backDef = 0, inputDef = 0, aheadDef = 0, sets;
            if (format == 2)
            {
                if (chained)
                {
                    backDef = sub + U16(d, sub + 4);
                    inputDef = sub + U16(d, sub + 6);
                    aheadDef = sub + U16(d, sub + 8);
                    sets = sub + 10;
                }
                else
                {
                    inputDef = sub + U16(d, sub + 4);
                    sets = sub + 6;
                }
            }
            else
            {
                sets = sub + 4;
            }

            int setIndex = format == 2 ? Kerning.ClassOf(d, inputDef, glyph) : coverage;
            if (setIndex >= U16(d, sets)) return -1;
            int setOffset = U16(d, sets + 2 + 2 * setIndex);
            if (setOffset == 0) return -1;
            int set = sub + setOffset;
            int rules = U16(d, set);

            // Format 1 compares glyph ids, format 2 classes.
            bool Same(int candidate, int value, int classDef) =>
                format == 1 ? candidate == value : Kerning.ClassOf(d, classDef, candidate) == value;

            for (int r = 0; r < rules && positions is null; r++)
            {
                if (!Budget()) return -1;
                int at = set + U16(d, set + 2 + 2 * r);
                if (chained)
                {
                    int back = U16(d, at);
                    int backStart = at + 2;
                    if (!MatchBackward(i, back, k => g => Same(g, U16(d, backStart + 2 * k), backDef), lookup))
                    {
                        continue;
                    }

                    at += 2 + 2 * back;
                }

                int inputCount = U16(d, at);
                int lookupCount = chained ? -1 : U16(d, at + 2);
                int inputs = chained ? at + 2 : at + 4;
                if (inputCount == 0) continue;
                int[]? matched = MatchForward(i, inputCount, k => g => Same(g, U16(d, inputs + 2 * (k - 1)), inputDef), lookup, first: false);
                if (matched is null) continue;
                at = inputs + 2 * (inputCount - 1);
                if (chained)
                {
                    int ahead = U16(d, at);
                    int aheadStart = at + 2;
                    if (!MatchAhead(matched[^1], ahead, k => g => Same(g, U16(d, aheadStart + 2 * k), aheadDef), lookup)) continue;
                    at += 2 + 2 * ahead;
                    lookupCount = U16(d, at);
                    at += 2;
                }

                positions = matched;
                records = at;
                recordCount = lookupCount;
            }

            if (positions is null) return -1;
        }
        else
        {
            return -1;
        }

        int end = positions[^1] + 1;
        if (depth >= MaxNesting) return end;

        // As in HarfBuzz: glyphs a nested lookup adds become sequence positions right after the
        // one it ran at, and glyphs it removes take the positions after it away, so later
        // records index into the sequence as it now is.
        var sequence = new List<int>(positions);
        for (int r = 0; r < recordCount; r++)
        {
            int sequenceIndex = U16(d, records + 4 * r);
            int nested = U16(d, records + 4 * r + 2);
            if (sequenceIndex >= sequence.Count) continue;
            Lookup? inner = table.GetLookup(nested);
            if (inner is null || !Budget()) continue;
            int before = _buffer.Count;
            int at = sequence[sequenceIndex];
            if (at >= _buffer.Count) continue;
            ApplyAt(table, inner, at, substitute, depth + 1);
            int delta = _buffer.Count - before;
            if (delta == 0) continue;
            end += delta;
            if (end < at)
            {
                delta += at - end;
                end = at;
            }

            int next = sequenceIndex + 1;
            if (delta > 0)
            {
                if (sequence.Count + delta > MaxContextLength) break;
                for (int k = 0; k < delta; k++) sequence.Insert(next + k, at + k + 1);
                next += delta;
            }
            else
            {
                sequence.RemoveRange(next, Math.Min(-delta, sequence.Count - next));
            }

            for (; next < sequence.Count; next++) sequence[next] += delta;
        }

        return Math.Min(end, _buffer.Count);
    }

    private static Func<int, bool> CoverageAt(byte[] d, int sub, int offsetField) =>
        g => Kerning.CoverageIndex(d, sub + U16(d, offsetField), g) >= 0;

    private int[]? MatchForward(int i, int count, Func<int, Func<int, bool>> test, Lookup lookup, bool first)
    {
        var positions = new int[count];
        positions[0] = i;
        if (first && !test(0)(_buffer[i].Glyph)) return null;
        for (int k = 1; k < count; k++)
        {
            int j = NextInput(positions[k - 1], lookup, test(k));
            if (j < 0) return null;
            positions[k] = j;
        }

        return positions;
    }

    private bool MatchBackward(int i, int count, Func<int, Func<int, bool>> test, Lookup lookup)
    {
        int j = i;
        for (int k = 0; k < count; k++)
        {
            j = PreviousContext(j, lookup, test(k));
            if (j < 0) return false;
        }

        return true;
    }

    private bool MatchAhead(int last, int count, Func<int, Func<int, bool>> test, Lookup lookup)
    {
        int j = last;
        for (int k = 0; k < count; k++)
        {
            j = NextContext(j, lookup, test(k));
            if (j < 0) return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- GPOS

    private int Position(LayoutTable table, Lookup lookup, int sub, int i, int depth)
    {
        ReadOnlySpan<byte> d = table.Data;
        GlyphInfo g = _buffer[i];
        int format = U16(d, sub);
        switch (lookup.Type)
        {
            case 1:
            {
                int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), g.Glyph);
                if (c < 0) return -1;
                int valueFormat = U16(d, sub + 4);
                int value = format == 1 ? sub + 6 : sub + 8 + c * ValueSize(valueFormat);
                ApplyValue(d, sub, value, valueFormat, g);
                return i + 1;
            }

            case 2:
                return Pair(d, lookup, sub, i);
            case 3:
                return Cursive(d, lookup, sub, i);
            case 4 or 5 or 6:
                return MarkAttach(d, lookup, sub, i);
            case 7 or 8:
                return Context(table, lookup, sub, i, depth, substitute: false);
            default:
                return -1;
        }
    }

    private int Pair(ReadOnlySpan<byte> d, Lookup lookup, int sub, int i)
    {
        int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), _buffer[i].Glyph);
        if (c < 0) return -1;
        int j = NextInput(i, lookup);
        if (j < 0) return -1;
        int format = U16(d, sub), format1 = U16(d, sub + 4), format2 = U16(d, sub + 6);
        int size1 = ValueSize(format1), size2 = ValueSize(format2);
        int right = _buffer[j].Glyph;
        int record;
        if (format == 1)
        {
            if (c >= U16(d, sub + 8)) return -1;
            int set = sub + U16(d, sub + 10 + 2 * c);
            int count = U16(d, set), stride = 2 + size1 + size2;
            record = -1;
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int second = U16(d, set + 2 + stride * mid);
                if (second == right)
                {
                    record = set + 2 + stride * mid + 2;
                    break;
                }

                if (second < right) lo = mid + 1;
                else hi = mid - 1;
            }

            // Device offsets in a pair set count from the pair set.
            if (record < 0) return -1;
            ApplyValue(d, set, record, format1, _buffer[i]);
            ApplyValue(d, set, record + size1, format2, _buffer[j]);
        }
        else if (format == 2)
        {
            int class1 = Kerning.ClassOf(d, sub + U16(d, sub + 8), _buffer[i].Glyph);
            int class2 = Kerning.ClassOf(d, sub + U16(d, sub + 10), right);
            int class1Count = U16(d, sub + 12), class2Count = U16(d, sub + 14);
            if (class1 >= class1Count || class2 >= class2Count) return -1;
            record = sub + 16 + (class1 * class2Count + class2) * (size1 + size2);
            ApplyValue(d, sub, record, format1, _buffer[i]);
            ApplyValue(d, sub, record + size1, format2, _buffer[j]);
        }
        else
        {
            return -1;
        }

        return format2 != 0 ? j + 1 : j;
    }

    private int Cursive(ReadOnlySpan<byte> d, Lookup lookup, int sub, int i)
    {
        int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), _buffer[i].Glyph);
        if (c < 0 || c >= U16(d, sub + 4)) return -1;
        int exitOffset = U16(d, sub + 6 + 4 * c + 2);
        if (exitOffset == 0) return -1;
        int j = NextInput(i, lookup);
        if (j < 0) return -1;
        int c2 = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), _buffer[j].Glyph);
        if (c2 < 0 || c2 >= U16(d, sub + 4)) return -1;
        int entryOffset = U16(d, sub + 6 + 4 * c2);
        if (entryOffset == 0) return -1;
        (double exitX, double exitY) = Anchor(d, sub + exitOffset);
        (double entryX, double entryY) = Anchor(d, sub + entryOffset);
        GlyphInfo a = _buffer[i], b = _buffer[j];
        if (!_rtl)
        {
            a.XAdvance = Round(exitX) + a.XOffset;
            int delta = Round(entryX) + b.XOffset;
            b.XAdvance -= delta;
            b.XOffset -= delta;
        }
        else
        {
            int delta = Round(exitX) + a.XOffset;
            a.XAdvance -= delta;
            a.XOffset -= delta;
            b.XAdvance = Round(entryX) + b.XOffset;
        }

        // The child hangs off its parent: with RightToLeft (flag bit 0) the earlier glyph is the
        // child. Its vertical offset becomes the anchors' difference; the parent's is added at the end.
        if ((lookup.Flag & 1) != 0)
        {
            a.CursiveParent = j;
            a.YOffset = Round(entryY - exitY);
        }
        else
        {
            b.CursiveParent = i;
            b.YOffset = Round(exitY - entryY);
        }

        return j;
    }

    private int MarkAttach(ReadOnlySpan<byte> d, Lookup lookup, int sub, int i)
    {
        GlyphInfo mark = _buffer[i];
        int markIndex = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), mark.Glyph);
        if (markIndex < 0) return -1;

        int target;
        if (lookup.Type == 6)
        {
            // The previous mark, with the lookup's mark filtering but none of its ignore flags.
            target = Walk(i, -1, lookup.Flag & ~0x0E, lookup.MarkFilteringSet, false, null);
            if (target < 0 || _buffer[target].Class != GlyphClass.Mark) return -1;
            GlyphInfo prev = _buffer[target];
            bool sameLigature = mark.LigatureId == prev.LigatureId
                ? mark.LigatureId == 0 || mark.Component == prev.Component
                : (mark.LigatureId > 0 && mark.Component == 0) || (prev.LigatureId > 0 && prev.Component == 0);
            if (!sameLigature) return -1;
        }
        else
        {
            // The base or ligature: the previous glyph that is not a mark or a default ignorable
            // (a ZWJ counts when the feature handles joiners by hand).
            target = i - 1;
            while (target >= 0 && (_buffer[target].Class == GlyphClass.Mark
                || (_buffer[target].IsIgnorable && (_autoZwj || (_buffer[target].Flags & GlyphFlags.Zwj) == 0))))
            {
                target--;
            }

            if (target < 0) return -1;
        }

        int targetIndex = Kerning.CoverageIndex(d, sub + U16(d, sub + 4), _buffer[target].Glyph);
        if (targetIndex < 0) return -1;
        int classCount = U16(d, sub + 6);
        int markArray = sub + U16(d, sub + 8), targetArray = sub + U16(d, sub + 10);
        if (markIndex >= U16(d, markArray)) return -1;
        int markClass = U16(d, markArray + 2 + 4 * markIndex);
        if (markClass >= classCount) return -1;
        int markAnchor = markArray + U16(d, markArray + 2 + 4 * markIndex + 2);

        int anchorOffset;
        int anchorBase;
        if (lookup.Type == 5)
        {
            if (targetIndex >= U16(d, targetArray)) return -1;
            int attach = targetArray + U16(d, targetArray + 2 + 2 * targetIndex);
            int components = U16(d, attach);
            if (components == 0) return -1;
            GlyphInfo lig = _buffer[target];
            int component = mark.LigatureId == lig.LigatureId && mark.LigatureId != 0 && mark.Component > 0
                ? Math.Min(mark.Component, components) - 1
                : components - 1;
            anchorOffset = U16(d, attach + 2 + 2 * (component * classCount + markClass));
            anchorBase = attach;
        }
        else
        {
            if (targetIndex >= U16(d, targetArray)) return -1;
            anchorOffset = U16(d, targetArray + 2 + 2 * (targetIndex * classCount + markClass));
            anchorBase = targetArray;
        }

        if (anchorOffset == 0) return -1;
        (double bx, double by) = Anchor(d, anchorBase + anchorOffset);
        (double mx, double my) = Anchor(d, markAnchor);
        // Attaching replaces earlier placement; lookups after this one add to the offset again. As
        // in HarfBuzz, the vertical offset is fixed now, from the base's offset along its cursive
        // chain at this moment; only the horizontal position follows the base at the end.
        mark.AttachTo = target;
        mark.AttachDx = Round(bx - mx);
        mark.XOffset = 0;
        mark.YOffset = Round(by - my) + CrossOffset(target);
        return i + 1;
    }

    /// <summary>
    /// A glyph's vertical offset, plus those of its cursive ancestors as they stand now. Every
    /// step is charged to the operation budget: many marks on a long chain cannot add up to
    /// quadratic work.
    /// </summary>
    private int CrossOffset(int glyph)
    {
        int offset = _buffer[glyph].YOffset;
        for (int steps = 0, at = _buffer[glyph].CursiveParent; at >= 0 && at < _buffer.Count && steps < _buffer.Count && Budget(); steps++, at = _buffer[at].CursiveParent)
        {
            offset += _buffer[at].YOffset;
        }

        return offset;
    }

    /// <summary>
    /// An anchor's coordinates; format 3 adds its variation deltas, unrounded: as in HarfBuzz,
    /// what is rounded is the position computed from the anchors.
    /// </summary>
    private (double X, double Y) Anchor(ReadOnlySpan<byte> d, int at)
    {
        double x = I16(d, at + 2), y = I16(d, at + 4);
        if (U16(d, at) != 3 || _variations is null) return (x, y);
        return (x + DeviceDelta(d, at, U16(d, at + 6)), y + DeviceDelta(d, at, U16(d, at + 8)));
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    private static int ValueSize(int format)
    {
        int n = 0;
        for (int v = format & 0xFF; v != 0; v &= v - 1) n++;
        return 2 * n;
    }

    /// <summary>
    /// Applies a value record's placement and advance. Device tables that are variation indices
    /// add the variable font's deltas at the instance; hinting device tables are ignored (no
    /// hinting at PDF resolution). Device offsets count from <paramref name="table"/>.
    /// </summary>
    private void ApplyValue(ReadOnlySpan<byte> d, int table, int at, int format, GlyphInfo g)
    {
        int offset = 0;
        int x = 0, y = 0, advance = 0;
        if ((format & 1) != 0)
        {
            x = I16(d, at);
            offset += 2;
        }

        if ((format & 2) != 0)
        {
            y = I16(d, at + offset);
            offset += 2;
        }

        if ((format & 4) != 0)
        {
            advance = I16(d, at + offset);
            offset += 2;
        }

        if ((format & 8) != 0) offset += 2;
        if (_variations is not null)
        {
            if ((format & 0x10) != 0)
            {
                x += Round(DeviceDelta(d, table, U16(d, at + offset)));
                offset += 2;
            }

            if ((format & 0x20) != 0)
            {
                y += Round(DeviceDelta(d, table, U16(d, at + offset)));
                offset += 2;
            }

            if ((format & 0x40) != 0) advance += Round(DeviceDelta(d, table, U16(d, at + offset)));
        }

        g.XOffset += x;
        g.YOffset += y;
        g.XAdvance += advance;
    }

    /// <summary>The delta of a VariationIndex table (delta format 0x8000) at the instance; 0 for anything else.</summary>
    private double DeviceDelta(ReadOnlySpan<byte> d, int table, int offset)
    {
        if (offset == 0 || _variations is null) return 0;
        int device = table + offset;
        if (U16(d, device + 4) != 0x8000) return 0;
        return _variations.Delta(U16(d, device), U16(d, device + 2), _coordinates);
    }

    /// <summary>
    /// Legacy pair kerning, split between the pair as HarfBuzz does: half on the first advance,
    /// the rest on the second glyph's advance and offset. Marks are skipped.
    /// </summary>
    private void LegacyKern()
    {
        for (int i = 0; i < _buffer.Count; i++)
        {
            if (_buffer[i].Class == GlyphClass.Mark) continue;
            int j = i + 1;
            while (j < _buffer.Count && _buffer[j].Class == GlyphClass.Mark) j++;
            if (j >= _buffer.Count) break;
            int kern = _legacyKerning!.Get(_buffer[i].Glyph, _buffer[j].Glyph);
            if (kern == 0) continue;
            int first = kern >> 1, second = kern - first;
            _buffer[i].XAdvance += first;
            _buffer[j].XAdvance += second;
            _buffer[j].XOffset += second;
        }
    }

    /// <summary>Joiners, bidi controls and other default ignorables draw as an empty space glyph, as in HarfBuzz.</summary>
    private void HideDefaultIgnorables()
    {
        int space = _font.TryGetGlyph(' ', out int s) ? s : 0;
        foreach (GlyphInfo g in _buffer)
        {
            if (!g.IsIgnorable) continue;
            g.Glyph = space;
            g.XAdvance = 0;
            g.XOffset = 0;
            g.YOffset = 0;
        }
    }

    /// <summary>
    /// The default ignorable characters HarfBuzz hides. The Hangul fillers and shorthand format
    /// controls are left alone: fonts draw them as spacing glyphs.
    /// </summary>
    private static bool IsDefaultIgnorable(int cp) =>
        cp is 0x00AD or 0x034F or 0x061C or 0xFEFF
        || (cp >= 0x17B4 && cp <= 0x17B5) || (cp >= 0x180B && cp <= 0x180F) || (cp >= 0x200B && cp <= 0x200F)
        || (cp >= 0x202A && cp <= 0x202E) || (cp >= 0x2060 && cp <= 0x206F) || (cp >= 0xFE00 && cp <= 0xFE0F)
        || (cp >= 0xFFF0 && cp <= 0xFFF8) || (cp >= 0x1D173 && cp <= 0x1D17A) || (cp >= 0xE0000 && cp <= 0xE0FFF);

    /// <summary>
    /// Marks take no space. In left to right text from a font without GPOS, a mark also moves back
    /// over its base, as in HarfBuzz; with GPOS, placing marks is the font's business.
    /// </summary>
    private void ZeroMarks()
    {
        bool adjust = !_rtl && _gpos is null;
        foreach (GlyphInfo g in _buffer)
        {
            if (g.Class != GlyphClass.Mark) continue;
            if (adjust) g.XOffset -= g.XAdvance;
            g.XAdvance = 0;
        }
    }

    /// <summary>Turns mark attachments and cursive chains into offsets relative to each glyph's pen position.</summary>
    private void ResolveAttachments()
    {
        int n = _buffer.Count;
        if (!_buffer.Any(g => g.AttachTo >= 0 || g.CursiveParent >= 0)) return;

        // Pen position of each glyph in visual order (right to left runs are drawn reversed).
        var pen = new int[n];
        int x = 0;
        for (int v = 0; v < n; v++)
        {
            int k = _rtl ? n - 1 - v : v;
            pen[k] = x;
            x += _buffer[k].XAdvance;
        }

        var resolved = new bool[n];
        for (int k = 0; k < n; k++) ResolveY(k, resolved);

        var originX = new int[n];
        for (int k = 0; k < n; k++)
        {
            GlyphInfo g = _buffer[k];
            if (g.AttachTo >= 0 && g.AttachTo < k)
            {
                originX[k] = originX[g.AttachTo] + g.AttachDx + g.XOffset;
                g.XOffset = originX[k] - pen[k];
            }
            else
            {
                originX[k] = pen[k] + g.XOffset;
            }
        }
    }

    /// <summary>
    /// Adds the parent's vertical offset to each glyph of a cursive chain, parents first. The
    /// chain is walked with an explicit list: it can be as long as the text, too deep to recurse.
    /// </summary>
    private void ResolveY(int k, bool[] resolved)
    {
        // The chain from k up to the first glyph already resolved, out of range, or seen (a cycle).
        var chain = new List<int>();
        var onChain = new HashSet<int>();
        for (int at = k; at >= 0 && at < _buffer.Count && !resolved[at] && onChain.Add(at); at = _buffer[at].CursiveParent) chain.Add(at);
        if (chain.Count == 0) return;

        int topParent = _buffer[chain[^1]].CursiveParent;
        bool topHasParent = topParent >= 0 && topParent < _buffer.Count && resolved[topParent] && !onChain.Contains(topParent);
        for (int c = chain.Count - 1; c >= 0; c--)
        {
            GlyphInfo g = _buffer[chain[c]];
            resolved[chain[c]] = true;
            if (c < chain.Count - 1 || topHasParent) g.YOffset += _buffer[g.CursiveParent].YOffset;
        }
    }
}
