using PdfNetStack.Fonts.OpenType;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts;

/// <summary>An inclusive range of values a face supports on one style dimension; a single value for a static font.</summary>
internal readonly record struct FontRange(double Minimum, double Maximum)
{
    public static FontRange Of(double value) => new(value, value);

    /// <summary>The range between two values in either order, as CSS swaps reversed descriptor ranges.</summary>
    public static FontRange Of(double a, double b) => a <= b ? new(a, b) : new(b, a);

    public bool Contains(double value) => value >= Minimum && value <= Maximum;

    public double Clamp(double value) => Math.Max(Minimum, Math.Min(Maximum, value));
}

/// <summary>
/// How a face matches (CSS Fonts 4, 5.2): its weight and width ranges, and its style as italic
/// values (0 upright, 1 italic, the ital axis between) and oblique angles (degrees, positive
/// leaning right, the slnt axis negated). A face has at least one of the two style scales.
/// </summary>
internal sealed record FontTraits(FontRange Weight, FontRange Width, FontRange? Italic, FontRange? Oblique)
{
    /// <summary>OS/2 width classes 1 to 9 as CSS percentages.</summary>
    private static readonly double[] WidthClasses = [50, 62.5, 75, 87.5, 100, 112.5, 125, 150, 200];

    /// <summary>A regular face: what an @font-face rule whose descriptors are auto matches as.</summary>
    public static readonly FontTraits Normal = new(FontRange.Of(400), FontRange.Of(100), FontRange.Of(0), FontRange.Of(0));

    /// <summary>
    /// The traits of a font from its OS/2, head and post tables and its variation axes: the axes'
    /// ranges, or the values at <paramref name="location"/> (user units, one per axis) for one instance.
    /// </summary>
    public static FontTraits Describe(ReadOnlySpan<byte> os2, ReadOnlySpan<byte> head, ReadOnlySpan<byte> post, IReadOnlyList<VariationAxis> axes, IReadOnlyList<double>? location)
    {
        int macStyle = head.Length >= 46 ? U16(head, 44) : 0;
        bool hasOs2 = os2.Length >= 64;
        int weightClass = hasOs2 ? U16(os2, 4) : 0;
        int widthClass = hasOs2 ? U16(os2, 6) : 0;
        int fsSelection = hasOs2 ? U16(os2, 62) : 0;
        double italicAngle = post.Length >= 8 ? (int)U32(post, 4) / 65536.0 : 0;

        FontRange weight = FontRange.Of(weightClass is >= 1 and <= 1000 ? weightClass : (macStyle & 1) != 0 ? 700 : 400);
        FontRange width = FontRange.Of(widthClass is >= 1 and <= 9 ? WidthClasses[widthClass - 1] : 100);
        bool italic = (fsSelection & 1) != 0 || (macStyle & 2) != 0;
        bool oblique = !italic && (fsSelection & 0x200) != 0;
        FontRange? italics = italic ? FontRange.Of(1) : oblique ? null : FontRange.Of(0);
        FontRange? obliques = italic ? null : FontRange.Of(oblique ? (italicAngle != 0 ? -italicAngle : 14) : 0);

        FontRange? ital = null;
        for (int i = 0; i < axes.Count; i++)
        {
            VariationAxis axis = axes[i];
            FontRange range = location is null ? new FontRange(axis.Minimum, axis.Maximum)
                : FontRange.Of(new FontRange(axis.Minimum, axis.Maximum).Clamp(i < location.Count ? location[i] : axis.Default));
            switch (axis.Tag)
            {
                case "wght":
                    weight = new FontRange(Math.Max(1, range.Minimum), Math.Min(1000, range.Maximum));
                    if (weight.Minimum > weight.Maximum) weight = FontRange.Of(weight.Minimum);
                    break;
                case "wdth":
                    width = range;
                    break;
                case "ital":
                    ital = range;
                    break;
                case "slnt":
                    obliques = FontRange.Of(-range.Maximum, -range.Minimum);
                    break;
            }
        }

        if (ital is FontRange axisItalics)
        {
            italics = axisItalics;
            if (!axes.Any(a => a.Tag == "slnt")) obliques = axisItalics.Minimum <= 0 ? FontRange.Of(0) : null;
        }
        else if (axes.Any(a => a.Tag == "slnt") && !italic)
        {
            // A slanting font is upright where its slant is zero.
            italics = obliques is FontRange o && o.Contains(0) ? FontRange.Of(0) : null;
        }

        return new FontTraits(weight, width, italics, obliques);
    }
}

