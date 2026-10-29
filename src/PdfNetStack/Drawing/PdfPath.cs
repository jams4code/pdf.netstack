namespace PdfNetStack.Drawing;

/// <summary>Which regions of a self-intersecting or nested path count as inside.</summary>
public enum PdfFillRule
{
    /// <summary>Nonzero winding number. Nested shapes drawn in the same direction stay filled.</summary>
    NonZero,

    /// <summary>Even-odd. Nested shapes alternate between filled and holes.</summary>
    EvenOdd,
}

/// <summary>Corner radii of a rounded rectangle, each with a horizontal and a vertical radius.</summary>
/// <param name="TopLeftX">Horizontal radius of the top left corner.</param>
/// <param name="TopLeftY">Vertical radius of the top left corner.</param>
/// <param name="TopRightX">Horizontal radius of the top right corner.</param>
/// <param name="TopRightY">Vertical radius of the top right corner.</param>
/// <param name="BottomRightX">Horizontal radius of the bottom right corner.</param>
/// <param name="BottomRightY">Vertical radius of the bottom right corner.</param>
/// <param name="BottomLeftX">Horizontal radius of the bottom left corner.</param>
/// <param name="BottomLeftY">Vertical radius of the bottom left corner.</param>
public readonly record struct PdfCornerRadii(
    double TopLeftX, double TopLeftY,
    double TopRightX, double TopRightY,
    double BottomRightX, double BottomRightY,
    double BottomLeftX, double BottomLeftY)
{
    /// <summary>The same circular radius on every corner.</summary>
    public static PdfCornerRadii Uniform(double radius) => new(radius, radius, radius, radius, radius, radius, radius, radius);

    /// <summary>Circular radii per corner, clockwise from the top left.</summary>
    public static PdfCornerRadii Of(double topLeft, double topRight, double bottomRight, double bottomLeft) =>
        new(topLeft, topLeft, topRight, topRight, bottomRight, bottomRight, bottomLeft, bottomLeft);

    internal bool IsZero => TopLeftX <= 0 && TopLeftY <= 0 && TopRightX <= 0 && TopRightY <= 0 &&
        BottomRightX <= 0 && BottomRightY <= 0 && BottomLeftX <= 0 && BottomLeftY <= 0;
}

/// <summary>
/// A shape made of straight and curved segments, in canvas coordinates (origin top left, y down).
/// Build it, then draw, fill or clip with it through <see cref="PdfGraphics"/>.
/// </summary>
public sealed class PdfPath
{
    // Bezier control distance for a quarter circle of radius 1.
    private const double Kappa = 0.5522847498307936;

    private readonly List<Segment> _segments = new();
    private PdfPoint? _current;
    private PdfPoint _subpathStart;

    private enum Kind
    {
        Move,
        Line,
        Cubic,
        Rectangle,
        Close,
    }

    /// <summary>Fill rule used when the path is filled or used as a clip. Default nonzero.</summary>
    public PdfFillRule FillRule { get; set; }

    /// <summary>True when nothing has been added.</summary>
    public bool IsEmpty => _segments.Count == 0;

    /// <summary>The end of the last segment, or null before the first.</summary>
    public PdfPoint? CurrentPoint => _current;

    /// <summary>Starts a new subpath at (x, y).</summary>
    public PdfPath MoveTo(double x, double y)
    {
        Finite(x, y);
        _segments.Add(new Segment(Kind.Move, x, y));
        _current = _subpathStart = new PdfPoint(x, y);
        return this;
    }

    /// <summary>A straight line to (x, y).</summary>
    public PdfPath LineTo(double x, double y)
    {
        Finite(x, y);
        RequireCurrent();
        _segments.Add(new Segment(Kind.Line, x, y));
        _current = new PdfPoint(x, y);
        return this;
    }

    /// <summary>A cubic Bezier curve to (x, y) with two control points.</summary>
    public PdfPath CurveTo(double x1, double y1, double x2, double y2, double x, double y)
    {
        Finite(x1, y1);
        Finite(x2, y2);
        Finite(x, y);
        RequireCurrent();
        _segments.Add(new Segment(Kind.Cubic, x1, y1, x2, y2, x, y));
        _current = new PdfPoint(x, y);
        return this;
    }

    /// <summary>A quadratic Bezier curve to (x, y) with one control point.</summary>
    public PdfPath QuadraticCurveTo(double cx, double cy, double x, double y)
    {
        PdfPoint p0 = RequireCurrent();
        return CurveTo(
            p0.X + 2.0 / 3 * (cx - p0.X), p0.Y + 2.0 / 3 * (cy - p0.Y),
            x + 2.0 / 3 * (cx - x), y + 2.0 / 3 * (cy - y),
            x, y);
    }

    /// <summary>Closes the current subpath with a straight line back to its start.</summary>
    public PdfPath Close()
    {
        if (_current is null) return this;
        _segments.Add(new Segment(Kind.Close));
        _current = _subpathStart;
        return this;
    }

