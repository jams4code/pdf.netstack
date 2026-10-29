using PdfNetStack.Drawing;

namespace PdfNetStack;

/// <summary>
/// One glyph drawn on a page, read back as text: what it says, where it sits and in which font.
/// Coordinates are in the page's default user space, in points from the bottom left of the
/// coordinate system, before the page's /Rotate is applied.
/// </summary>
/// <example>
/// <code>
/// foreach (PdfLetter letter in doc.Pages[0].GetLetters())
/// {
///     Console.WriteLine($"{letter.Text} at {letter.BaselineStart.X:0.#}, {letter.BaselineStart.Y:0.#} in {letter.FontName} {letter.FontSize:0.#}");
/// }
/// </code>
/// </example>
public sealed class PdfLetter
{
    internal PdfLetter(string text, PdfRectangle bounds, PdfPoint baselineStart, PdfPoint baselineEnd, PdfPoint direction, string fontName, double fontSize, int renderMode)
    {
        Text = text;
        Bounds = bounds;
        BaselineStart = baselineStart;
        BaselineEnd = baselineEnd;
        Direction = direction;
        FontName = fontName;
        FontSize = fontSize;
        RenderMode = renderMode;
    }

    /// <summary>
    /// The text the glyph stands for: usually one character, several for a ligature such as "fi"
    /// or a character outside the Basic Multilingual Plane, the replacement text of a span marked
    /// with /ActualText, or U+FFFD when the font does not say.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// The smallest rectangle around the glyph's box: its advance width from the font's descent to
    /// its ascent, or from a quarter of the font size below the baseline to three quarters above
    /// when the font has no metrics. A rotated glyph's box is rotated before the rectangle is taken.
    /// </summary>
    public PdfRectangle Bounds { get; }

    /// <summary>Where the glyph's baseline starts: its origin, after any text rise.</summary>
    public PdfPoint BaselineStart { get; }

    /// <summary>Where the glyph's baseline ends: the origin moved by the glyph's advance.</summary>
    public PdfPoint BaselineEnd { get; }

    /// <summary>The base font name without a subset prefix, such as "Helvetica" for "ABCDEF+Helvetica".</summary>
    public string FontName { get; }

    /// <summary>
    /// The font size as it appears on the page, in points: the size set with Tf scaled by the text
    /// and transformation matrices, so 1 point text scaled 12 times reads as 12.
    /// </summary>
    public double FontSize { get; }

    /// <summary>
    /// The text rendering mode (ISO 32000-1, 9.3.6): 0 fill, 1 stroke, 2 fill and stroke, 3
    /// invisible (as in the text layer of scanned pages), 4 to 7 the same adding the glyph to the clip.
    /// </summary>
    public int RenderMode { get; }

    /// <summary>The direction of the baseline in degrees, counterclockwise from the x axis: 0 for ordinary text, -90 for vertical writing.</summary>
    public double Angle => Math.Atan2(Direction.Y, Direction.X) * 180 / Math.PI;

    /// <summary>Unit vector along the writing direction.</summary>
    internal PdfPoint Direction { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}

/// <summary>A word on a page: letters on one line with no space between them.</summary>
public sealed class PdfWord
{
    internal PdfWord(string text, PdfRectangle bounds, IReadOnlyList<PdfLetter> letters)
    {
        Text = text;
        Bounds = bounds;
        Letters = letters;
    }

    /// <summary>The word's text, in reading order, without spaces.</summary>
    public string Text { get; }

    /// <summary>The smallest rectangle around the word's letters, in default user space.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>
    /// The letters of the word in reading order. A span replaced by /ActualText is one letter, and
    /// when its text holds several words, each of them lists that letter.
    /// </summary>
    public IReadOnlyList<PdfLetter> Letters { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}
