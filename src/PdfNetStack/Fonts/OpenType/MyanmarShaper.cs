using PdfNetStack.Text;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// The Myanmar shaper, from Microsoft's "Developing OpenType Fonts for Myanmar Script" as
/// HarfBuzz implements it (the mym2 specification): kinzi moves after the base, the medial ra and
/// left vowel signs move before it, the rest sorts by position, and the basic forms apply one
/// feature at a time.
/// </summary>
internal sealed partial class Shaper
{
    // Syllable types: the grammars' indices. Type 1 is a joiner alone, which is not Myanmar text.
    private const int MyanmarConsonantSyllable = 0, MyanmarBrokenCluster = 2, MyanmarOther = 3;

    private const long MyanmarConsonants = 1L << (int)IndicCategory.C | 1L << (int)IndicCategory.CS | 1L << (int)IndicCategory.Ra
        | 1L << (int)IndicCategory.V | 1L << (int)IndicCategory.Placeholder | 1L << (int)IndicCategory.DottedCircle;

    /// <summary>The syllable machines, built when Myanmar text is first shaped rather than with every shaper.</summary>
    private static class MyanmarGrammar
    {
        public static readonly SyllableGrammar.Machine[] Machines = BuildMyanmarMachines();
    }

    private static readonly ShapingFeature[] MyanmarOtherFeatures =
    [
        new("pres", ManualZwj: true), new("abvs", ManualZwj: true), new("blws", ManualZwj: true), new("psts", ManualZwj: true),
        new("rlig"), new("calt"), new("clig"), new("liga"), new("rclt"),
    ];

    private void SubstituteMyanmar()
    {
        foreach (GlyphInfo g in _buffer) g.IndicCategory = (byte)(ShapingData.Indic(g.CodePoint) & 0xFF);
        ApplyStage(_gsub, _gsubFeatures, [new("rvrn")]);
        FindSyllables(MyanmarGrammar.Machines, g => g.IndicCategory, MyanmarOther);
        ApplyStage(_gsub, _gsubFeatures, [new("locl", PerSyllable: true), new("ccmp", PerSyllable: true)]);
        InsertDottedCircles(MyanmarBrokenCluster, g => g.IndicCategory = (byte)IndicCategory.DottedCircle);
        foreach ((int start, int end) in Syllables())
        {
            int type = _buffer[start].Syllable & 0x0F;
            if (type is MyanmarConsonantSyllable or MyanmarBrokenCluster) ReorderMyanmar(start, end);
        }

        foreach (string tag in (string[])["rphf", "pref", "blwf", "pstf"])
        {
            ApplyStage(_gsub, _gsubFeatures, [new(tag, PerSyllable: true, ManualZwj: true, Global: true)]);
        }

        ApplyStage(_gsub, _gsubFeatures, MyanmarOtherFeatures);
    }

    private void ReorderMyanmar(int start, int end)
    {
        List<GlyphInfo> b = _buffer;
        static IndicCategory Category(GlyphInfo g) => (IndicCategory)g.IndicCategory;

        // Kinzi (nga, asat, virama) at the start goes after the base, the first consonant after it.
        int baseIndex = end;
        bool kinzi = start + 3 <= end && Category(b[start]) == IndicCategory.Ra && Category(b[start + 1]) == IndicCategory.As
            && Category(b[start + 2]) == IndicCategory.H;
        int limit = kinzi ? start + 3 : start;
        if (!kinzi) baseIndex = limit;
        for (int i = limit; i < end; i++)
        {
            if ((b[i].Flags & GlyphFlags.Ligated) == 0 && ((1L << b[i].IndicCategory) & MyanmarConsonants) != 0)
            {
                baseIndex = i;
                break;
            }
        }

        int k = start;
        for (; k < limit; k++) b[k].IndicPosition = (byte)IndicPosition.AfterMain;
        for (; k < baseIndex; k++) b[k].IndicPosition = (byte)IndicPosition.PreC;
        if (k < end) b[k++].IndicPosition = (byte)IndicPosition.BaseC;
        IndicPosition position = IndicPosition.AfterMain;
        for (; k < end; k++)
        {
            IndicCategory category = Category(b[k]);
            IndicPosition p;
            if (category == IndicCategory.MR) p = IndicPosition.PreC;
            else if (category == IndicCategory.VPre) p = IndicPosition.PreM;
            else if (category == IndicCategory.VS) p = (IndicPosition)b[k - 1].IndicPosition;
            else if (position == IndicPosition.AfterMain && category == IndicCategory.VBlw) p = position = IndicPosition.BelowC;
            else if (position == IndicPosition.BelowC && category == IndicCategory.A) p = IndicPosition.BeforeSub;
            else if (position == IndicPosition.BelowC && category == IndicCategory.VBlw) p = position;
            else if (position == IndicPosition.BelowC) p = position = IndicPosition.AfterSub;
            else p = position;
            b[k].IndicPosition = (byte)p;
        }

        // A stable sort by position; a glyph that moves merges clusters with what it passes.
        for (int i = start + 1; i < end; i++)
        {
            int j = i;
            while (j > start && b[j - 1].IndicPosition > b[i].IndicPosition) j--;
            if (j == i) continue;
            MergeClusters(j, i + 1);
            GlyphInfo moved = b[i];
            b.RemoveAt(i);
            b.Insert(j, moved);
        }

        // Left vowel signs go back to typing order, each with the variation selector after it.
        int first = end, last = end;
        for (int i = start; i < end; i++)
        {
            if (b[i].IndicPosition != (byte)IndicPosition.PreM) continue;
            if (first == end) first = i;
            last = i;
        }

        if (first < last)
        {
            b.Reverse(first, last - first + 1);
            int from = first;
            for (int j = first; j <= last; j++)
            {
                if (Category(b[j]) != IndicCategory.VPre) continue;
                b.Reverse(from, j - from + 1);
                from = j + 1;
            }
        }
    }

