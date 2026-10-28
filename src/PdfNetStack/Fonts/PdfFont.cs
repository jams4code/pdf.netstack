using System.Globalization;

namespace PdfNetStack.Fonts;

/// <summary>
/// A font for drawing text. Font size is given when drawing, so one instance serves every size.
/// Vertical metrics are fractions of the font size: multiply by the size in points to get points.
/// </summary>
/// <remarks>
/// Use <see cref="PdfStandardFont"/> for the 14 fonts every PDF viewer has. A font is written into a
/// document the first time text is drawn with it, once per document, and is safe to share between
/// documents and threads.
/// </remarks>
public abstract class PdfFont
{
    private protected PdfFont()
    {
    }

    /// <summary>PostScript name, as written to /BaseFont.</summary>
    public abstract string Name { get; }

    /// <summary>Family name, such as "Helvetica".</summary>
    public abstract string FamilyName { get; }

    /// <summary>True for bold and heavier weights.</summary>
    public abstract bool IsBold { get; }

    /// <summary>True for italic and oblique styles.</summary>
    public abstract bool IsItalic { get; }

    /// <summary>Height above the baseline used for line spacing.</summary>
    public double Ascent { get; private protected set; }

    /// <summary>Depth below the baseline used for line spacing, as a positive number.</summary>
    public double Descent { get; private protected set; }

    /// <summary>Extra space the font designer recommends between lines.</summary>
    public double LineGap { get; private protected set; }

    /// <summary>Height of flat capital letters such as H.</summary>
    public double CapHeight { get; private protected set; }

    /// <summary>Height of flat lowercase letters such as x.</summary>
    public double XHeight { get; private protected set; }

    /// <summary>Distance from the baseline down to the middle of an underline.</summary>
    public double UnderlinePosition { get; private protected set; }

    /// <summary>Underline stroke thickness.</summary>
    public double UnderlineThickness { get; private protected set; }

    /// <summary>Height of the middle of a strikethrough line above the baseline.</summary>
    public double StrikeoutPosition { get; private protected set; }

    /// <summary>Strikethrough stroke thickness.</summary>
    public double StrikeoutThickness { get; private protected set; }

    /// <summary>Slant in degrees, counterclockwise from vertical; negative for fonts leaning right.</summary>
    public double ItalicAngle { get; private protected set; }

    /// <summary>Default line height for <paramref name="size"/>: ascent, descent and line gap, in points.</summary>
    public double GetLineHeight(double size) => (Ascent + Descent + LineGap) * size;

    /// <summary>Width of <paramref name="text"/> drawn on one line at <paramref name="size"/>, in points.</summary>
    /// <exception cref="ArgumentException">The font has no glyph for a character of the text.</exception>
    public double MeasureString(string text, double size)
    {
        Compat.ThrowIfNull(text, nameof(text));
        double total = 0;
        foreach (ShapedGlyph g in Shape(text)) total += g.Advance;
        return total * size / 1000;
    }

    /// <summary>True when the font has a glyph for the Unicode code point.</summary>
    public bool HasGlyph(int codePoint) => TryGetGlyph(codePoint, out _);

    /// <summary>
    /// True when every character of <paramref name="text"/> can be drawn: the font has a glyph for
    /// it, or it is an invisible control or format character, which drawing skips.
    /// </summary>
    public bool CanRender(string text)
    {
        Compat.ThrowIfNull(text, nameof(text));
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            if (!IsInvisible(text, i) && !TryGetGlyph(char.ConvertToUtf32(text, i), out _)) return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>Glyph for a code point, in the font's own numbering.</summary>
    internal abstract bool TryGetGlyph(int codePoint, out int glyph);

    /// <summary>Advance width of a glyph in thousandths of the font size.</summary>
    internal abstract double GetAdvance(int glyph);

    /// <summary>Adjustment between two glyphs in thousandths of the font size; negative moves them closer.</summary>
    internal virtual double GetKerning(int left, int right) => 0;

    /// <summary>Creates the per document state that encodes text and writes the font on save.</summary>
    internal abstract FontResource CreateResource(PdfDocument document);

    /// <summary>
    /// Maps text to positioned glyphs. Control and format characters (tabs, line breaks, zero
    /// width joiners, bidi marks, soft hyphens) are skipped.
    /// </summary>
    /// <exception cref="ArgumentException">The font has no glyph for a character.</exception>
    internal List<ShapedGlyph> Shape(string text)
    {
        var glyphs = new List<ShapedGlyph>(text.Length);
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            if (IsInvisible(text, i)) continue;
            int codePoint = char.ConvertToUtf32(text, i);
            if (!TryGetGlyph(codePoint, out int glyph))
            {
                throw new ArgumentException(
                    $"{Name} has no glyph for U+{codePoint:X4}. Use a font that covers this character.", nameof(text));
            }

            if (glyphs.Count > 0)
            {
                ShapedGlyph previous = glyphs[^1];
                double kern = GetKerning(previous.Glyph, glyph);
                if (kern != 0) glyphs[^1] = previous with { Advance = previous.Advance + kern };
            }

            glyphs.Add(new ShapedGlyph(glyph, codePoint, GetAdvance(glyph)));
        }

        return glyphs;
    }

    private static bool IsInvisible(string text, int index) =>
        CharUnicodeInfo.GetUnicodeCategory(text, index) is UnicodeCategory.Control or UnicodeCategory.Format;
}

/// <summary>A glyph to draw and how far to move after it, in thousandths of the font size.</summary>
internal readonly record struct ShapedGlyph(int Glyph, int CodePoint, double Advance);
