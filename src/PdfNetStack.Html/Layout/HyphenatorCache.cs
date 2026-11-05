using PdfNetStack.Drawing;

namespace PdfNetStack.Html.Layout;

/// <summary>The hyphenators of one conversion by language tag: the application's function is asked once per tag.</summary>
internal sealed class HyphenatorCache(Func<string, PdfHyphenator?> find)
{
    private readonly Dictionary<string, PdfHyphenator?> _byTag = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The hyphenator for a tag from a lang attribute; null for no language, an empty one or one without patterns.</summary>
    public PdfHyphenator? For(string? tag)
    {
        tag = tag?.Trim();
        if (string.IsNullOrEmpty(tag)) return null;
        if (!_byTag.TryGetValue(tag!, out PdfHyphenator? hyphenator)) _byTag[tag!] = hyphenator = find(tag!);
        return hyphenator;
    }
}
