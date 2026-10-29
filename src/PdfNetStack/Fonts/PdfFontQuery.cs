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
    private readonly double _weight = 400;
    private readonly double _width = 100;
    private readonly double _obliqueAngle = 14;
    private readonly double? _opticalSize;
    private readonly IReadOnlyDictionary<string, double>? _variations;

    /// <summary>CSS weight, 1 to 1000: 400 regular, 700 bold. Default 400.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a number from 1 to 1000.</exception>
    public double Weight
    {
        get => _weight;
        init => _weight = value is >= 1 and <= 1000 ? value : throw new ArgumentOutOfRangeException(nameof(Weight), value, "CSS weights range from 1 to 1000.");
    }

    /// <summary>Width as a percentage of normal, as CSS font-width: 75 condensed, 125 expanded. Default 100.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, infinite or not a number.</exception>
    public double Width
    {
        get => _width;
        init => _width = value >= 0 && !double.IsInfinity(value) ? value : throw new ArgumentOutOfRangeException(nameof(Width), value, "The width is a finite percentage, 0 or more.");
    }

    /// <summary>Upright, italic or oblique. Default upright.</summary>
    public PdfFontStyle Style { get; init; }

    /// <summary>
    /// The slant in degrees for <see cref="PdfFontStyle.Oblique"/>, -90 to 90, positive leaning
    /// right as in CSS. Default 14, the CSS default for oblique.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a number from -90 to 90.</exception>
    public double ObliqueAngle
    {
        get => _obliqueAngle;
        init => _obliqueAngle = value is >= -90 and <= 90 ? value : throw new ArgumentOutOfRangeException(nameof(ObliqueAngle), value, "Oblique angles range from -90 to 90 degrees.");
    }

    /// <summary>
    /// The size the text is set at, for a variable font's optical size axis (opsz), in the axis's
    /// units. Null leaves the axis at its default; fonts without the axis ignore it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative, infinite or not a number.</exception>
    public double? OpticalSize
    {
        get => _opticalSize;
        init => _opticalSize = value is null || (value > 0 && !double.IsInfinity(value.Value)) ? value : throw new ArgumentOutOfRangeException(nameof(OpticalSize), value, "The optical size is a finite number above 0.");
    }

    /// <summary>
    /// Axis values by tag, applied last over the values the match chose, as CSS
    /// font-variation-settings: <c>{ ["wght"] = 625, ["GRAD"] = 50 }</c>. Tags the font does
    /// not have are ignored and values are clamped to each axis's range. The values are copied
    /// when set, so changing the dictionary afterwards does not change the query.
    /// </summary>
    /// <exception cref="ArgumentException">A value is infinite or not a number.</exception>
    public IReadOnlyDictionary<string, double>? Variations
    {
        get => _variations;
        init
        {
            if (value is null)
            {
                _variations = null;
                return;
            }

            var copy = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> v in value)
            {
                if (double.IsNaN(v.Value) || double.IsInfinity(v.Value)) throw new ArgumentException($"The value of axis '{v.Key}' is not a finite number.", nameof(Variations));
                copy[v.Key] = v.Value;
            }

            _variations = copy;
        }
    }
}
