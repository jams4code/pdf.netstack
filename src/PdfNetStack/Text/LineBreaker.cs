using static PdfNetStack.Text.LineBreakClass;

namespace PdfNetStack.Text;

/// <summary>A place where a line may end: before the character at <paramref name="Position"/> (a UTF-16 index).</summary>
/// <param name="Position">Index of the first character of the next line; the text length for the end.</param>
/// <param name="Mandatory">True for a hard break: after a line feed, paragraph separator and the like.</param>
internal readonly record struct LineBreak(int Position, bool Mandatory);

/// <summary>
/// The Unicode line breaking algorithm, UAX #14 (Unicode 16), default rules without tailoring.
/// Rules are checked in the order the standard lists them; the first that applies decides. A
/// base character and the combining marks after it form one unit (LB9), so marks never start a
/// line. Conformance is tested against the Unicode LineBreakTest file.
/// </summary>
internal static class LineBreaker
{
    private const int DottedCircle = 0x25CC;

    /// <summary>Every break opportunity of <paramref name="text"/>, ending with the mandatory break at its end.</summary>
    public static List<LineBreak> Breaks(string text)
    {
        var breaks = new List<LineBreak>();
        if (text.Length == 0) return breaks;
        Unit[] units = Units(text);
        for (int k = 1; k < units.Length; k++)
        {
            Decision d = Decide(units, k);
            if (d != Decision.Keep) breaks.Add(new LineBreak(units[k].Start, d == Decision.Mandatory));
        }

        breaks.Add(new LineBreak(text.Length, true)); // LB3
        return breaks;
    }

    private enum Decision
    {
        Keep,
        Allow,
        Mandatory,
    }

    /// <summary>A base character with its attached combining marks (LB9, LB10).</summary>
    private struct Unit
    {
        public int Start;
        public int CodePoint;
        public LineBreakClass Class;
        public byte Flags;
        public bool EndsWithZwj;

        public readonly bool EastAsian => (Flags & UnicodeData.EastAsian) != 0;
    }

    private static Unit[] Units(string text)
    {
        var units = new List<Unit>(text.Length);
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int cp = char.IsSurrogate(text[i]) && !char.IsSurrogatePair(text, i) ? 0xFFFD : char.ConvertToUtf32(text, i);
            LineBreakClass c = UnicodeData.LineBreak(cp);
            if (c is CM or ZWJ && units.Count > 0 && units[^1].Class is not (BK or CR or LF or NL or SP or ZW))
            {
                Unit last = units[^1];
                last.EndsWithZwj = c == ZWJ;
                units[^1] = last;
                continue;
            }

            units.Add(new Unit
            {
                Start = i,
                CodePoint = cp,
                Class = c is CM or ZWJ ? AL : c,
                Flags = UnicodeData.Flags(cp),
                EndsWithZwj = c == ZWJ,
            });
        }

