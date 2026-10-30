using System.Globalization;
using PdfNetStack.Text;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Unicode normalization against the font, the way HarfBuzz's normalizer does it: characters the
/// font cannot draw decompose into ones it can, marks are put in canonical order, and a base and
/// mark compose again when the font has the composite (UAX #15, with the font's cmap deciding).
/// </summary>
internal sealed partial class Shaper
{
    /// <summary>Longer runs of marks are left in their order: sorting them is quadratic.</summary>
    private const int MaxCombiningMarks = 32;

    // How a space without a glyph of its own is drawn with the font's space: a fraction of the em,
    // the width of a digit or of a period, or half the space.
    private const byte SpaceEm = 1, SpaceEm16 = 16, SpaceFourEm18 = 17, SpacePlain = 18, SpaceFigure = 19, SpacePunctuation = 20, SpaceNarrow = 21;

    /// <summary>Arabic marks that modify the letter's shape and go next to it (UTR #53, modifier combining marks).</summary>
    private static readonly int[] ModifierMarks =
        [0x0654, 0x0655, 0x0658, 0x06DC, 0x06E3, 0x06E7, 0x06E8, 0x08CA, 0x08CB, 0x08CD, 0x08CE, 0x08CF, 0x08D3, 0x08F3];

    /// <summary>
    /// Decomposes, reorders and recomposes the buffer, and maps every character to its glyph. A
    /// character alone keeps its own glyph when the font has one; within a base and its marks,
    /// and always for the Indic, USE, Khmer and Myanmar shapers, characters decompose as far as
    /// the font covers. Hangul only decomposes what the font lacks, and does not recompose.
    /// </summary>
    private void Normalize(ShaperKind kind)
    {
        bool mightShortCircuit = kind is not (ShaperKind.Indic or ShaperKind.Use or ShaperKind.Khmer or ShaperKind.Myanmar);
        bool alwaysShortCircuit = kind == ShaperKind.Hangul;
        var output = new List<GlyphInfo>(_buffer.Count);
        bool allSimple = true;
        int count = _buffer.Count;
        for (int i = 0; i < count;)
        {
            // Characters up to the base of the next marks stand alone.
            int end = i + 1;
            while (end < count && !_buffer[end].IsMark) end++;
            if (end < count) end--;
            for (; i < end; i++) DecomposeCharacter(_buffer[i], mightShortCircuit, kind, output);
            if (i == count) break;

            allSimple = false;
            end = i + 1;
            while (end < count && _buffer[end].IsMark) end++;
            bool variationSelector = false;
            for (int k = i; k < end; k++) variationSelector |= IsVariationSelector(_buffer[k].CodePoint);

            if (variationSelector)
            {
                VariationSelectorCluster(i, end, output);
                i = end;
                continue;
            }

            for (; i < end; i++) DecomposeCharacter(_buffer[i], alwaysShortCircuit, kind, output);
        }

        _buffer = output;
        if (!allSimple) ReorderMarks(kind);

        // A combining grapheme joiner that kept no marks from reordering is skipped by lookups
        // like any default ignorable, as in HarfBuzz; one that did stays visible to them.
        for (int i = 1; i + 1 < _buffer.Count; i++)
        {
            if (_buffer[i].CodePoint == 0x034F && (_buffer[i + 1].CombiningClass == 0 || _buffer[i - 1].CombiningClass <= _buffer[i + 1].CombiningClass))
            {
                _buffer[i].Flags &= ~GlyphFlags.Hidden;
            }
        }

        if (!allSimple && !alwaysShortCircuit) Recompose(kind);
    }

