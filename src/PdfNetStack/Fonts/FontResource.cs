using PdfNetStack.Objects;

namespace PdfNetStack.Fonts;

/// <summary>
/// A font as used by one document: the font dictionary, reserved when the font is first used, and
/// the codes given to its glyphs. <see cref="Finish"/> fills in widths, encoding and font data
/// from what was drawn, and runs again on every save.
/// </summary>
internal abstract class FontResource
{
    protected FontResource(PdfDocument document)
    {
        Document = document;
        Dictionary = new PdfDictionary();
        Reference = document.AddObject(Dictionary);
    }

    protected PdfDocument Document { get; }

    public PdfDictionary Dictionary { get; }

    public PdfReference Reference { get; }

    /// <summary>True for two byte codes, written as hex strings; false for one byte codes.</summary>
    public abstract bool IsTwoByte { get; }

    /// <summary>The code for <paramref name="glyph"/>, assigning one on first use.</summary>
    /// <exception cref="InvalidOperationException">The font's code space is full.</exception>
    public abstract int Encode(ShapedGlyph glyph);

    /// <summary>The advance a viewer uses for <paramref name="glyph"/>: the width written for its code, in thousandths.</summary>
    public abstract double Width(int glyph);

    /// <summary>Writes the font dictionary entries that depend on the glyphs used so far.</summary>
    public abstract void Finish();
}