    /// <summary>Adds a closed rectangle with its top left corner at (x, y).</summary>
    public PdfPath AddRectangle(double x, double y, double width, double height)
    {
        Finite(x, y);
        Finite(width, height);
        _segments.Add(new Segment(Kind.Rectangle, x, y, width, height));
        _current = _subpathStart = new PdfPoint(x, y);
        return this;
    }

    /// <summary>Adds a closed rectangle with the same circular radius on every corner.</summary>
    public PdfPath AddRoundedRectangle(double x, double y, double width, double height, double radius) =>
        AddRoundedRectangle(x, y, width, height, PdfCornerRadii.Uniform(radius));

    /// <summary>
    /// Adds a closed rectangle with elliptical corners. Radii that do not fit are scaled down
    /// together, as CSS border-radius specifies, so adjacent corners never overlap.
    /// </summary>
    public PdfPath AddRoundedRectangle(double x, double y, double width, double height, PdfCornerRadii radii)
    {
        Finite(x, y);
        Finite(width, height);
        if (width < 0)
        {
            x += width;
            width = -width;
        }

        if (height < 0)
        {
            y += height;
            height = -height;
        }

        if (radii.IsZero) return AddRectangle(x, y, width, height);

        double tlx = Math.Max(0, radii.TopLeftX), tly = Math.Max(0, radii.TopLeftY);
        double trx = Math.Max(0, radii.TopRightX), try_ = Math.Max(0, radii.TopRightY);
        double brx = Math.Max(0, radii.BottomRightX), bry = Math.Max(0, radii.BottomRightY);
        double blx = Math.Max(0, radii.BottomLeftX), bly = Math.Max(0, radii.BottomLeftY);

        // CSS Backgrounds 3, 5.5: one factor for all radii so every side has room for its two corners.
        double f = 1;
        f = Fit(f, width, tlx + trx);
        f = Fit(f, width, blx + brx);
        f = Fit(f, height, tly + bly);
        f = Fit(f, height, try_ + bry);
        tlx *= f; tly *= f; trx *= f; try_ *= f; brx *= f; bry *= f; blx *= f; bly *= f;

        double r = x + width, b = y + height;
        MoveTo(x + tlx, y);
        LineTo(r - trx, y);
        Corner(r - trx, y + try_, trx, try_, 270);
        LineTo(r, b - bry);
        Corner(r - brx, b - bry, brx, bry, 0);
        LineTo(x + blx, b);
        Corner(x + blx, b - bly, blx, bly, 90);
        LineTo(x, y + tly);
        Corner(x + tlx, y + tly, tlx, tly, 180);
        return Close();

        static double Fit(double factor, double side, double sum) => sum > side && sum > 0 ? Math.Min(factor, side / sum) : factor;
    }

    /// <summary>Adds a closed ellipse inscribed in the given rectangle.</summary>
    public PdfPath AddEllipse(double x, double y, double width, double height)
    {
        Finite(x, y);
        Finite(width, height);
        double rx = width / 2, ry = height / 2, cx = x + rx, cy = y + ry;
        double kx = rx * Kappa, ky = ry * Kappa;
        MoveTo(cx + rx, cy);
        CurveTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
        CurveTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
        CurveTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
        CurveTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
        return Close();
    }

    /// <summary>Adds a closed circle.</summary>
    public PdfPath AddCircle(double centerX, double centerY, double radius) =>
        AddEllipse(centerX - radius, centerY - radius, 2 * radius, 2 * radius);

