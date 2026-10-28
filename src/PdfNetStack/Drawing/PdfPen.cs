namespace PdfNetStack.Drawing;

/// <summary>Shape at the open ends of stroked lines.</summary>
public enum PdfLineCap
{
    /// <summary>The stroke ends exactly at the end point.</summary>
    Butt = 0,

    /// <summary>A half circle around the end point.</summary>
    Round = 1,

    /// <summary>A half square beyond the end point.</summary>
    Square = 2,
}

/// <summary>Shape where two stroked segments meet.</summary>
public enum PdfLineJoin
{
    /// <summary>Sharp corner, limited by the miter limit.</summary>
    Miter = 0,

    /// <summary>Rounded corner.</summary>
    Round = 1,

    /// <summary>Cut-off corner.</summary>
    Bevel = 2,
}

/// <summary>How lines and outlines are stroked. Immutable; use <c>with</c> to derive variants.</summary>
public sealed record PdfPen
{
    /// <summary>Creates a solid pen.</summary>
    /// <param name="color">Stroke color.</param>
    /// <param name="width">Line width in points. 0 draws the thinnest line the device can show.</param>
    public PdfPen(PdfColor color, double width = 1)
    {
        Color = color;
        Width = width;
    }

    /// <summary>Stroke color.</summary>
    public PdfColor Color { get; init; }

    /// <summary>Line width in points.</summary>
    public double Width
    {
        get;
        init => field = value >= 0 && !double.IsInfinity(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>End shape. Default butt.</summary>
    public PdfLineCap LineCap { get; init; }

    /// <summary>Corner shape. Default miter.</summary>
    public PdfLineJoin LineJoin { get; init; }

    /// <summary>Miter limit, at least 1. Default 10, as in PDF.</summary>
    public double MiterLimit
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    } = 10;

    /// <summary>Alternating dash and gap lengths in points; empty for a solid line.</summary>
    public IReadOnlyList<double> DashPattern
    {
        get;
        init => field = value is null || value.Any(v => v < 0 || double.IsNaN(v)) || (value.Count > 0 && value.All(v => v == 0))
            ? throw new ArgumentException("Dash lengths must be non-negative and not all zero.", nameof(value))
            : value.ToArray();
    } = [];

    /// <summary>Offset into the dash pattern where the line starts.</summary>
    public double DashOffset { get; init; }

    /// <summary>A 1 point black pen.</summary>
    public static PdfPen Black { get; } = new(PdfColor.Black);
}
