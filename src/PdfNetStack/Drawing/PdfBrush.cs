namespace PdfNetStack.Drawing;

/// <summary>How shapes are filled: a solid color or a gradient.</summary>
public abstract class PdfBrush
{
    private protected PdfBrush()
    {
    }

    /// <summary>A solid brush.</summary>
    public static implicit operator PdfBrush(PdfColor color) => new PdfSolidBrush(color);
}

/// <summary>Fills with one color.</summary>
/// <param name="color">The fill color; its alpha sets the fill opacity.</param>
public sealed class PdfSolidBrush(PdfColor color) : PdfBrush
{
    /// <summary>The fill color.</summary>
    public PdfColor Color { get; } = color;
}

/// <summary>One color stop of a gradient.</summary>
/// <param name="Offset">Position along the gradient, 0 to 1.</param>
/// <param name="Color">Color at that position.</param>
public readonly record struct PdfGradientStop(double Offset, PdfColor Color);

/// <summary>Base of linear and radial gradients.</summary>
public abstract class PdfGradientBrush : PdfBrush
{
    private protected PdfGradientBrush(IEnumerable<PdfGradientStop> stops, bool extend)
    {
        Compat.ThrowIfNull(stops, nameof(stops));
        PdfGradientStop[] list = stops.ToArray();
        if (list.Length < 2) throw new ArgumentException("A gradient needs at least two stops.", nameof(stops));
        double previous = 0;
        foreach (PdfGradientStop s in list)
        {
            if (double.IsNaN(s.Offset) || s.Offset < 0 || s.Offset > 1) throw new ArgumentException("Stop offsets range from 0 to 1.", nameof(stops));
            if (s.Offset < previous) throw new ArgumentException("Stop offsets must not decrease.", nameof(stops));
            previous = s.Offset;
        }

        double alpha = list[0].Color.Alpha;
        if (list.Any(s => s.Color.Alpha != alpha))
        {
            throw new NotSupportedException("Gradient stops with different opacities are not supported yet; use one opacity for all stops.");
        }

        Stops = list;
        Extend = extend;
    }

    /// <summary>The color stops, in order.</summary>
    public IReadOnlyList<PdfGradientStop> Stops { get; }

    /// <summary>When true, the end colors continue beyond the gradient's ends. Default true.</summary>
    public bool Extend { get; }

    /// <summary>The common opacity of all stops.</summary>
    public double Alpha => Stops[0].Color.Alpha;
}

/// <summary>A gradient along a line between two points, in canvas coordinates.</summary>
public sealed class PdfLinearGradientBrush : PdfGradientBrush
{
    /// <summary>Creates a linear gradient from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public PdfLinearGradientBrush(PdfPoint start, PdfPoint end, IEnumerable<PdfGradientStop> stops, bool extend = true)
        : base(stops, extend)
    {
        Start = start;
        End = end;
    }

    /// <summary>Creates a two color linear gradient.</summary>
    public PdfLinearGradientBrush(PdfPoint start, PdfPoint end, PdfColor startColor, PdfColor endColor)
        : this(start, end, [new PdfGradientStop(0, startColor), new PdfGradientStop(1, endColor)])
    {
    }

    /// <summary>Where offset 0 lies.</summary>
    public PdfPoint Start { get; }

    /// <summary>Where offset 1 lies.</summary>
    public PdfPoint End { get; }
}

/// <summary>
/// A gradient between two circles, in canvas coordinates. With the same center and a start radius
/// of 0 it is the usual circular gradient.
/// </summary>
public sealed class PdfRadialGradientBrush : PdfGradientBrush
{
    /// <summary>Creates a radial gradient between two circles.</summary>
    public PdfRadialGradientBrush(PdfPoint startCenter, double startRadius, PdfPoint endCenter, double endRadius, IEnumerable<PdfGradientStop> stops, bool extend = true)
        : base(stops, extend)
    {
        if (startRadius < 0 || endRadius < 0) throw new ArgumentOutOfRangeException(nameof(startRadius), "Radii must not be negative.");
        StartCenter = startCenter;
        StartRadius = startRadius;
        EndCenter = endCenter;
        EndRadius = endRadius;
    }

    /// <summary>Creates a circular gradient from <paramref name="center"/> out to <paramref name="radius"/>.</summary>
    public PdfRadialGradientBrush(PdfPoint center, double radius, PdfColor centerColor, PdfColor edgeColor)
        : this(center, 0, center, radius, [new PdfGradientStop(0, centerColor), new PdfGradientStop(1, edgeColor)])
    {
    }

    /// <summary>Center of the circle at offset 0.</summary>
    public PdfPoint StartCenter { get; }

    /// <summary>Radius of the circle at offset 0.</summary>
    public double StartRadius { get; }

    /// <summary>Center of the circle at offset 1.</summary>
    public PdfPoint EndCenter { get; }

    /// <summary>Radius of the circle at offset 1.</summary>
    public double EndRadius { get; }
}