    /// <summary>
    /// True when normalization lets the font draw the character at <paramref name="index"/>:
    /// through its decomposition, by composing a mark with the characters before it, or as a
    /// space or hyphen fallback. The character's grapheme up to it is normalized alone; past
    /// <see cref="MaxCombiningMarks"/> marks back, the look stops, so long runs stay linear.
    /// </summary>
    public static bool Covers(ShapingTables tables, string text, int index)
    {
        int start = index;
        while (start > 0 && index - start < 2 * MaxCombiningMarks && ShapingData.IsMark(CodePointAt(text, start)))
        {
            start -= start >= 2 && char.IsSurrogatePair(text[start - 2], text[start - 1]) ? 2 : 1;
        }

        int end = index + (char.IsSurrogatePair(text, index) ? 2 : 1);
        var shaper = new Shaper(tables);
        for (int i = start; i < end; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            var g = new GlyphInfo { Cluster = i, Text = text.Substring(i, char.IsSurrogatePair(text, i) ? 2 : 1), Mask = Global };
            SetCharacter(g, CodePointAt(text, i));
            shaper._buffer.Add(g);
        }

        shaper.Normalize(ShaperKind.Default);
        return shaper._buffer.All(g => g.Glyph != 0 || g.IsIgnorable);
    }

    private static int CodePointAt(string text, int i) =>
        char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : char.IsSurrogate(text[i]) ? 0xFFFD : text[i];

    private int GlyphOf(int codePoint) => _font.TryGetGlyph(codePoint, out int glyph) ? glyph : 0;

    private static GlyphInfo WithGlyph(GlyphInfo g, int glyph)
    {
        g.Glyph = glyph;
        return g;
    }

    /// <summary>
    /// A cluster with a variation selector is not normalized, as in HarfBuzz: a character and the
    /// selector after it become the font's glyph for the sequence (cmap format 14) when it has
    /// one; otherwise each keeps its own glyph, for GSUB to handle.
    /// </summary>
    private void VariationSelectorCluster(int start, int end, List<GlyphInfo> output)
    {
        int i = start;
        while (i < end)
        {
            GlyphInfo g = _buffer[i];
            if (i + 1 < end && IsVariationSelector(_buffer[i + 1].CodePoint))
            {
                GlyphInfo selector = _buffer[i + 1];
                if (_font.TryGetVariationGlyph(g.CodePoint, selector.CodePoint, out int variant))
                {
                    MergeClusters(_buffer, i, i + 2);
                    g.Text += selector.Text;
                    output.Add(WithGlyph(g, variant));
                }
                else
                {
                    output.Add(WithGlyph(g, GlyphOf(g.CodePoint)));
                    output.Add(WithGlyph(selector, GlyphOf(selector.CodePoint)));
                }

                i += 2;
                for (; i < end && IsVariationSelector(_buffer[i].CodePoint); i++) output.Add(WithGlyph(_buffer[i], GlyphOf(_buffer[i].CodePoint)));
            }
            else
            {
                output.Add(WithGlyph(g, GlyphOf(g.CodePoint)));
                i++;
            }
        }
    }