        return [.. units];
    }

    /// <summary>The rule for the boundary before unit <paramref name="k"/>.</summary>
    private static Decision Decide(Unit[] u, int k)
    {
        LineBreakClass before = u[k - 1].Class, after = u[k].Class;
        LineBreakClass? next = k + 1 < u.Length ? u[k + 1].Class : null;

        // LB4, LB5: hard breaks.
        if (before == BK) return Decision.Mandatory;
        if (before == CR && after == LF) return Decision.Keep;
        if (before is CR or LF or NL) return Decision.Mandatory;

        // LB6, LB7
        if (after is BK or CR or LF or NL or SP or ZW) return Decision.Keep;

        // LB8: ZW SP* ÷
        if (BeforeSpaces(u, k) is int z && u[z].Class == ZW) return Decision.Allow;

        // LB8a: ZWJ ×
        if (u[k - 1].EndsWithZwj) return Decision.Keep;

        // LB11, LB12, LB12a
        if (after == WJ || before == WJ) return Decision.Keep;
        if (before == GL) return Decision.Keep;
        if (after == GL && before is not (SP or BA or HY)) return Decision.Keep;

        // LB13
        if (after is CL or CP or EX or SY) return Decision.Keep;

        // LB14: OP SP* ×
        int? p = BeforeSpaces(u, k);
        if (p is int op && u[op].Class == OP) return Decision.Keep;

        // LB15a: an initial quotation mark after an opening context, and the spaces after it, keep with what follows.
        if (p is int pi && u[pi].Class == QU && (u[pi].Flags & UnicodeData.InitialQuote) != 0
            && (pi == 0 || u[pi - 1].Class is BK or CR or LF or NL or OP or QU or GL or SP or ZW))
        {
            return Decision.Keep;
        }

        // LB15b: a final quotation mark before a closing context keeps with what precedes it.
        if (after == QU && (u[k].Flags & UnicodeData.FinalQuote) != 0
            && (next is null || next is SP or GL or WJ or CL or QU or CP or EX or IS or SY or BK or CR or LF or NL or ZW))
        {
            return Decision.Keep;
        }

        // LB15c, LB15d
        if (before == SP && after == IS && next == NU) return Decision.Allow;
        if (after == IS) return Decision.Keep;

        // LB16, LB17
        if (after == NS && p is int cl && u[cl].Class is CL or CP) return Decision.Keep;
        if (after == B2 && p is int b2 && u[b2].Class == B2) return Decision.Keep;

        // LB18
        if (before == SP) return Decision.Allow;

        // LB19
        if (after == QU && (u[k].Flags & UnicodeData.InitialQuote) == 0) return Decision.Keep;
        if (before == QU && (u[k - 1].Flags & UnicodeData.FinalQuote) == 0) return Decision.Keep;

        // LB19a: quotation marks among non East Asian text.
        if (after == QU && (!u[k - 1].EastAsian || k + 1 >= u.Length || !u[k + 1].EastAsian)) return Decision.Keep;
        if (before == QU && (!u[k].EastAsian || k < 2 || !u[k - 2].EastAsian)) return Decision.Keep;

        // LB20
        if (after == CB || before == CB) return Decision.Allow;

        // LB20a: a word initial hyphen.
        if ((before == HY || u[k - 1].CodePoint == 0x2010) && after == AL
            && (k < 2 || u[k - 2].Class is BK or CR or LF or NL or SP or ZW or CB or GL))
        {
            return Decision.Keep;
        }

        // LB21, LB21a, LB21b
        if (after is BA or HY or NS || before == BB) return Decision.Keep;
        if (k >= 2 && u[k - 2].Class == HL && (before == HY || (before == BA && !u[k - 1].EastAsian)) && after != HL) return Decision.Keep;
        if (before == SY && after == HL) return Decision.Keep;

        // LB22
        if (after == IN) return Decision.Keep;

        // LB23, LB23a, LB24
        if ((before is AL or HL && after == NU) || (before == NU && after is AL or HL)) return Decision.Keep;
        if ((before == PR && after is ID or EB or EM) || (before is ID or EB or EM && after == PO)) return Decision.Keep;
        if ((before is PR or PO && after is AL or HL) || (before is AL or HL && after is PR or PO)) return Decision.Keep;

        // LB25: numbers.
        if (after is PO or PR)
        {
            int j = k - 1;
            if (u[j].Class is CL or CP) j--;
            while (j >= 0 && u[j].Class is SY or IS) j--;
            if (j >= 0 && u[j].Class == NU) return Decision.Keep;
        }

        if (before is PO or PR)
        {
            if (after == NU) return Decision.Keep;
            if (after == OP && (next == NU || (next == IS && k + 2 < u.Length && u[k + 2].Class == NU))) return Decision.Keep;
        }

        if (after == NU)
        {
            if (before is HY or IS) return Decision.Keep;
            int j = k - 1;
            while (j >= 0 && u[j].Class is SY or IS) j--;
            if (j >= 0 && u[j].Class == NU) return Decision.Keep;
        }

        // LB26, LB27: Korean syllable blocks.
        if (before == JL && after is JL or JV or H2 or H3) return Decision.Keep;
        if (before is JV or H2 && after is JV or JT) return Decision.Keep;
        if (before is JT or H3 && after == JT) return Decision.Keep;
        if (before is JL or JV or JT or H2 or H3 && after == PO) return Decision.Keep;
        if (before == PR && after is JL or JV or JT or H2 or H3) return Decision.Keep;

        // LB28
        if (before is AL or HL && after is AL or HL) return Decision.Keep;

        // LB28a: Brahmic orthographic syllables.
        if (before == AP && Aksara(u[k])) return Decision.Keep;
        if (Aksara(u[k - 1]) && after is VF or VI) return Decision.Keep;
        if (before == VI && k >= 2 && Aksara(u[k - 2]) && (after == AK || u[k].CodePoint == DottedCircle)) return Decision.Keep;
        if (Aksara(u[k - 1]) && Aksara(u[k]) && next == VF) return Decision.Keep;

        // LB29, LB30
        if (before == IS && after is AL or HL) return Decision.Keep;
        if (before is AL or HL or NU && after == OP && !u[k].EastAsian) return Decision.Keep;
        if (before == CP && !u[k - 1].EastAsian && after is AL or HL or NU) return Decision.Keep;

        // LB30a: regional indicators pair up into flags.
        if (before == RI && after == RI)
        {
            int run = 0;
            for (int j = k - 1; j >= 0 && u[j].Class == RI; j--) run++;
            if (run % 2 == 1) return Decision.Keep;
        }

        // LB30b
        if (after == EM && (before == EB || (u[k - 1].Flags & UnicodeData.ReservedPictographic) != 0)) return Decision.Keep;

        return Decision.Allow; // LB31
    }

    /// <summary>Index of the unit before the spaces that end just before unit <paramref name="k"/>, or null at the start.</summary>
    private static int? BeforeSpaces(Unit[] u, int k)
    {
        int j = k - 1;
        while (j >= 0 && u[j].Class == SP) j--;
        return j >= 0 ? j : null;
    }

    private static bool Aksara(Unit unit) => unit.Class is AK or AS || unit.CodePoint == DottedCircle;
}
