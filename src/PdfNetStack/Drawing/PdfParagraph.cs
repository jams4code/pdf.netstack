using System.Text;

namespace PdfNetStack.Drawing;

/// <summary>
/// Rich text to lay out: spans of text, each with its own style, and how lines are arranged. Line
/// feeds (\n, \r\n) and U+2029 start a new line; tabs advance four spaces; soft hyphens (U+00AD)
/// mark where a word may break, showing a hyphen when it does.
/// </summary>
/// <example>
/// <code>
/// var body = new PdfTextStyle(inter, 11);
/// var p = new PdfParagraph { Alignment = PdfTextAlignment.Justify, LineHeight = 1.5 }
///     .Add("Invoices are due within ", body)
///     .Add("30 days", body with { Font = interBold });
/// PdfTextLayout layout = g.DrawText(p, 50, 100, 300);   // returns the lines and the height used
/// </code>
/// </example>
public sealed class PdfParagraph
{
    private readonly StringBuilder _text = new();
    private readonly List<(int Start, PdfTextStyle Style)> _spans = [];

    /// <summary>Line alignment. Default <see cref="PdfTextAlignment.Start"/>.</summary>
    public PdfTextAlignment Alignment { get; set; }

    /// <summary>Base direction. Default <see cref="PdfTextDirection.Auto"/>.</summary>
    public PdfTextDirection Direction { get; set; }

    /// <summary>
    /// Line height as a multiple of each span's font size, as CSS line-height takes a number; null
    /// for the font's own spacing (ascent, descent and line gap), like CSS normal.
    /// </summary>
    public double? LineHeight
    {
        get => field;
        set => field = value is null || (value > 0 && !double.IsInfinity(value.Value))
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Line height must be positive.");
    }

    /// <summary>Indent of the first line, in points.</summary>
    public double FirstLineIndent { get; set; }

    /// <summary>The text of all spans.</summary>
    public string Text => _text.ToString();

    internal IReadOnlyList<(int Start, PdfTextStyle Style)> Spans => _spans;

    /// <summary>Appends text in a style.</summary>
    public PdfParagraph Add(string text, PdfTextStyle style)
    {
        Compat.ThrowIfNull(text, nameof(text));
        Compat.ThrowIfNull(style, nameof(style));
        if (text.Length == 0) return this;
        if (_spans.Count == 0 || !ReferenceEquals(_spans[^1].Style, style)) _spans.Add((_text.Length, style));
        _text.Append(text);
        return this;
    }

    internal PdfTextStyle StyleAt(int index)
    {
        int lo = 0, hi = _spans.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_spans[mid].Start <= index) lo = mid;
            else hi = mid - 1;
        }

        return _spans[lo].Style;
    }
}