/// <summary>A style found by matching: an italic value, or an oblique angle in degrees.</summary>
internal readonly record struct StyleMatch(bool IsOblique, double Value);

/// <summary>
/// The CSS font matching algorithm (CSS Fonts 4, 5.2 step 4) over faces with ranges: width first,
/// then style, then weight, each step keeping the faces that include the closest value. And the
/// variation values to apply to the face found (CSS Fonts 4, 7.2).
/// </summary>
internal static class FontMatcher
{
    /// <summary>The faces that match best, in their original order; empty only when <paramref name="faces"/> is.</summary>
    public static List<T> Select<T>(IEnumerable<T> faces, Func<T, FontTraits> traitsOf, PdfFontQuery query)
    {
        List<T> set = [.. faces];
        if (set.Count < 2) return set;

        if (MatchWidth(set.Select(f => traitsOf(f).Width), query.Width) is double width)
        {
            set = [.. set.Where(f => traitsOf(f).Width.Contains(width))];
        }

        if (MatchStyle(set.Select(traitsOf), query.Style, query.ObliqueAngle) is StyleMatch style)
        {
            set = [.. set.Where(f => (style.IsOblique ? traitsOf(f).Oblique : traitsOf(f).Italic) is FontRange r && r.Contains(style.Value))];
        }

        if (MatchWeight(set.Select(f => traitsOf(f).Weight), query.Weight) is double weight)
        {
            set = [.. set.Where(f => traitsOf(f).Weight.Contains(weight))];
        }

        return set;
    }

    /// <summary>
    /// The weight found for <paramref name="wanted"/>: inside a range if one has it; for 400 to
    /// 500, heavier up to 500 first, then lighter, then heavier; below 400 lighter first; above
    /// 500 heavier first.
    /// </summary>
    public static double? MatchWeight(IEnumerable<FontRange> ranges, double wanted)
    {
        List<FontRange> list = [.. ranges];
        if (wanted is >= 400 and <= 500)
        {
            return Ascending(list, wanted, 500, false) ?? Descending(list, wanted, double.NegativeInfinity, false) ?? Ascending(list, wanted, double.PositiveInfinity, false);
        }

        return wanted < 400
            ? Descending(list, wanted, double.NegativeInfinity, false) ?? Ascending(list, wanted, double.PositiveInfinity, false)
            : Ascending(list, wanted, double.PositiveInfinity, false) ?? Descending(list, wanted, double.NegativeInfinity, false);
    }

    /// <summary>The width found for <paramref name="wanted"/>: up to 100%, narrower first; above, wider first.</summary>
    public static double? MatchWidth(IEnumerable<FontRange> ranges, double wanted)
    {
        List<FontRange> list = [.. ranges];
        return wanted <= 100
            ? Descending(list, wanted, double.NegativeInfinity, false) ?? Ascending(list, wanted, double.PositiveInfinity, false)
            : Ascending(list, wanted, double.PositiveInfinity, false) ?? Descending(list, wanted, double.NegativeInfinity, false);
    }

    /// <summary>
    /// The italic value or oblique angle found for a style. Italic looks for italics, then
    /// obliques of 11 degrees or more, then upright faces; oblique looks for angles of the same
    /// sign, then italics, then upright faces; normal looks for upright, then oblique, then italic.
    /// </summary>
    public static StyleMatch? MatchStyle(IEnumerable<FontTraits> faces, PdfFontStyle style, double angle)
    {
        List<FontTraits> list = [.. faces];
        List<FontRange> italics = [.. list.Where(t => t.Italic is not null).Select(t => t.Italic!.Value)];
        List<FontRange> obliques = [.. list.Where(t => t.Oblique is not null).Select(t => t.Oblique!.Value)];
        const double Inf = double.PositiveInfinity;

        switch (style)
        {
            case PdfFontStyle.Italic:
                return Italic(Ascending(italics, 1, Inf, false) ?? Descending(italics, 1, 0, true))
                    ?? Oblique(Ascending(obliques, 11, Inf, false) ?? Descending(obliques, 11, 0, true))
                    ?? Italic(Descending(italics, 0, -Inf, false))
                    ?? Oblique(Descending(obliques, 0, -Inf, false));
            case PdfFontStyle.Oblique:
                // Negative angles search the mirror image.
                double sign = angle < 0 ? -1 : 1;
                double a = Math.Abs(angle);
                List<FontRange> mirrored = sign > 0 ? obliques : [.. obliques.Select(r => new FontRange(-r.Maximum, -r.Minimum))];
                double? found = a >= 11
                    ? Ascending(mirrored, a, Inf, false) ?? Descending(mirrored, a, 0, true)
                    : (mirrored.Any(r => r.Contains(a)) ? a : (double?)null) ?? Descending(mirrored, a, 0, true) ?? Ascending(mirrored, a, Inf, false);
                return Oblique(found * sign)
                    ?? Italic(a >= 11
                        ? Ascending(italics, 1, Inf, false) ?? Descending(italics, 1, 0, true)
                        : Descending(italics, 1, 0, true) ?? Ascending(italics, 1, Inf, false))
                    ?? Oblique(Descending(mirrored, 0, -Inf, false) * sign)
                    ?? Italic(Descending(italics, 0, -Inf, false));
            default:
                return Oblique(Ascending(obliques, 0, Inf, false))
                    ?? Italic(Ascending(italics, 0, Inf, false))
                    ?? Oblique(Descending(obliques, 0, -Inf, false))
                    ?? Italic(Descending(italics, 0, -Inf, false));
        }

        static StyleMatch? Italic(double? v) => v is double d ? new StyleMatch(false, d) : null;

        static StyleMatch? Oblique(double? v) => v is double d ? new StyleMatch(true, d == 0 ? 0 : d) : null;
    }

