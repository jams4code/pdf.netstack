using PdfNetStack.Fonts;

namespace PdfNetStack.Text;

/// <summary>
/// Shapes one line of text in one font: the bidirectional algorithm assigns levels, the text is
/// split into runs of one level and one script, each run is shaped, and the runs are put in
/// visual order (UAX #9, L1 and L2). Right to left runs come back reversed, ready to draw left to
/// right.
/// </summary>
internal static class LineShaper
{
    /// <param name="font">The font; it must have a glyph for every visible character.</param>
    /// <param name="text">One line of text.</param>
    /// <param name="direction">0 left to right, 1 right to left, null from the first strong character.</param>
    public static List<ShapedGlyph> Shape(PdfFont font, string text, int? direction)
    {
        var glyphs = new List<ShapedGlyph>(text.Length);
        if (text.Length == 0) return glyphs;

        var codePoints = new List<int>(text.Length);
        var starts = new List<int>(text.Length);
        bool simple = direction != 1;
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int cp = char.IsSurrogate(text[i]) && !char.IsSurrogatePair(text, i) ? 0xFFFD : char.ConvertToUtf32(text, i);
            if (!PdfFont.IsInvisible(text, i) && !font.TryGetGlyph(cp, out _)) throw font.MissingGlyph(cp);
            codePoints.Add(cp);
            starts.Add(i);
            if (simple && UnicodeData.Bidi(cp) is BidiClass.R or BidiClass.AL or BidiClass.AN
                or BidiClass.RLE or BidiClass.RLO or BidiClass.RLI or BidiClass.FSI)
            {
                simple = false;
            }
        }

        starts.Add(text.Length);
        int n = codePoints.Count;
        byte[] levels = simple ? new byte[n] : Bidi.Analyze([.. codePoints], direction ?? 2).LineLevels(0, n);
        int[] scripts = ResolveScripts(codePoints);

        var runs = new List<(int Start, int End, byte Level, int Script)>();
        for (int k = 0; k < n;)
        {
            int end = k + 1;
            while (end < n && levels[end] == levels[k] && scripts[end] == scripts[k]) end++;
            runs.Add((k, end, levels[k], scripts[k]));
            k = end;
        }

        int[] order = Bidi.Reorder([.. runs.Select(r => r.Level)]);
        foreach (int r in order)
        {
            (int start, int end, byte level, int script) = runs[r];
            bool rtl = level % 2 == 1;
            List<ShapedGlyph> run = font.ShapeRun(text, starts[start], starts[end], rtl, UnicodeData.ScriptTags[script]);
            if (rtl) run.Reverse();
            glyphs.AddRange(run);
        }

        return glyphs;
    }

    /// <summary>
    /// Common and inherited characters (spaces, digits, punctuation, combining marks) take the
    /// script of the text before them, or after them at the start of the line.
    /// </summary>
    private static int[] ResolveScripts(List<int> codePoints)
    {
        var scripts = new int[codePoints.Count];
        int current = -1;
        for (int k = 0; k < scripts.Length; k++)
        {
            int s = UnicodeData.Script(codePoints[k]);
            if (s > 2) current = s;
            scripts[k] = s > 2 ? s : current;
        }

        int first = Array.Find(scripts, s => s > 2);
        for (int k = 0; k < scripts.Length && scripts[k] < 0; k++) scripts[k] = first > 2 ? first : UnicodeData.CommonScript;
        return scripts;
    }
}
