using System.Globalization;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// The Thai and Lao shaper, after HarfBuzz's, which follows Uniscribe and the Thai OpenType
/// shaping notes of the Thai Linux Working Group: sara am splits into nikhahit and sara aa, the
/// nikhahit moving back over the above-base marks before it; and for a Thai font without Thai
/// substitutions, marks and descending consonants take the font's private use glyphs (the
/// Windows or Macintosh layout) so marks clear tall consonants and descenders.
/// </summary>
internal sealed partial class Shaper
{
    private static readonly ShapingFeature[][] ThaiStages =
    [
        [new("rvrn")], [new("ccmp"), new("locl"), new("rlig"), new("calt"), new("clig"), new("liga"), new("rclt")],
    ];

    // Private use replacements: the character, then its glyph in the Windows and the Macintosh
    // layouts. Shift down, shift down and left, shift left, and remove descender.
    private static readonly int[] ShiftDown =
    [
        0x0E48, 0xF70A, 0xF88B, 0x0E49, 0xF70B, 0xF88E, 0x0E4A, 0xF70C, 0xF891, 0x0E4B, 0xF70D, 0xF894,
        0x0E4C, 0xF70E, 0xF897, 0x0E38, 0xF718, 0xF89B, 0x0E39, 0xF719, 0xF89C, 0x0E3A, 0xF71A, 0xF89D,
    ];

    private static readonly int[] ShiftDownLeft =
    [
        0x0E48, 0xF705, 0xF88C, 0x0E49, 0xF706, 0xF88F, 0x0E4A, 0xF707, 0xF892, 0x0E4B, 0xF708, 0xF895,
        0x0E4C, 0xF709, 0xF898,
    ];

    private static readonly int[] ShiftLeft =
    [
        0x0E48, 0xF713, 0xF88A, 0x0E49, 0xF714, 0xF88D, 0x0E4A, 0xF715, 0xF890, 0x0E4B, 0xF716, 0xF893,
        0x0E4C, 0xF717, 0xF896, 0x0E31, 0xF710, 0xF884, 0x0E34, 0xF701, 0xF885, 0x0E35, 0xF702, 0xF886,
        0x0E36, 0xF703, 0xF887, 0x0E37, 0xF704, 0xF888, 0x0E47, 0xF712, 0xF889, 0x0E4D, 0xF711, 0xF899,
    ];

    private static readonly int[] RemoveDescender = [0x0E0D, 0xF70F, 0xF89A, 0x0E10, 0xF700, 0xF89E];

    private enum ThaiAction
    {
        None,
        ShiftDown,
        ShiftLeft,
        ShiftDownLeft,
        RemoveDescender,
    }

    /// <summary>
    /// Splits each sara am. A nikhahit typed after above-base marks stays where it is; only the one
    /// from a sara am moves, since that is how it is drawn.
    /// </summary>
    private void PreprocessThai(bool thai)
    {
        List<GlyphInfo> input = _buffer;
        var output = new List<GlyphInfo>(input.Count + 4);
        for (int next = 1; next <= input.Count; next++)
        {
            GlyphInfo g = input[next - 1];
            int u = g.CodePoint;
            if ((u & ~0x80) != 0x0E33)
            {
                output.Add(g);
                continue;
            }

            // The nikhahit is a mark of class 0, as the sara am's other properties stay.
            GlyphInfo nikhahit = g.Copy();
            nikhahit.CodePoint = u - 0x0E33 + 0x0E4D;
            nikhahit.Category = UnicodeCategory.NonSpacingMark;
            GlyphInfo aa = g;
            aa.CodePoint = u - 1;
            aa.Text = "";
            output.Add(nikhahit);
            output.Add(aa);

            int end = output.Count, start = end - 2;
            while (start > 0 && IsThaiAboveMark(output[start - 1].CodePoint)) start--;
            if (start + 2 < end)
            {
                MergeOutClusters(output, start, end, input, next);
                output.RemoveAt(end - 2);
                output.Insert(start, nikhahit);
            }
            else if (start > 0)
            {
                MergeOutClusters(output, start - 1, end, input, next);
            }
        }

        _buffer = output;
        if (thai && _gsubScript != "thai") ThaiPrivateUse();
    }