    /// <summary>
    /// Myanmar syllables (HarfBuzz's grammar of the specification): an optional kinzi, a consonant,
    /// independent vowel or generic base, stacked consonants after viramas, medials ya, ra, wa, ha
    /// and la, vowel signs, then pwo tones and visargas in any order.
    /// </summary>
    private static SyllableGrammar.Machine[] BuildMyanmarMachines()
    {
        static SyllableGrammar.Rx M(params IndicCategory[] c) => SyllableGrammar.Sym([.. c.Select(k => (int)k)]);
        static SyllableGrammar.Rx Opt(SyllableGrammar.Rx r) => SyllableGrammar.Opt(r);
        static SyllableGrammar.Rx Star(SyllableGrammar.Rx r) => SyllableGrammar.Star(r);
        static SyllableGrammar.Rx Seq(params SyllableGrammar.Rx[] r) => SyllableGrammar.Seq(r);
        static SyllableGrammar.Rx Or(params SyllableGrammar.Rx[] r) => SyllableGrammar.Or(r);
        SyllableGrammar.Rx asat = M(IndicCategory.As), dotBelow = M(IndicCategory.N), a = M(IndicCategory.A);
        SyllableGrammar.Rx vs = M(IndicCategory.VS), h = M(IndicCategory.H);
        SyllableGrammar.Rx joiner = M(IndicCategory.Zwj, IndicCategory.Zwnj);
        SyllableGrammar.Rx kinzi = Seq(M(IndicCategory.Ra), asat, h);
        SyllableGrammar.Rx c = M(IndicCategory.C, IndicCategory.Ra);
        SyllableGrammar.Rx dotBelowGroup = Opt(Seq(dotBelow, Opt(asat)));
        SyllableGrammar.Rx medialGroup = Seq(
            Opt(M(IndicCategory.MY)), Opt(asat), Opt(M(IndicCategory.MR)),
            Opt(Seq(Or(Seq(M(IndicCategory.MW), Opt(M(IndicCategory.MH)), Opt(M(IndicCategory.ML))), Seq(M(IndicCategory.MH), Opt(M(IndicCategory.ML))), M(IndicCategory.ML)), Opt(asat))));
        SyllableGrammar.Rx mainVowelGroup = Seq(Star(Seq(M(IndicCategory.VPre), Opt(vs))), Star(M(IndicCategory.VAbv)), Star(M(IndicCategory.VBlw)), Star(a), dotBelowGroup);
        SyllableGrammar.Rx postVowelGroup = Seq(M(IndicCategory.VPst), Opt(M(IndicCategory.MH)), Opt(M(IndicCategory.ML)), Star(asat), Star(M(IndicCategory.VAbv)), Star(a), dotBelowGroup);
        SyllableGrammar.Rx pwoToneGroup = Seq(M(IndicCategory.PT), Star(a), Opt(dotBelow), Opt(asat));
        SyllableGrammar.Rx complexTail = Seq(Star(asat), medialGroup, mainVowelGroup, Star(postVowelGroup), Star(Or(pwoToneGroup, M(IndicCategory.SM))), Opt(joiner));
        SyllableGrammar.Rx syllableTail = Seq(Star(Seq(h, Or(c, M(IndicCategory.V)), Opt(vs))), Or(h, complexTail));
        SyllableGrammar.Rx consonant = Seq(Opt(Or(kinzi, M(IndicCategory.CS))), M(IndicCategory.C, IndicCategory.Ra, IndicCategory.V, IndicCategory.Placeholder, IndicCategory.DottedCircle), Opt(vs), syllableTail);
        SyllableGrammar.Rx broken = Seq(Opt(kinzi), Opt(vs), syllableTail);
        return [SyllableGrammar.Machine.From(consonant), SyllableGrammar.Machine.From(joiner), SyllableGrammar.Machine.From(broken)];
    }
}