    private static bool IsVariationSelector(int cp) =>
        cp is (>= 0x180B and <= 0x180D) or 0x180F or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);

    private void DecomposeCharacter(GlyphInfo g, bool shortest, ShaperKind kind, List<GlyphInfo> output)
    {
        int cp = g.CodePoint;
        if (shortest && _font.TryGetGlyph(cp, out int glyph))
        {
            output.Add(WithGlyph(g, glyph));
            return;
        }

        var parts = new List<(int CodePoint, int Glyph)>(4);
        if (Decompose(cp, shortest, kind, parts) > 0)
        {
            for (int k = 0; k < parts.Count; k++)
            {
                GlyphInfo part = g.Copy();
                SetCharacter(part, parts[k].CodePoint);
                part.Glyph = parts[k].Glyph;
                if (k > 0) part.Text = "";
                output.Add(part);
            }

            return;
        }

        if (!shortest && _font.TryGetGlyph(cp, out glyph))
        {
            output.Add(WithGlyph(g, glyph));
            return;
        }

        // Spaces the font lacks are drawn with its space, at their own width.
        if (g.Category == UnicodeCategory.SpaceSeparator && SpaceKind(cp) is byte space and > 0 && _font.TryGetGlyph(' ', out glyph))
        {
            g.SpaceFallback = space;
            output.Add(WithGlyph(g, glyph));
            return;
        }

        // The non-breaking hyphen looks like the hyphen.
        if (cp == 0x2011 && _font.TryGetGlyph(0x2010, out glyph))
        {
            output.Add(WithGlyph(g, glyph));
            return;
        }

        output.Add(WithGlyph(g, 0));
    }

    /// <summary>
    /// Decomposes <paramref name="ab"/> into characters the font has, recursively through its
    /// first part. With <paramref name="shortest"/>, stops at the first level the font covers.
    /// Returns the number of characters added, 0 when the font cannot draw the decomposition.
    /// </summary>
    private int Decompose(int ab, bool shortest, ShaperKind kind, List<(int CodePoint, int Glyph)> parts)
    {
        if (!DecomposeFor(kind, ab, out int a, out int b)) return 0;
        int bGlyph = 0;
        if (b != 0 && !_font.TryGetGlyph(b, out bGlyph)) return 0;
        bool hasA = _font.TryGetGlyph(a, out int aGlyph);
        int added;
        if (shortest && hasA)
        {
            parts.Add((a, aGlyph));
            added = 1;
        }
        else if (Decompose(a, shortest, kind, parts) is int inner and > 0)
        {
            added = inner;
        }
        else if (hasA)
        {
            parts.Add((a, aGlyph));
            added = 1;
        }
        else
        {
            return 0;
        }

        if (b == 0) return added;
        parts.Add((b, bGlyph));
        return added + 1;
    }

    /// <summary>
    /// Canonical decomposition, except that the Indic shaper keeps Devanagari rra, Bengali rra and
    /// rha and Tamil au whole (fonts draw them as their own letters), and the Khmer shaper splits
    /// the vowel signs that start with the sign e on the left, which Unicode does not decompose.
    /// </summary>
    private static bool DecomposeFor(ShaperKind kind, int ab, out int a, out int b)
    {
        if (kind == ShaperKind.Indic && ab is 0x0931 or 0x09DC or 0x09DD or 0x0B94)
        {
            a = b = 0;
            return false;
        }

        if (kind == ShaperKind.Khmer && ab is (>= 0x17BE and <= 0x17C0) or 0x17C4 or 0x17C5)
        {
            a = 0x17C1;
            b = ab;
            return true;
        }

        return ShapingData.Decompose(ab, out a, out b);
    }

    /// <summary>
    /// Canonical composition. The Indic, USE and Khmer shapers do not compose onto a mark (split
    /// vowel signs stay split), and the Indic one composes Bengali yya from ya and nukta; the
    /// Hebrew shaper also composes the presentation forms of pointed letters when the font cannot
    /// position the points.
    /// </summary>
    private bool ComposeFor(ShaperKind kind, int a, int b, out int ab)
    {
        if (kind is ShaperKind.Indic or ShaperKind.Use or ShaperKind.Khmer)
        {
            ab = 0;
            if (ShapingData.IsMark(a)) return false;
            if (kind == ShaperKind.Indic && a == 0x09AF && b == 0x09BC)
            {
                ab = 0x09DF;
                return true;
            }
        }

        if (ShapingData.Compose(a, b, out ab)) return true;
        return kind == ShaperKind.Hebrew && !_gposFeatures.Any(f => f.Tag == "mark") && ShapingData.ComposeHebrew(a, b, out ab);
    }

    /// <summary>Sorts each run of marks by combining class, stably; the Arabic shaper then moves modifier marks first.</summary>
    private void ReorderMarks(ShaperKind kind)
    {
        int count = _buffer.Count;
        for (int i = 0; i < count; i++)
        {
            if (_buffer[i].CombiningClass == 0) continue;
            int end = i + 1;
            while (end < count && _buffer[end].CombiningClass != 0) end++;
            if (end - i <= MaxCombiningMarks)
            {
                for (int k = i + 1; k < end; k++)
                {
                    int j = k;
                    while (j > i && _buffer[j - 1].CombiningClass > _buffer[k].CombiningClass) j--;
                    if (j == k) continue;
                    MergeClusters(j, k + 1);
                    GlyphInfo moved = _buffer[k];
                    _buffer.RemoveAt(k);
                    _buffer.Insert(j, moved);
                }

                if (kind == ShaperKind.Arabic) ReorderModifierMarks(i, end);
            }

            i = end;
        }
    }

    /// <summary>
    /// UTR #53: modifier marks (hamza, small high letters) among the marks below, then among the
    /// marks above, move to the front, right after the letter. They get combining classes 25 and
    /// 26 so the sequence stays sorted.
    /// </summary>
    private void ReorderModifierMarks(int start, int end)
    {
        int i = start;
        for (int cc = 220; cc <= 230; cc += 10)
        {
            while (i < end && _buffer[i].CombiningClass < cc) i++;
            if (i == end) break;
            if (_buffer[i].CombiningClass > cc) continue;
            int j = i;
            while (j < end && _buffer[j].CombiningClass == cc && Array.IndexOf(ModifierMarks, _buffer[j].CodePoint) >= 0) j++;
            if (i == j) continue;

            MergeClusters(start, j);
            List<GlyphInfo> moved = _buffer.GetRange(i, j - i);
            _buffer.RemoveRange(i, j - i);
            _buffer.InsertRange(start, moved);
            for (int k = start; k < start + moved.Count; k++) _buffer[k].CombiningClass = (byte)(cc == 220 ? 25 : 26);
            start += moved.Count;
            i = j;
        }
    }

    /// <summary>
    /// Composes each mark with the last starter when nothing between them blocks it and the font
    /// has a glyph for the result. Only marks compose: a base never joins the base before it.
    /// </summary>
    private void Recompose(ShaperKind kind)
    {
        if (_buffer.Count == 0) return;
        var output = new List<GlyphInfo>(_buffer.Count) { _buffer[0] };
        int starter = 0;
        for (int i = 1; i < _buffer.Count; i++)
        {
            GlyphInfo g = _buffer[i];
            if (g.IsMark)
            {
                GlyphInfo previous = output[^1];
                if ((starter == output.Count - 1 || previous.CombiningClass < g.CombiningClass)
                    && ComposeFor(kind, output[starter].CodePoint, g.CodePoint, out int composed)
                    && _font.TryGetGlyph(composed, out int glyph))
                {
                    output.Add(g);
                    MergeClusters(output, starter, output.Count);
                    output.RemoveAt(output.Count - 1);
                    GlyphInfo s = output[starter];
                    s.Text += g.Text;
                    SetCharacter(s, composed);
                    s.Glyph = glyph;
                    continue;
                }

                // A tailored order can put a lower class after a higher one; composition stops there.
                if (starter < output.Count - 1 && previous.CombiningClass > g.CombiningClass) starter = output.Count;
            }

            output.Add(g);
            if (g.CombiningClass == 0) starter = output.Count - 1;
        }

        _buffer = output;
    }

    /// <summary>The fallback kind of a space character, or 0 for characters that are not spaces.</summary>
    private static byte SpaceKind(int cp) => cp switch
    {
        0x0020 or 0x00A0 or 0x1680 => SpacePlain,
        0x2000 or 0x2002 => 2,
        0x2001 or 0x2003 or 0x3000 => SpaceEm,
        0x2004 => 3,
        0x2005 => 4,
        0x2006 => 6,
        0x2007 => SpaceFigure,
        0x2008 => SpacePunctuation,
        0x2009 => 5,
        0x200A => SpaceEm16,
        0x202F => SpaceNarrow,
        0x205F => SpaceFourEm18,
        _ => 0,
    };

    /// <summary>Gives spaces drawn with the font's space their own widths.</summary>
    private void PositionFallbackSpaces()
    {
        int em = _font.UnitsPerEm;
        foreach (GlyphInfo g in _buffer)
        {
            switch (g.SpaceFallback)
            {
                case 0 or SpacePlain:
                    break;
                case <= SpaceEm16:
                    g.XAdvance = (em + g.SpaceFallback / 2) / g.SpaceFallback;
                    break;
                case SpaceFourEm18:
                    g.XAdvance = em * 4 / 18;
                    break;
                case SpaceFigure:
                    for (int digit = '0'; digit <= '9'; digit++)
                    {
                        if (!_font.TryGetGlyph(digit, out int glyph)) continue;
                        g.XAdvance = _font.Advance(glyph);
                        break;
                    }

                    break;
                case SpacePunctuation:
                    if (_font.TryGetGlyph('.', out int period) || _font.TryGetGlyph(',', out period)) g.XAdvance = _font.Advance(period);
                    break;
                case SpaceNarrow:
                    g.XAdvance /= 2;
                    break;
            }
        }
    }
}