    /// <summary>Above-base marks of Thai, and of Lao 0x80 above them.</summary>
    private static bool IsThaiAboveMark(int u)
    {
        int t = u & ~0x80;
        return u is >= 0x0E00 and <= 0x0EFF && (t is (>= 0x0E34 and <= 0x0E37) or (>= 0x0E47 and <= 0x0E4E) or 0x0E31 or 0x0E3B);
    }

    /// <summary>
    /// For fonts without Thai substitutions: two small state machines follow each consonant's
    /// marks, one above (tall consonants push the first mark left, a vowel above pushes the tone
    /// up) and one below (descenders push marks down or lose their descender).
    /// </summary>
    private void ThaiPrivateUse()
    {
        // States above: 0 nothing yet, 1 after a tall consonant, 2 a mark on a tall consonant, 3 done.
        // States below: 0 no descender, 1 removable descender, 2 strict descender.
        int above = 3, below = 2, baseIndex = 0;
        for (int i = 0; i < _buffer.Count; i++)
        {
            GlyphInfo g = _buffer[i];
            int mark = ThaiMarkType(g.CodePoint);
            if (mark < 0)
            {
                int consonant = ThaiConsonantType(g.CodePoint);
                above = consonant == 1 ? 1 : consonant < 0 ? 3 : 0;
                below = consonant switch { 2 => 1, 3 => 2, < 0 => 2, _ => 0 };
                baseIndex = i;
                continue;
            }

            (above, ThaiAction aboveAction) = (above, mark) switch
            {
                (0, 0) => (3, ThaiAction.None),
                (0, 2) => (3, ThaiAction.ShiftDown),
                (1, 0) => (2, ThaiAction.ShiftLeft),
                (1, 2) => (2, ThaiAction.ShiftDownLeft),
                (2, 0) => (3, ThaiAction.None),
                (2, 2) => (3, ThaiAction.ShiftLeft),
                (_, 1) => (above, ThaiAction.None),
                _ => (3, ThaiAction.None),
            };
            (below, ThaiAction belowAction) = (below, mark) switch
            {
                (0, 1) => (2, ThaiAction.None),
                (1, 1) => (2, ThaiAction.RemoveDescender),
                (2, 1) => (2, ThaiAction.ShiftDown),
                _ => (below, ThaiAction.None),
            };
            ThaiAction action = aboveAction != ThaiAction.None ? aboveAction : belowAction;
            if (action == ThaiAction.RemoveDescender) _buffer[baseIndex].CodePoint = ThaiPrivateUse(_buffer[baseIndex].CodePoint, action);
            else g.CodePoint = ThaiPrivateUse(g.CodePoint, action);
        }
    }

    /// <summary>0 above-base vowel or sign, 1 below-base vowel, 2 tone mark; -1 for anything else.</summary>
    private static int ThaiMarkType(int u) => u switch
    {
        0x0E31 or (>= 0x0E34 and <= 0x0E37) or 0x0E47 or 0x0E4D or 0x0E4E => 0,
        >= 0x0E38 and <= 0x0E3A => 1,
        >= 0x0E48 and <= 0x0E4C => 2,
        _ => -1,
    };

    /// <summary>0 ordinary consonant, 1 ascending, 2 with a removable descender, 3 with a strict one; -1 for anything else.</summary>
    private static int ThaiConsonantType(int u) => u switch
    {
        0x0E1B or 0x0E1D or 0x0E1F => 1,
        0x0E0D or 0x0E10 => 2,
        0x0E0E or 0x0E0F => 3,
        >= 0x0E01 and <= 0x0E2E => 0,
        _ => -1,
    };

    /// <summary>The private use character for <paramref name="u"/> that the font has, Windows layout first; else <paramref name="u"/>.</summary>
    private int ThaiPrivateUse(int u, ThaiAction action)
    {
        int[]? table = action switch
        {
            ThaiAction.ShiftDown => ShiftDown,
            ThaiAction.ShiftDownLeft => ShiftDownLeft,
            ThaiAction.ShiftLeft => ShiftLeft,
            ThaiAction.RemoveDescender => RemoveDescender,
            _ => null,
        };
        if (table is null) return u;
        for (int i = 0; i < table.Length; i += 3)
        {
            if (table[i] != u) continue;
            if (_font.TryGetGlyph(table[i + 1], out _)) return table[i + 1];
            if (_font.TryGetGlyph(table[i + 2], out _)) return table[i + 2];
            break;
        }

        return u;
    }
}
