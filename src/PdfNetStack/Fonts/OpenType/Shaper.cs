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
    public int LigatureId;
    public int Component;
    public int XAdvance;
    public int XOffset;
    public int YOffset;
    public int AttachTo = -1;
    public int AttachDx;
    public int CursiveParent = -1;

    public GlyphInfo Copy() => (GlyphInfo)MemberwiseClone();
}

/// <summary>
/// OpenType shaping of one run (one font, script and direction): character to glyph mapping,
/// Arabic joining forms, GSUB substitutions (single, multiple, alternate, ligature, contextual,
/// chained, reverse chained) and GPOS positioning (single, pair, cursive, mark to base, ligature
/// and mark, contextual). Feature selection and ordering follow HarfBuzz's default and Arabic
/// shapers. Written from ISO/IEC 14496-22.
/// </summary>
internal sealed class Shaper
{
    private const uint Global = 1, Isol = 2, Fina = 4, Medi = 8, Init = 16;
    private const int MaxNesting = 6;

    private static readonly string[][] ArabicStages =
    [
        ["ccmp", "locl"], ["isol"], ["fina"], ["medi"], ["init"], ["rlig"], ["calt", "rclt"], ["mset"],
        ["liga", "clig", "rvrn"],
    ];

    private static readonly string[][] DefaultStages = [["rvrn", "ccmp", "locl", "rlig", "calt", "clig", "liga", "rclt"]];
    private static readonly string[] PositionFeatures = ["abvm", "blwm", "curs", "dist", "kern", "mark", "mkmk"];

    private readonly OpenTypeFile _font;
    private readonly LayoutTable? _gsub;
    private readonly LayoutTable? _gpos;
    private readonly GlyphDefinitions? _gdef;
    private readonly Kerning? _legacyKerning;
    private List<GlyphInfo> _buffer = [];
    private bool _rtl;
    private int _operations;
    private int _nextLigatureId = 1;
    private int _maxLength;

    /// <summary>A shaper for one call; the tables are shared and read only.</summary>
    public Shaper(ShapingTables tables)
    {
        _font = tables.Font;
        _gsub = tables.Gsub;
        _gpos = tables.Gpos;
        _gdef = tables.Gdef;
        _legacyKerning = tables.Kerning;
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
            int mapped = rightToLeft ? UnicodeData.Mirror(cp) : cp;
            if (!_font.TryGetGlyph(mapped, out int glyph) && !_font.TryGetGlyph(cp, out glyph)) glyph = 0;
            _buffer.Add(new GlyphInfo
            {
                Glyph = glyph,
                Cluster = i,
                Text = text.Substring(i, char.IsSurrogatePair(text, i) ? 2 : 1),
                CodePoint = cp,
                Mask = Global,
            });
        }

        MergeClusters();

        // Every lookup costs at most this many steps per glyph; a crafted font cannot run away.
        _operations = Math.Max(16384, _buffer.Count * 64);

        // Multiple substitutions may grow the buffer, but never past this (HarfBuzz caps it too).
        _maxLength = Math.Max(64, _buffer.Count * 32);
        bool arabic = script is "arab" or "syrc" or "nko " or "mong";
        if (arabic) SetJoiningForms();
        Classify();

        if (_gsub is not null)
        {
            foreach (string[] stage in arabic ? ArabicStages : DefaultStages) ApplyStage(_gsub, stage, script, substitute: true);
        }

        foreach (GlyphInfo g in _buffer) g.XAdvance = _font.Advance(g.Glyph);
        bool gposKern = false;
        if (_gpos is not null)
        {
            gposKern = Collect(_gpos, ["kern"], script).Count > 0;
            foreach ((int index, uint mask) in Collect(_gpos, PositionFeatures, script)) ApplyLookup(_gpos, index, mask, substitute: false);
        }

