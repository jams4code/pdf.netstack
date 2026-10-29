using PdfNetStack.Fonts;

namespace PdfNetStack.Drawing;

/// <summary>Horizontal alignment of the lines of a paragraph.</summary>
public enum PdfTextAlignment
{
    /// <summary>Left in left to right paragraphs, right in right to left ones.</summary>
    Start,

    /// <summary>Right in left to right paragraphs, left in right to left ones.</summary>
    End,

    /// <summary>Left edge.</summary>
    Left,

    /// <summary>Right edge.</summary>
    Right,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>Both edges, by widening the spaces between words; the last line aligns to the start.</summary>
    Justify,
}

/// <summary>Base direction of a paragraph.</summary>
public enum PdfTextDirection
{
    /// <summary>From the first strong character, as the Unicode bidirectional algorithm does.</summary>
    Auto,

    /// <summary>Left to right, as in English.</summary>
    LeftToRight,

    /// <summary>Right to left, as in Arabic and Hebrew.</summary>
    RightToLeft,
}

/// <summary>How a span of text looks: font, size, paint and decorations.</summary>
/// <param name="Font">The font. Characters it lacks are looked up in <see cref="Fallbacks"/>.</param>
/// <param name="Size">Font size in points.</param>
public sealed record PdfTextStyle(PdfFont Font, double Size)
{
    /// <summary>The font.</summary>
    public PdfFont Font { get; init; } = Font ?? throw new ArgumentNullException(nameof(Font));

    /// <summary>Font size in points.</summary>
    public double Size { get; init; } = Size > 0 && !double.IsInfinity(Size) ? Size : throw new ArgumentOutOfRangeException(nameof(Size), "Font size must be positive.");

    /// <summary>Fill of the glyphs, and of underlines and strikethroughs. Default black; null for outlines only.</summary>
    public PdfBrush? Brush { get; init; } = PdfColor.Black;

    /// <summary>Outline of the glyphs, or null.</summary>
    public PdfPen? Pen { get; init; }

    /// <summary>Fonts tried, in order, for characters <see cref="Font"/> has no glyph for.</summary>
    public IReadOnlyList<PdfFont> Fallbacks { get; init; } = [];

    /// <summary>Extra space after every character, in points; may be negative.</summary>
    public double LetterSpacing { get; init; }

    /// <summary>Extra space added to every space between words, in points.</summary>
    public double WordSpacing { get; init; }

    /// <summary>Draws a line under the text.</summary>
    public bool Underline { get; init; }

    /// <summary>Draws a line through the text.</summary>
    public bool Strikethrough { get; init; }
}
