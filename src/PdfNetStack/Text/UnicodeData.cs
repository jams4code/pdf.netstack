namespace PdfNetStack.Text;

/// <summary>Line breaking classes of UAX #14, after its rule LB1 (AI, SG, XX, SA and CJ are resolved).</summary>
internal enum LineBreakClass : byte
{
    BK, CR, LF, CM, NL, WJ, ZW, GL, SP, ZWJ, B2, BA, BB, HY, CB, CL,
    CP, EX, IN, NS, OP, QU, IS, NU, PO, PR, SY, AK, AL, AP, AS, EB,
    EM, H2, H3, HL, ID, JL, JV, JT, RI, VF, VI,
}

/// <summary>Bidirectional character types of UAX #9.</summary>
internal enum BidiClass : byte
{
    L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON,
    LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI,
}

/// <summary>Arabic joining types (Unicode chapter 9.2): non-joining, right, left, dual, join causing, transparent.</summary>
internal enum JoiningType : byte
{
    U, R, L, D, C, T,
}

/// <summary>Character properties for text layout, from <c>UnicodeData.g.cs</c>.</summary>
internal static partial class UnicodeData
{
    /// <summary>East Asian width F, W or H.</summary>
    internal const byte EastAsian = 1;

    /// <summary>General category Pi, initial quotation.</summary>
    internal const byte InitialQuote = 2;

    /// <summary>General category Pf, final quotation.</summary>
    internal const byte FinalQuote = 4;

    /// <summary>An unassigned code point reserved for pictographs (Extended_Pictographic and Cn).</summary>
    internal const byte ReservedPictographic = 8;

    public static LineBreakClass LineBreak(int codePoint) => (LineBreakClass)Find(LineBreakStarts, LineBreakValues, codePoint);

    public static byte Flags(int codePoint) => Find(FlagsStarts, FlagsValues, codePoint);

    public static BidiClass Bidi(int codePoint) => (BidiClass)Find(BidiStarts, BidiValues, codePoint);

    public static JoiningType Joining(int codePoint) => (JoiningType)Find(JoiningStarts, JoiningValues, codePoint);

    /// <summary>Script index of the Common script (punctuation, digits, symbols shared by scripts).</summary>
    public const int CommonScript = 0;

    /// <summary>Script index of the Inherited script (combining marks that take their base's script).</summary>
    public const int InheritedScript = 1;

    /// <summary>Script index; <see cref="ScriptTags"/> gives its OpenType tag.</summary>
    public static int Script(int codePoint) => Find(ScriptStarts, ScriptValues, codePoint);

    /// <summary>The mirrored form of a character in right to left text, or the character itself.</summary>
    public static int Mirror(int codePoint)
    {
        int i = SearchPairs(Mirrors, 2, codePoint);
        return i < 0 ? codePoint : Mirrors[i + 1];
    }

    /// <summary>A paired bracket's partner and whether it opens; false when not a paired bracket.</summary>
    public static bool Bracket(int codePoint, out int pair, out bool opening)
    {
        int i = SearchPairs(Brackets, 3, codePoint);
        pair = i < 0 ? 0 : Brackets[i + 1];
        opening = i >= 0 && Brackets[i + 2] == 1;
        return i >= 0;
    }

    private static byte Find(int[] starts, byte[] values, int codePoint)
    {
        if ((uint)codePoint > 0x10FFFF) return values[0];
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (starts[mid] <= codePoint) lo = mid;
            else hi = mid - 1;
        }

        return values[lo];
    }

    private static int SearchPairs(int[] table, int stride, int codePoint)
    {
        int lo = 0, hi = table.Length / stride - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int v = table[mid * stride];
            if (v == codePoint) return mid * stride;
            if (v < codePoint) lo = mid + 1;
            else hi = mid - 1;
        }

        return -1;
    }
}
