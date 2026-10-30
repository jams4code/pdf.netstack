using System.Globalization;

namespace PdfNetStack.Text;

/// <summary>Character properties for OpenType shaping and normalization, from <c>ShapingData.g.cs</c>.</summary>
internal static partial class ShapingData
{
    private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
    private const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    /// <summary>
    /// Combining classes as the shaper sorts marks (UAX #15 canonical ordering, tailored): Hebrew
    /// points follow the order of the SBL Hebrew manual, Arabic shadda goes before the other
    /// vowel marks, and the Telugu length marks, Thai sara u and the Tibetan vowel signs sort where
    /// their scripts expect them.
    /// </summary>
    private static readonly byte[] Tailored = BuildTailoring();

    /// <summary>The general category of a code point; unassigned code points are <see cref="UnicodeCategory.OtherNotAssigned"/>.</summary>
    public static UnicodeCategory Category(int codePoint) => (UnicodeCategory)Find(CategoryStarts, CategoryValues, codePoint);

    /// <summary>True for nonspacing, spacing and enclosing combining marks.</summary>
    public static bool IsMark(int codePoint) =>
        Category(codePoint) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>The canonical combining class of a mark, tailored for shaping; 0 for anything else.</summary>
    public static int CombiningClass(int codePoint)
    {
        if (!IsMark(codePoint)) return 0;

        // Tai Tham sakot and Tibetan padma go after the other marks; Tibetan tsa-phru before u.
        if (codePoint is 0x1A60 or 0x0FC6) return 254;
        if (codePoint == 0x0F39) return 127;
        return Tailored[Find(CombiningClassStarts, CombiningClassValues, codePoint)];
    }

    /// <summary>The Indic shaping category (low byte) and position (high byte) of a code point.</summary>
    public static int Indic(int codePoint) => Find(IndicStarts, IndicValues, codePoint);

    /// <summary>The Universal Shaping Engine category of a code point.</summary>
    public static int Use(int codePoint) => Find(UseStarts, UseValues, codePoint);

    /// <summary>
    /// The length of the invalid vowel sequence (one that looks like another vowel, as a letter a
    /// with the sign aa looks like the letter aa) that starts at <paramref name="start"/>, or 0.
    /// </summary>
    public static int InvalidVowelSequence(IReadOnlyList<int> codePoints, int start)
    {
        if (!VowelConstraintIndex.Starts.TryGetValue(codePoints[start], out List<int>? offsets)) return 0;
        foreach (int at in offsets)
        {
            int length = VowelConstraints[at];
            if (start + length > codePoints.Count) continue;
            int k = 1;
            while (k < length && VowelConstraints[at + 1 + k] == codePoints[start + k]) k++;
            if (k == length) return length;
        }

        return 0;
    }

    /// <summary>
    /// Where each invalid vowel sequence starts in <see cref="VowelConstraints"/>, by its first
    /// code point; built on first use, when the generated tables are ready.
    /// </summary>
    private static class VowelConstraintIndex
    {
        public static readonly Dictionary<int, List<int>> Starts = Build();

        private static Dictionary<int, List<int>> Build()
        {
            var index = new Dictionary<int, List<int>>();
            for (int at = 0; at < VowelConstraints.Length; at += 1 + VowelConstraints[at])
            {
                int first = VowelConstraints[at + 1];
                if (!index.TryGetValue(first, out List<int>? list)) index[first] = list = [];
                list.Add(at);
            }

            return index;
        }
    }

    /// <summary>
    /// One step of canonical decomposition: <paramref name="a"/> and <paramref name="b"/>, which is
    /// 0 for a singleton. Hangul syllables decompose algorithmically, LV + T or L + V.
    /// </summary>
    public static bool Decompose(int ab, out int a, out int b)
    {
        int s = ab - SBase;
        if ((uint)s < SCount)
        {
            if (s % TCount != 0)
            {
                a = ab - s % TCount;
                b = TBase + s % TCount;
            }
            else
            {
                a = LBase + s / NCount;
                b = VBase + s % NCount / TCount;
            }

            return true;
        }

        int i = Search(Decompositions, ab, 0);
        a = i < 0 ? 0 : Decompositions[i + 1];
        b = i < 0 ? 0 : Decompositions[i + 2];
        return i >= 0;
    }

