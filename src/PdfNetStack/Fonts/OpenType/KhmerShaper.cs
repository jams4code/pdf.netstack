using PdfNetStack.Text;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// The Khmer shaper, from Microsoft's "Developing OpenType Fonts for Khmer Script" with the
/// syllable grammar HarfBuzz extracted from Uniscribe: a coeng and ro move before the base and
/// take the pre-base form, a left vowel sign moves to the start, and the below, above and
/// post-base forms apply to everything after the base, all before any other substitution.
/// </summary>
internal sealed partial class Shaper
{
    private const int KhmerConsonantSyllable = 0, KhmerBrokenCluster = 1, KhmerOther = 2;

    /// <summary>The syllable machines, built when Khmer text is first shaped rather than with every shaper.</summary>
    private static class KhmerGrammar
    {
        public static readonly SyllableGrammar.Machine[] Machines = BuildKhmerMachines();
    }

    private static readonly ShapingFeature[] KhmerBasicFeatures =
    [
        new("locl", PerSyllable: true), new("ccmp", PerSyllable: true),
        new("pref", true, true), new("blwf", true, true), new("abvf", true, true), new("pstf", true, true), new("cfar", true, true),
    ];

    // The Khmer specification counts clig among the required features; liga is left out, as Uniscribe does.
    private static readonly ShapingFeature[] KhmerOtherFeatures =
    [
        new("pres", true), new("abvs", true), new("blws", true), new("psts", true),
        new("rlig"), new("calt"), new("clig"), new("rclt"),
    ];

    private void SubstituteKhmer()
    {
        foreach (GlyphInfo g in _buffer) g.IndicCategory = (byte)(ShapingData.Indic(g.CodePoint) & 0xFF);
        ApplyStage(_gsub, _gsubFeatures, [new("rvrn")]);
        FindSyllables(KhmerGrammar.Machines, g => g.IndicCategory, KhmerOther);
        InsertDottedCircles(KhmerBrokenCluster, g => g.IndicCategory = (byte)IndicCategory.DottedCircle);
        foreach ((int start, int end) in Syllables())
        {
            if ((_buffer[start].Syllable & 0x0F) != KhmerOther) ReorderKhmer(start, end);
        }

        ApplyStage(_gsub, _gsubFeatures, KhmerBasicFeatures);
        ApplyStage(_gsub, _gsubFeatures, KhmerOtherFeatures);
    }

    /// <summary>
    /// Masks and reordering of one syllable. Of the first two coengs, one followed by ro moves with
    /// the ro to the start for the pre-base form, and what follows takes cfar.
    /// </summary>
    private void ReorderKhmer(int start, int end)
    {
        List<GlyphInfo> b = _buffer;
        for (int i = start + 1; i < end; i++) b[i].Mask |= Blwf | Abvf | Pstf;
        int coengs = 0;
        for (int i = start + 1; i < end; i++)
        {
            var category = (IndicCategory)b[i].IndicCategory;
            if (category == IndicCategory.H && coengs <= 2 && i + 1 < end)
            {
                coengs++;
                if ((IndicCategory)b[i + 1].IndicCategory != IndicCategory.Ra) continue;
                b[i].Mask |= Pref;
                b[i + 1].Mask |= Pref;
                MergeClusters(start, i + 2);
                GlyphInfo coeng = b[i], ro = b[i + 1];
                b.RemoveRange(i, 2);
                b.InsertRange(start, [coeng, ro]);
                for (int j = i + 2; j < end; j++) b[j].Mask |= Cfar;
                coengs = 2;
            }
            else if (category == IndicCategory.VPre)
            {
                MergeClusters(start, i + 1);
                GlyphInfo vowel = b[i];
                b.RemoveAt(i);
                b.Insert(start, vowel);
            }
        }
    }

