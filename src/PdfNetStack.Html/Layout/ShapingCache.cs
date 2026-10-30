using PdfNetStack.Fonts;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Shaped runs of one conversion, by font, text, direction and script. Shaping reads only the
/// run's own characters, so a run shapes the same wherever it stands; only the clusters, which
/// index the paragraph, move with it. Table cells shape their text once to measure the columns
/// and again to lay out, and generated documents repeat the same labels, numbers and phrases:
/// those runs shape once. Bounded: runs longer than <see cref="MaxRunLength"/> are not kept, as
/// paragraphs rarely repeat, and the cache stops growing at <see cref="MaxCharacters"/>.
/// </summary>
internal sealed class ShapingCache
{
    /// <summary>The longest run kept, in UTF-16 units.</summary>
    public const int MaxRunLength = 256;

    /// <summary>The most text the cache holds, in UTF-16 units, about 4 MB with the glyphs.</summary>
    public const int MaxCharacters = 1 << 18;

    private readonly Dictionary<Key, ShapedGlyph[]> _runs = [];
    private int _characters;

    private readonly record struct Key(PdfFont Font, string Text, bool RightToLeft, string Script);

    /// <summary>
    /// The glyphs of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/>
    /// as <see cref="PdfFont.ShapeRun"/> gives them: a new list the caller owns.
    /// </summary>
    public List<ShapedGlyph> Shape(PdfFont font, string text, int start, int end, bool rightToLeft, string script)
    {
        int length = end - start;
        if (length > MaxRunLength) return font.ShapeRun(text, start, end, rightToLeft, script);
        var key = new Key(font, text.Substring(start, length), rightToLeft, script);
        if (!_runs.TryGetValue(key, out ShapedGlyph[]? shaped))
        {
            List<ShapedGlyph> glyphs = font.ShapeRun(text, start, end, rightToLeft, script);
            if (_characters + length > MaxCharacters) return glyphs;
            shaped = new ShapedGlyph[glyphs.Count];
            for (int i = 0; i < shaped.Length; i++) shaped[i] = glyphs[i] with { Cluster = glyphs[i].Cluster - start };
            _runs[key] = shaped;
            _characters += length;
            return glyphs;
        }

        var result = new List<ShapedGlyph>(shaped.Length);
        foreach (ShapedGlyph g in shaped) result.Add(g with { Cluster = g.Cluster + start });
        return result;
    }
}
