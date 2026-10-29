namespace PdfNetStack.Fonts;

/// <summary>
/// A design axis of a variable font, such as weight (wght) or width (wdth), with its range in the
/// axis's own units: weights run from 1 to 1000, widths are percentages of the normal width.
/// </summary>
public sealed class PdfFontAxis
{
    internal PdfFontAxis(string tag, string name, double minimum, double defaultValue, double maximum, bool hidden)
    {
        Tag = tag;
        Name = name;
        Minimum = minimum;
        Default = defaultValue;
        Maximum = maximum;
        IsHidden = hidden;
    }

    /// <summary>The four letter axis tag: wght, wdth, slnt, ital, opsz, or one of the font's own.</summary>
    public string Tag { get; }

    /// <summary>The axis name the font gives, such as "Weight".</summary>
    public string Name { get; }

    /// <summary>The smallest value.</summary>
    public double Minimum { get; }

    /// <summary>The value of the font's default instance.</summary>
    public double Default { get; }

    /// <summary>The largest value.</summary>
    public double Maximum { get; }

    /// <summary>True when the font asks applications not to offer the axis to users.</summary>
    public bool IsHidden { get; }

    /// <inheritdoc/>
    public override string ToString() => Compat.Format("{0} {1:0.###}..{2:0.###} (default {3:0.###})", Tag, Minimum, Maximum, Default);
}
