namespace PdfNetStack.Fonts;

/// <summary>The slant a <see cref="PdfFontQuery"/> asks for, as CSS font-style.</summary>
public enum PdfFontStyle
{
    /// <summary>Upright.</summary>
    Normal,

    /// <summary>An italic face, or an oblique one when the family has no italic.</summary>
    Italic,

    /// <summary>An oblique face at <see cref="PdfFontQuery.ObliqueAngle"/>, or an italic one when the family has no oblique.</summary>
    Oblique,
}

/// <summary>
/// What <see cref="PdfFontCollection.Find(string, PdfFontQuery)"/> looks for in a family: weight,
/// width and style, matched as CSS does, and for variable fonts the optical size and any other
/// axis values to apply to the instance found.
/// </summary>
/// <example>
/// <code>
/// PdfOpenTypeFont? caption = fonts.Find("Roboto Flex", new PdfFontQuery
/// {
///     Weight = 650,
///     Width = 87.5,
///     Style = PdfFontStyle.Italic,
///     OpticalSize = 9,
/// });
/// </code>
/// </example>
public sealed class PdfFontQuery
{
    /// <summary>CSS weight, 1 to 1000: 400 regular, 700 bold. Default 400.</summary>
    public double Weight { get; init; } = 400;

    /// <summary>Width as a percentage of normal, as CSS font-width: 75 condensed, 125 expanded. Default 100.</summary>
    public double Width { get; init; } = 100;

    /// <summary>Upright, italic or oblique. Default upright.</summary>
    public PdfFontStyle Style { get; init; }

    /// <summary>
    /// The slant in degrees for <see cref="PdfFontStyle.Oblique"/>, -90 to 90, positive leaning
    /// right as in CSS. Default 14, the CSS default for oblique.
    /// </summary>
    public double ObliqueAngle { get; init; } = 14;

    /// <summary>
    /// The size the text is set at, for a variable font's optical size axis (opsz), in the axis's
    /// units. Null leaves the axis at its default; fonts without the axis ignore it.
    /// </summary>
    public double? OpticalSize { get; init; }

    /// <summary>
    /// Axis values by tag, applied last over the values the match chose, as CSS
    /// font-variation-settings: <c>{ ["wght"] = 625, ["GRAD"] = 50 }</c>. Tags the font does
    /// not have are ignored and values are clamped to each axis's range.
    /// </summary>
    public IReadOnlyDictionary<string, double>? Variations { get; init; }

    /// <summary>Throws when a value is outside its range.</summary>
    internal void Validate()
    {
        if (!(Weight is >= 1 and <= 1000)) throw new ArgumentOutOfRangeException(nameof(Weight), "CSS weights range from 1 to 1000.");
        if (!(Width >= 0) || double.IsInfinity(Width)) throw new ArgumentOutOfRangeException(nameof(Width), "The width is a positive percentage.");
        if (!(ObliqueAngle is >= -90 and <= 90)) throw new ArgumentOutOfRangeException(nameof(ObliqueAngle), "Oblique angles range from -90 to 90 degrees.");
        if (OpticalSize is double size && (!(size > 0) || double.IsInfinity(size))) throw new ArgumentOutOfRangeException(nameof(OpticalSize), "The optical size is a positive number.");
        if (Variations is not null)
        {
            foreach (KeyValuePair<string, double> v in Variations)
            {
                if (double.IsNaN(v.Value) || double.IsInfinity(v.Value)) throw new ArgumentException($"The value of axis '{v.Key}' is not a finite number.", nameof(Variations));
            }
        }
    }
}