    /// <summary>
    /// The face's font at the variation a query asks for, built up as CSS Fonts 4, 7.2 orders it:
    /// <paramref name="start"/> (one value per axis, such as a named instance), then the weight,
    /// width and style found, clamped to <paramref name="applied"/>, then
    /// <paramref name="faceSettings"/>, the optical size and last the query's own variations.
    /// Axes the font does not have are skipped. A static font is returned as it is, and so is a
    /// variable font whose variation data turns out to be damaged. With a
    /// <paramref name="budget"/>, the location is snapped and counted against it.
    /// </summary>
    public static PdfOpenTypeFont Instance(PdfOpenTypeFont font, FontTraits applied, PdfFontQuery query, IReadOnlyList<double>? start = null, IEnumerable<KeyValuePair<string, double>>? faceSettings = null, InstanceBudget? budget = null)
    {
        if (!font.IsVariable) return font;
        IReadOnlyList<PdfFontAxis> axes = font.VariationAxes;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < axes.Count; i++) values[axes[i].Tag] = start is not null && i < start.Count ? start[i] : axes[i].Default;

        void Set(string tag, double value)
        {
            if (values.ContainsKey(tag) && !double.IsNaN(value) && !double.IsInfinity(value)) values[tag] = value;
        }

        Set("wght", applied.Weight.Clamp(query.Weight));
        Set("wdth", applied.Width.Clamp(query.Width));

        // At most one of ital and slnt follows the style (CSS Fonts 4, 7.2).
        if (MatchStyle([applied], query.Style, query.ObliqueAngle) is StyleMatch style)
        {
            if (style.IsOblique) Set("slnt", style.Value == 0 ? 0 : -style.Value);
            else Set("ital", style.Value);
        }

        if (faceSettings is not null)
        {
            foreach (KeyValuePair<string, double> v in faceSettings) Set(v.Key, v.Value);
        }

        if (query.OpticalSize is double size) Set("opsz", size);
        if (query.Variations is not null)
        {
            foreach (KeyValuePair<string, double> v in query.Variations) Set(v.Key, v.Value);
        }

        try
        {
            return budget is null ? font.GetInstance(values) : budget.Instance(font, values);
        }
        catch (FontFormatException)
        {
            return font;
        }
    }

    /// <summary>The smallest value at or above <paramref name="from"/> and below <paramref name="limit"/> that a range has.</summary>
    private static double? Ascending(List<FontRange> ranges, double from, double limit, bool exclusive)
    {
        double? best = null;
        foreach (FontRange r in ranges)
        {
            double v = Math.Max(r.Minimum, from);
            if (v > r.Maximum || v > limit || (exclusive && v == limit)) continue;
            if (best is null || v < best) best = v;
        }

        return best;
    }

    /// <summary>The largest value at or below <paramref name="from"/> and above <paramref name="limit"/> that a range has.</summary>
    private static double? Descending(List<FontRange> ranges, double from, double limit, bool exclusive)
    {
        double? best = null;
        foreach (FontRange r in ranges)
        {
            double v = Math.Min(r.Maximum, from);
            if (v < r.Minimum || v < limit || (exclusive && v == limit)) continue;
            if (best is null || v > best) best = v;
        }

        return best;
    }
}