    /// <summary>
    /// Adds an elliptical arc. Angles are in degrees from the positive x axis, clockwise on the
    /// canvas. When the path has a current point, a line joins it to the arc's start.
    /// </summary>
    public PdfPath AddArc(double centerX, double centerY, double radiusX, double radiusY, double startAngle, double sweepAngle)
    {
        Finite(centerX, centerY);
        Finite(radiusX, radiusY);
        Finite(startAngle, sweepAngle);
        double start = startAngle * Math.PI / 180;
        var first = new PdfPoint(centerX + radiusX * Math.Cos(start), centerY + radiusY * Math.Sin(start));
        if (_current is null) MoveTo(first.X, first.Y);
        else LineTo(first.X, first.Y);

        // Quarter turns or less per Bezier keep the error under 0.03% of the radius.
        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepAngle) / 90 - 1e-9));
        double step = sweepAngle * Math.PI / 180 / pieces;
        double k = 4.0 / 3 * Math.Tan(step / 4);
        double a = start;
        for (int i = 0; i < pieces; i++)
        {
            double b = a + step;
            double cosA = Math.Cos(a), sinA = Math.Sin(a), cosB = Math.Cos(b), sinB = Math.Sin(b);
            CurveTo(
                centerX + radiusX * (cosA - k * sinA), centerY + radiusY * (sinA + k * cosA),
                centerX + radiusX * (cosB + k * sinB), centerY + radiusY * (sinB - k * cosB),
                centerX + radiusX * cosB, centerY + radiusY * sinB);
            a = b;
        }

        return this;
    }

    /// <summary>Adds a polygon through the given points, closed unless <paramref name="close"/> is false.</summary>
    public PdfPath AddPolygon(IEnumerable<PdfPoint> points, bool close = true)
    {
        Compat.ThrowIfNull(points, nameof(points));
        bool first = true;
        foreach (PdfPoint p in points)
        {
            if (first) MoveTo(p.X, p.Y);
            else LineTo(p.X, p.Y);
            first = false;
        }

        return close && !first ? Close() : this;
    }

    /// <summary>The box around every point and control point: a cheap, slightly loose bound. Zeros for an empty path.</summary>
    internal (double MinX, double MinY, double MaxX, double MaxY) ControlBounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (Segment s in _segments)
        {
            switch (s.Kind)
            {
                case Kind.Move or Kind.Line:
                    Add(s.A, s.B);
                    break;
                case Kind.Cubic:
                    Add(s.A, s.B);
                    Add(s.C, s.D);
                    Add(s.E, s.F);
                    break;
                case Kind.Rectangle:
                    Add(s.A, s.B);
                    Add(s.A + s.C, s.B + s.D);
                    break;
            }
        }

        return minX > maxX ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
    }

    /// <summary>A copy with every point mapped through <paramref name="m"/>; rectangles become four lines.</summary>
    internal PdfPath Transformed(PdfMatrix m)
    {
        var result = new PdfPath { FillRule = FillRule };
        foreach (Segment s in _segments)
        {
            PdfPoint a = m.Transform(new PdfPoint(s.A, s.B));
            switch (s.Kind)
            {
                case Kind.Move:
                    result.MoveTo(a.X, a.Y);
                    break;
                case Kind.Line:
                    result.LineTo(a.X, a.Y);
                    break;
                case Kind.Cubic:
                    PdfPoint c = m.Transform(new PdfPoint(s.C, s.D)), e = m.Transform(new PdfPoint(s.E, s.F));
                    result.CurveTo(a.X, a.Y, c.X, c.Y, e.X, e.Y);
                    break;
                case Kind.Rectangle:
                    result.AddPolygon([a, m.Transform(new PdfPoint(s.A + s.C, s.B)), m.Transform(new PdfPoint(s.A + s.C, s.B + s.D)), m.Transform(new PdfPoint(s.A, s.B + s.D))]);
                    break;
                default:
                    result.Close();
                    break;
            }
        }

        return result;
    }

    /// <summary>Adds the subpaths of another path, as for a clip made of several shapes.</summary>
    internal void Append(PdfPath other)
    {
        _segments.AddRange(other._segments);
        _current = other._current;
        _subpathStart = other._subpathStart;
    }

    internal void Write(ContentWriter w)
    {
        foreach (Segment s in _segments)
        {
            switch (s.Kind)
            {
                case Kind.Move:
                    w.Numbers(s.A, s.B).Op("m");
                    break;
                case Kind.Line:
                    w.Numbers(s.A, s.B).Op("l");
                    break;
                case Kind.Cubic:
                    w.Numbers(s.A, s.B, s.C, s.D).Numbers(s.E, s.F).Op("c");
                    break;
                case Kind.Rectangle:
                    w.Numbers(s.A, s.B, s.C, s.D).Op("re");
                    break;
                default:
                    w.Op("h");
                    break;
            }
        }
    }

    // A quarter ellipse from angle 'from' (degrees, clockwise on the canvas) through 90 degrees.
    private void Corner(double cx, double cy, double rx, double ry, double from)
    {
        if (rx <= 0 || ry <= 0) return;
        double a = from * Math.PI / 180, b = a + Math.PI / 2;
        double cosA = Math.Cos(a), sinA = Math.Sin(a), cosB = Math.Cos(b), sinB = Math.Sin(b);
        CurveTo(
            cx + rx * (cosA - Kappa * sinA), cy + ry * (sinA + Kappa * cosA),
            cx + rx * (cosB + Kappa * sinB), cy + ry * (sinB - Kappa * cosB),
            cx + rx * cosB, cy + ry * sinB);
    }

    private PdfPoint RequireCurrent() =>
        _current ?? throw new InvalidOperationException("The path has no current point; start with MoveTo.");

    private static void Finite(double a, double b)
    {
        if (double.IsNaN(a) || double.IsInfinity(a) || double.IsNaN(b) || double.IsInfinity(b))
        {
            throw new ArgumentOutOfRangeException(nameof(a), "Coordinates must be finite numbers.");
        }
    }

    private readonly record struct Segment(Kind Kind, double A = 0, double B = 0, double C = 0, double D = 0, double E = 0, double F = 0);
}