    /// <summary>
    /// Khmer syllables (HarfBuzz's grammar, from what Uniscribe accepts): a consonant, independent
    /// vowel or placeholder with an optional robat, subscripts after coengs, vowel signs in visual
    /// order with signs between them, and a final coeng and consonant. A robat or a sign without a
    /// consonant starts a broken cluster.
    /// </summary>
    private static SyllableGrammar.Machine[] BuildKhmerMachines()
    {
        static SyllableGrammar.Rx K(params IndicCategory[] c) => SyllableGrammar.Sym([.. c.Select(k => (int)k)]);
        SyllableGrammar.Rx joiner = K(IndicCategory.Zwj, IndicCategory.Zwnj);
        SyllableGrammar.Rx c = K(IndicCategory.C, IndicCategory.Ra, IndicCategory.V);
        SyllableGrammar.Rx cn = SyllableGrammar.Seq(c, SyllableGrammar.Opt(SyllableGrammar.Seq(SyllableGrammar.Opt(joiner), K(IndicCategory.Robatic))));
        SyllableGrammar.Rx xgroup = SyllableGrammar.Star(SyllableGrammar.Seq(SyllableGrammar.Star(joiner), K(IndicCategory.Xgroup)));
        SyllableGrammar.Rx ygroup = SyllableGrammar.Star(K(IndicCategory.Ygroup));
        SyllableGrammar.Rx coeng = K(IndicCategory.H);
        SyllableGrammar.Rx matraGroup = SyllableGrammar.Seq(
            SyllableGrammar.Opt(K(IndicCategory.VPre)), xgroup, SyllableGrammar.Opt(K(IndicCategory.VBlw)), xgroup,
            SyllableGrammar.Opt(SyllableGrammar.Seq(SyllableGrammar.Opt(joiner), K(IndicCategory.VAbv))), xgroup, SyllableGrammar.Opt(K(IndicCategory.VPst)));
        SyllableGrammar.Rx syllableTail = SyllableGrammar.Seq(xgroup, matraGroup, xgroup, SyllableGrammar.Opt(SyllableGrammar.Seq(coeng, c)), ygroup);
        SyllableGrammar.Rx broken = SyllableGrammar.Seq(
            SyllableGrammar.Opt(K(IndicCategory.Robatic)), SyllableGrammar.Star(SyllableGrammar.Seq(coeng, cn)), SyllableGrammar.Or(coeng, syllableTail));
        SyllableGrammar.Rx consonant = SyllableGrammar.Seq(SyllableGrammar.Or(cn, K(IndicCategory.Placeholder), K(IndicCategory.DottedCircle)), broken);
        return [SyllableGrammar.Machine.From(consonant), SyllableGrammar.Machine.From(broken)];
    }

    /// <summary>
    /// Splits the buffer into syllables of the given grammars, numbering them from 1 as
    /// <c>serial &lt;&lt; 4 | type</c>; what no grammar matches is one glyph of type <paramref name="other"/>.
    /// </summary>
    private void FindSyllables(SyllableGrammar.Machine[] machines, Func<GlyphInfo, int> category, int other)
    {
        int[] categories = [.. _buffer.Select(category)];
        int serial = 1;
        foreach ((int start, int length, int type) in SyllableGrammar.Split(machines, categories, other))
        {
            for (int i = start; i < start + length; i++) _buffer[i].Syllable = serial << 4 | type;
            serial++;
        }
    }

    /// <summary>
    /// A broken syllable (one that starts with a sign) gets a dotted circle to stand on, after any
    /// glyphs <paramref name="leading"/> keeps first, when the font has one.
    /// </summary>
    private void InsertDottedCircles(int brokenType, Action<GlyphInfo> categorize, Func<GlyphInfo, bool>? leading = null)
    {
        if (!_buffer.Any(g => (g.Syllable & 0x0F) == brokenType) || !_font.TryGetGlyph(0x25CC, out int circle)) return;
        var output = new List<GlyphInfo>(_buffer.Count + 4);
        int last = 0;
        for (int i = 0; i < _buffer.Count;)
        {
            GlyphInfo g = _buffer[i];
            if (g.Syllable != last && (g.Syllable & 0x0F) == brokenType)
            {
                last = g.Syllable;
                while (leading is not null && i < _buffer.Count && _buffer[i].Syllable == last && leading(_buffer[i])) output.Add(_buffer[i++]);
                var dotted = new GlyphInfo { Glyph = circle, Cluster = g.Cluster, Mask = g.Mask, Syllable = g.Syllable };
                SetCharacter(dotted, 0x25CC);
                dotted.Class = _gdef?.HasClasses == true ? _gdef.ClassOf(circle) : GlyphClass.Base;
                categorize(dotted);
                output.Add(dotted);
                continue;
            }

            output.Add(g);
            i++;
        }

        _buffer = output;
    }
}
