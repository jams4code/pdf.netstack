namespace PdfNetStack.Fonts;

/// <summary>
/// Bounds the variable font instances one document makes from values it does not control, such
/// as the font sizes and font-variation-settings of HTML. Axis values snap to a grid: whole units
/// for wght, wdth and opsz, half degrees for slnt, tenths for ital and a hundredth of the range
/// for any other axis. Each font then makes at most <see cref="MaxPerFont"/> locations; past
/// that, a request gets the nearest location already made.
/// </summary>
internal sealed class InstanceBudget
{
    /// <summary>The usual limit: enough for every weight and size a real document uses.</summary>
    public const int DefaultMaxPerFont = 32;

    private readonly Dictionary<object, List<(double[] Values, PdfOpenTypeFont Font)>> _made = [];

    public InstanceBudget(int maxPerFont = DefaultMaxPerFont)
    {
        if (maxPerFont < 1) throw new ArgumentOutOfRangeException(nameof(maxPerFont), "At least one instance per font.");
        MaxPerFont = maxPerFont;
    }

    /// <summary>The most locations made of one font program.</summary>
    public int MaxPerFont { get; }

    /// <summary>
    /// The instance of <paramref name="font"/> at <paramref name="values"/> (axis tag to user
    /// value; axes not named keep their default), snapped to the grid, or the nearest location
    /// already made once the font has used its budget.
    /// </summary>
    /// <exception cref="FontFormatException">The font's variation data is damaged.</exception>
    public PdfOpenTypeFont Instance(PdfOpenTypeFont font, IReadOnlyDictionary<string, double> values)
    {
        IReadOnlyList<PdfFontAxis> axes = font.VariationAxes;
        var snapped = new double[axes.Count];
        var request = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < axes.Count; i++)
        {
            PdfFontAxis axis = axes[i];
            double value = values.TryGetValue(axis.Tag, out double v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : axis.Default;
            snapped[i] = Snap(axis, value);
            if (!request.ContainsKey(axis.Tag)) request[axis.Tag] = snapped[i];
        }

        lock (_made)
        {
            if (!_made.TryGetValue(font.InstanceCache, out List<(double[] Values, PdfOpenTypeFont Font)>? made)) _made[font.InstanceCache] = made = [];
            foreach ((double[] location, PdfOpenTypeFont instance) in made)
            {
                if (location.SequenceEqual(snapped)) return instance;
            }

            if (made.Count >= MaxPerFont) return made.OrderBy(m => Distance(axes, m.Values, snapped)).First().Font;

            // Locations that round to the same point in the font share one instance, counted once.
            PdfOpenTypeFont created = font.GetInstance(request);
            if (!made.Any(m => ReferenceEquals(m.Font, created))) made.Add((snapped, created));
            return created;
        }
    }

    /// <summary>The value clamped to the axis and snapped to its grid.</summary>
    internal static double Snap(PdfFontAxis axis, double value)
    {
        double step = axis.Tag switch
        {
            "wght" or "wdth" or "opsz" => 1,
            "slnt" => 0.5,
            "ital" => 0.1,
            _ => (axis.Maximum - axis.Minimum) / 100,
        };
        value = Math.Max(axis.Minimum, Math.Min(axis.Maximum, value));
        if (step > 0) value = Math.Round(Math.Round(value / step, MidpointRounding.AwayFromZero) * step, 6);
        value = Math.Max(axis.Minimum, Math.Min(axis.Maximum, value));
        return value == 0 ? 0 : value;
    }

    /// <summary>How far apart two locations are, each axis measured as a fraction of its range.</summary>
    private static double Distance(IReadOnlyList<PdfFontAxis> axes, double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < axes.Count; i++)
        {
            double range = axes[i].Maximum - axes[i].Minimum;
            if (range <= 0) continue;
            double d = (a[i] - b[i]) / range;
            sum += d * d;
        }

        return sum;
    }
}