    /// <summary>The canonical composition of <paramref name="a"/> and <paramref name="b"/> that normalization form C allows.</summary>
    public static bool Compose(int a, int b, out int ab)
    {
        if ((uint)(a - LBase) < LCount && (uint)(b - VBase) < VCount)
        {
            ab = SBase + ((a - LBase) * VCount + (b - VBase)) * TCount;
            return true;
        }

        if ((uint)(a - SBase) < SCount && (a - SBase) % TCount == 0 && b > TBase && b < TBase + TCount)
        {
            ab = a + (b - TBase);
            return true;
        }

        int i = Search(Compositions, a, b);
        ab = i < 0 ? 0 : Compositions[i + 2];
        return i >= 0;
    }

    /// <summary>
    /// A Hebrew presentation form for a letter and point, for fonts that draw pointed Hebrew with
    /// precomposed glyphs. These forms are excluded from normalization form C. Shin with a shin or
    /// sin dot also takes a dagesh.
    /// </summary>
    public static bool ComposeHebrew(int a, int b, out int ab)
    {
        ab = (a, b) switch
        {
            (0xFB2A, 0x05BC) => 0xFB2C,
            (0xFB2B, 0x05BC) => 0xFB2D,
            _ => 0,
        };
        if (ab != 0) return true;
        int i = Search(HebrewCompositions, a, b);
        ab = i < 0 ? 0 : HebrewCompositions[i + 2];
        return i >= 0;
    }

    private static byte[] BuildTailoring()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)i;

        // Hebrew fixed position classes 10 to 26: sheva, hataf segol, hataf patah, hataf qamats,
        // hiriq, tsere, segol, patah, qamats, holam, qubuts, dagesh, meteg, rafe, shin dot, sin
        // dot, varika.
        byte[] hebrew = [22, 15, 16, 17, 23, 18, 19, 20, 21, 14, 24, 12, 25, 13, 10, 11, 26];
        for (int i = 0; i < hebrew.Length; i++) t[10 + i] = hebrew[i];

        // Arabic 27 to 35: shadda (33) first.
        byte[] arabic = [28, 29, 30, 31, 32, 33, 27, 34, 35];
        for (int i = 0; i < arabic.Length; i++) t[27 + i] = arabic[i];

        // Telugu length marks, which would otherwise sort with the virama (9).
        t[84] = 4;
        t[91] = 5;

        // Thai sara u and uu; Tibetan sign i and u.
        t[103] = 3;
        t[130] = 132;
        t[132] = 131;
        return t;
    }

    private static int Find(int[] starts, byte[] values, int codePoint) => values[Range(starts, codePoint)];

    private static int Find(int[] starts, ushort[] values, int codePoint) => values[Range(starts, codePoint)];

    /// <summary>The range of a sorted table of range starts that holds the code point.</summary>
    private static int Range(int[] starts, int codePoint)
    {
        if ((uint)codePoint > 0x10FFFF) return 0;
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (starts[mid] <= codePoint) lo = mid;
            else hi = mid - 1;
        }

        return lo;
    }

    /// <summary>Binary search of a table of triples sorted by their first two values.</summary>
    private static int Search(int[] table, int first, int second)
    {
        bool bySecond = !ReferenceEquals(table, Decompositions);
        int lo = 0, hi = table.Length / 3 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = table[3 * mid].CompareTo(first);
            if (c == 0 && bySecond) c = table[3 * mid + 1].CompareTo(second);
            if (c == 0) return 3 * mid;
            if (c < 0) lo = mid + 1;
            else hi = mid - 1;
        }

        return -1;
    }
}