        // As HarfBuzz: the kern table applies when GPOS does not kern this script.
        if (!gposKern && _legacyKerning is not null) LegacyKern();
        HideDefaultIgnorables();
        ZeroMarks();
        ResolveAttachments();
        return _buffer;
    }

    /// <summary>
    /// Combining marks and zero width joiners belong to the cluster of the character before them,
    /// so a base and its marks are selected, copied and broken as one (HarfBuzz cluster level 0).
    /// </summary>
    private void MergeClusters()
    {
        for (int i = 1; i < _buffer.Count; i++)
        {
            GlyphInfo g = _buffer[i];
            bool continuation = g.CodePoint == 0x200D || CharUnicodeInfo.GetUnicodeCategory(g.Text, 0)
                is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (continuation) g.Cluster = _buffer[i - 1].Cluster;
        }
    }

    /// <summary>Arabic joining (Unicode chapter 9.2): each letter takes its isolated, final, medial or initial form.</summary>
    private void SetJoiningForms()
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
    }

    private void Classify()
    {
        foreach (GlyphInfo g in _buffer)
        {
            g.Class = _gdef?.ClassOf(g.Glyph) ?? GlyphClass.Unclassified;

            // Without GDEF, combining marks are marks by their Unicode category.
            if (_gdef is null && CharUnicodeInfo.GetUnicodeCategory(g.Text, 0) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                g.Class = GlyphClass.Mark;
            }
        }
    }

    /// <summary>The lookups of the given features, in lookup order, each with the masks it applies to.</summary>
    private List<(int Index, uint Mask)> Collect(LayoutTable table, string[] features, string script)
    {
        var masks = new SortedDictionary<int, uint>();
        foreach ((string tag, int[] lookups) in table.Features(script))
        {
            uint mask = tag switch
            {
                "isol" => Isol,
                "fina" => Fina,
                "medi" => Medi,
                "init" => Init,
                _ => Array.IndexOf(features, tag) >= 0 ? Global : 0,
            };
            if (mask == 0 || Array.IndexOf(features, tag) < 0) continue;
            foreach (int l in lookups) masks[l] = masks.TryGetValue(l, out uint m) ? m | mask : mask;
        }

        return [.. masks.Select(e => (e.Key, e.Value))];
    }

    private void ApplyStage(LayoutTable table, string[] features, string script, bool substitute)
    {
        foreach ((int index, uint mask) in Collect(table, features, script)) ApplyLookup(table, index, mask, substitute);
    }

    private void ApplyLookup(LayoutTable table, int index, uint mask, bool substitute)
    {
        Lookup? lookup = table.GetLookup(index);
        if (lookup is null) return;
        if (substitute && lookup.Type == 8)
        {
            for (int i = _buffer.Count - 1; i >= 0; i--)
            {
                if ((_buffer[i].Mask & mask) != 0 && !Skip(_buffer[i], lookup) && Budget()) ApplyAt(table, lookup, i, substitute, 0);
            }

            return;
        }

        for (int i = 0; i < _buffer.Count;)
        {
            if ((_buffer[i].Mask & mask) == 0 || Skip(_buffer[i], lookup) || !Budget())
            {
                i++;
                continue;
            }

            int next = ApplyAt(table, lookup, i, substitute, 0);
            i = next > i ? next : i + 1;
        }
    }

    private bool Budget() => --_operations > 0;

    /// <summary>Applies the first subtable that matches at <paramref name="i"/>. Returns the index to continue at, or -1.</summary>
    private int ApplyAt(LayoutTable table, Lookup lookup, int i, bool substitute, int depth)
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

    private bool Skip(GlyphInfo g, Lookup lookup)
    {
        int flag = lookup.Flag;
        switch (g.Class)
        {
            case GlyphClass.Base when (flag & 2) != 0:
            case GlyphClass.Ligature when (flag & 4) != 0:
            case GlyphClass.Mark when (flag & 8) != 0:
                return true;
            case GlyphClass.Mark:
                if ((flag & 0x10) != 0 && !(_gdef?.InMarkSet(lookup.MarkFilteringSet, g.Glyph) ?? false)) return true;
                int attach = flag >> 8;
                return attach != 0 && _gdef?.MarkAttachClass(g.Glyph) != attach;
            default:
                return false;
        }
    }

    private int NextIndex(int i, Lookup lookup)
    {
        for (int j = i + 1; j < _buffer.Count; j++)
        {
            if (!Skip(_buffer[j], lookup)) return j;
        }

        return -1;
    }

    private int PreviousIndex(int i, Lookup lookup)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            if (!Skip(_buffer[j], lookup)) return j;
        }

        return -1;
    }

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
                g.Glyph = format == 1 ? (g.Glyph + I16(d, sub + 4)) & 0xFFFF : U16(d, sub + 6 + 2 * c);
                Reclassify(g);
                return i + 1;
            }

            case 2 or 3:
            {
                int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), g.Glyph);
                if (c < 0 || c >= U16(d, sub + 4)) return -1;
                int seq = sub + U16(d, sub + 6 + 2 * c);
                int count = U16(d, seq);
                if (lookup.Type == 3)
                {
                    if (count == 0) return -1;
                    g.Glyph = U16(d, seq + 2);
                    Reclassify(g);
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
                for (int k = 0; k < count; k++)
                {
                    GlyphInfo copy = g.Copy();
                    copy.Glyph = U16(d, seq + 2 + 2 * k);
                    if (k > 0) copy.Text = "";
                    Reclassify(copy);
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
                return ReverseChain(d, lookup, sub, i);
            default:
                return -1;
        }
    }

    private void Reclassify(GlyphInfo g)
    {
        if (_gdef is not null) g.Class = _gdef.ClassOf(g.Glyph);
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
                int j = NextIndex(positions[k - 1], lookup);
                match = j >= 0 && _buffer[j].Glyph == U16(d, lig + 4 + 2 * (k - 1));
                if (match) positions[k] = j;
            }

            if (!match) continue;

            // The first glyph becomes the ligature; marks skipped between components remember which component they belong to.
            GlyphInfo first = _buffer[i];
            var merged = new HashSet<int>(positions.Select(p => _buffer[p].Cluster));
            int id = _nextLigatureId++;
            first.Glyph = U16(d, lig);
            first.LigatureId = id;
            first.Component = 0;
            first.Text = string.Concat(positions.Select(p => _buffer[p].Text));
            Reclassify(first);
            if (first.Class == GlyphClass.Unclassified) first.Class = GlyphClass.Ligature;
            for (int k = 1; k < components; k++)
            {
                for (int m = positions[k - 1] + 1; m < positions[k]; m++)
                {
                    _buffer[m].LigatureId = id;
                    _buffer[m].Component = k;
                }
            }

            // Clusters merge whole: whatever shared a cluster with a component joins the ligature's.
            int cluster = merged.Min();
            foreach (GlyphInfo other in _buffer)
            {
                if (merged.Contains(other.Cluster)) other.Cluster = cluster;
            }

            for (int k = components - 1; k >= 1; k--) _buffer.RemoveAt(positions[k]);
            return i + 1;
        }

        return -1;
    }

    private int ReverseChain(ReadOnlySpan<byte> d, Lookup lookup, int sub, int i)
    {
        int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), _buffer[i].Glyph);
        if (c < 0) return -1;
        int at = sub + 4;
        int backtrack = U16(d, at);
        int j = i;
        for (int k = 0; k < backtrack; k++)
        {
            j = PreviousIndex(j, lookup);
            if (j < 0 || Kerning.CoverageIndex(d, sub + U16(d, at + 2 + 2 * k), _buffer[j].Glyph) < 0) return -1;
        }

        at += 2 + 2 * backtrack;
        int lookahead = U16(d, at);
        j = i;
        for (int k = 0; k < lookahead; k++)
        {
            j = NextIndex(j, lookup);
            if (j < 0 || Kerning.CoverageIndex(d, sub + U16(d, at + 2 + 2 * k), _buffer[j].Glyph) < 0) return -1;
        }

        at += 2 + 2 * lookahead;
        if (c >= U16(d, at)) return -1;
        _buffer[i].Glyph = U16(d, at + 2 + 2 * c);
        Reclassify(_buffer[i]);
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
        for (int r = 0; r < recordCount; r++)
        {
            int sequenceIndex = U16(d, records + 4 * r);
            int nested = U16(d, records + 4 * r + 2);
            if (sequenceIndex >= positions.Length) continue;
            Lookup? inner = table.GetLookup(nested);
            if (inner is null || !Budget()) continue;
            int before = _buffer.Count;
            int at = positions[sequenceIndex];
            if (at >= _buffer.Count) continue;
            ApplyAt(table, inner, at, substitute, depth + 1);
            int delta = _buffer.Count - before;
            if (delta == 0) continue;
            for (int k = sequenceIndex + 1; k < positions.Length; k++) positions[k] = Math.Max(at, positions[k] + delta);
            end = Math.Max(at + 1, end + delta);
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
            int j = NextIndex(positions[k - 1], lookup);
            if (j < 0 || !test(k)(_buffer[j].Glyph)) return null;
            positions[k] = j;
        }

        return positions;
    }

    private bool MatchBackward(int i, int count, Func<int, Func<int, bool>> test, Lookup lookup)
    {
        int j = i;
        for (int k = 0; k < count; k++)
        {
            j = PreviousIndex(j, lookup);
            if (j < 0 || !test(k)(_buffer[j].Glyph)) return false;
        }

        return true;
    }

    private bool MatchAhead(int last, int count, Func<int, Func<int, bool>> test, Lookup lookup)
    {
        int j = last;
        for (int k = 0; k < count; k++)
        {
            j = NextIndex(j, lookup);
            if (j < 0 || !test(k)(_buffer[j].Glyph)) return false;
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
                ApplyValue(d, value, valueFormat, g);
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
        int j = NextIndex(i, lookup);
        if (j < 0) return -1;
        int c = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), _buffer[i].Glyph);
        if (c < 0) return -1;
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

            if (record < 0) return -1;
            ApplyValue(d, record, format1, _buffer[i]);
            ApplyValue(d, record + size1, format2, _buffer[j]);
        }
        else if (format == 2)
        {
            int class1 = Kerning.ClassOf(d, sub + U16(d, sub + 8), _buffer[i].Glyph);
            int class2 = Kerning.ClassOf(d, sub + U16(d, sub + 10), right);
            int class1Count = U16(d, sub + 12), class2Count = U16(d, sub + 14);
            if (class1 >= class1Count || class2 >= class2Count) return -1;
            record = sub + 16 + (class1 * class2Count + class2) * (size1 + size2);
            ApplyValue(d, record, format1, _buffer[i]);
            ApplyValue(d, record + size1, format2, _buffer[j]);
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
        int j = NextIndex(i, lookup);
        if (j < 0) return -1;
        int c2 = Kerning.CoverageIndex(d, sub + U16(d, sub + 2), _buffer[j].Glyph);
        if (c2 < 0 || c2 >= U16(d, sub + 4)) return -1;
        int entryOffset = U16(d, sub + 6 + 4 * c2);
        if (entryOffset == 0) return -1;
        (int exitX, int exitY) = Anchor(d, sub + exitOffset);
        (int entryX, int entryY) = Anchor(d, sub + entryOffset);
        GlyphInfo a = _buffer[i], b = _buffer[j];
        if (!_rtl)
        {
            a.XAdvance = exitX + a.XOffset;
            int delta = entryX + b.XOffset;
            b.XAdvance -= delta;
            b.XOffset -= delta;
        }
        else
        {
            int delta = exitX + a.XOffset;
            a.XAdvance -= delta;
            a.XOffset -= delta;
            b.XAdvance = entryX + b.XOffset;
        }

        // The child hangs off its parent: with RightToLeft (flag bit 0) the earlier glyph is the
        // child. Its vertical offset becomes the anchors' difference; the parent's is added at the end.
        if ((lookup.Flag & 1) != 0)
        {
            a.CursiveParent = j;
            a.YOffset = entryY - exitY;
        }
        else
        {
            b.CursiveParent = i;
            b.YOffset = exitY - entryY;
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
            target = PreviousIndex(i, lookup);
            if (target < 0 || _buffer[target].Class != GlyphClass.Mark) return -1;
            GlyphInfo prev = _buffer[target];
            if (prev.LigatureId != mark.LigatureId || prev.Component != mark.Component) return -1;
        }
        else
        {
            target = i - 1;
            while (target >= 0 && _buffer[target].Class == GlyphClass.Mark) target--;
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
        (int bx, int by) = Anchor(d, anchorBase + anchorOffset);
        (int mx, int my) = Anchor(d, markAnchor);
        // Attaching replaces earlier placement; lookups after this one add to the offset again. As
        // in HarfBuzz, the vertical offset is fixed now, from the base's offset along its cursive
        // chain at this moment; only the horizontal position follows the base at the end.
        mark.AttachTo = target;
        mark.AttachDx = bx - mx;
        mark.XOffset = 0;
        mark.YOffset = by - my + CrossOffset(target);
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

    private static (int X, int Y) Anchor(ReadOnlySpan<byte> d, int at) => (I16(d, at + 2), I16(d, at + 4));

    private static int ValueSize(int format)
    {
        int n = 0;
        for (int v = format & 0xFF; v != 0; v &= v - 1) n++;
        return 2 * n;
    }

    /// <summary>Applies a value record's placement and advance; device tables are ignored (no hinting at PDF resolution).</summary>
    private static void ApplyValue(ReadOnlySpan<byte> d, int at, int format, GlyphInfo g)
    {
        int offset = 0;
        if ((format & 1) != 0)
        {
            g.XOffset += I16(d, at);
            offset += 2;
        }

        if ((format & 2) != 0)
        {
            g.YOffset += I16(d, at + offset);
            offset += 2;
        }

        if ((format & 4) != 0) g.XAdvance += I16(d, at + offset);
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
            if (g.Text.Length == 0 || !IsDefaultIgnorable(g.CodePoint) || g.Text.Length > (g.CodePoint > 0xFFFF ? 2 : 1)) continue;
            g.Glyph = space;
            g.XAdvance = 0;
            g.XOffset = 0;
            g.YOffset = 0;
        }
    }

    private static bool IsDefaultIgnorable(int cp) =>
        cp is 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFEFF or 0xFFA0
        || (cp >= 0x180B && cp <= 0x180F) || (cp >= 0x200B && cp <= 0x200F) || (cp >= 0x202A && cp <= 0x202E)
        || (cp >= 0x2060 && cp <= 0x206F) || (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xFFF0 && cp <= 0xFFF8)
        || (cp >= 0x1BCA0 && cp <= 0x1BCA3) || (cp >= 0x1D173 && cp <= 0x1D17A) || (cp >= 0xE0000 && cp <= 0xE0FFF);

    /// <summary>Marks take no space; in left to right text an unattached mark moves back over its base.</summary>
    private void ZeroMarks()
    {
        foreach (GlyphInfo g in _buffer)
        {
            if (g.Class != GlyphClass.Mark) continue;
            if (!_rtl && g.AttachTo < 0) g.XOffset -= g.XAdvance;
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
        var originY = new int[n];
        for (int k = 0; k < n; k++)
        {
            GlyphInfo g = _buffer[k];
            if (g.AttachTo >= 0 && g.AttachTo < k)
            {
                originX[k] = originX[g.AttachTo] + g.AttachDx + g.XOffset;
                originY[k] = g.YOffset;
                g.XOffset = originX[k] - pen[k];
            }
            else
            {
                originX[k] = pen[k] + g.XOffset;
                originY[k] = g.YOffset;
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
