using PdfNetStack.Drawing;

namespace PdfNetStack.Ocr.Processing;

/// <summary>
/// Puts a scan stored as several images back together: strips or tiles side by side, a text mask
/// over a background. Each piece is painted into one gray canvas in page space, in drawing order,
/// so the engine reads the page as it is shown.
/// </summary>
internal static class Mosaic
{
    /// <summary>
    /// True when the image lands as an upright or quarter turned rectangle (mirrored or not), so its
    /// bounds are exactly what it covers. Corners may stray a thousandth of a side.
    /// </summary>
    public static bool IsAxisAligned(PdfMatrix m)
    {
        if (!m.IsInvertible || !IsFinite(m.A) || !IsFinite(m.B) || !IsFinite(m.C) || !IsFinite(m.D) || !IsFinite(m.E) || !IsFinite(m.F)) return false;
        double ax = Math.Abs(m.A), bx = Math.Abs(m.B), cy = Math.Abs(m.C), dy = Math.Abs(m.D);
        const double Slack = 1e-3;
        return (bx <= Slack * ax && cy <= Slack * dy) || (ax <= Slack * bx && dy <= Slack * cy);
    }

    /// <summary>
    /// The area the rectangles cover together, overlaps counted once. A sweep from left to right
    /// keeps the vertical spans of the rectangles it is inside, sorted, and adds the length they
    /// cover times the distance to the next edge: quadratic in the count at worst, no sorting per step.
    /// </summary>
    public static double UnionArea(IReadOnlyList<PdfRectangle> rectangles)
    {
        var edges = new List<(double X, bool Opens, double Bottom, double Top)>();
        foreach (PdfRectangle r in rectangles)
        {
            if (!(r.Right > r.Left && r.Top > r.Bottom)) continue;
            edges.Add((r.Left, true, r.Bottom, r.Top));
            edges.Add((r.Right, false, r.Bottom, r.Top));
        }

        edges.Sort((a, b) => a.X.CompareTo(b.X));
        var open = new List<(double Bottom, double Top)>();
        double area = 0, x = 0;
        foreach ((double edge, bool opens, double bottom, double top) in edges)
        {
            if (open.Count > 0) area += Covered(open) * (edge - x);
            x = edge;
            int at = open.BinarySearch((bottom, top));
            if (opens) open.Insert(at < 0 ? ~at : at, (bottom, top));
            else if (at >= 0) open.RemoveAt(at);
        }

        return area;
    }

    /// <summary>The length spans sorted by their bottom cover together.</summary>
    private static double Covered(List<(double Bottom, double Top)> spans)
    {
        double covered = 0, reached = double.NegativeInfinity;
        foreach ((double bottom, double top) in spans)
        {
            if (top <= reached) continue;
            covered += top - Math.Max(bottom, reached);
            reached = top;
        }

        return covered;
    }

    /// <summary>The pixels one piece covers on the canvas, before it is decoded: the bound on the work painting it takes.</summary>
    public static long Footprint(PdfRectangle bounds, PdfRectangle area, int width, int height)
    {
        (int x0, int x1, int y0, int y1) = Span(bounds, area, width / area.Width, height / area.Height, width, height);
        return (long)(x1 - x0) * (y1 - y0);
    }

    /// <summary>
    /// Paints one piece into the canvas. Canvas pixel (x, y) is page point
    /// (left + (x + 0.5) / scaleX, top - (y + 0.5) / scaleY); a pixel whose center falls in the
    /// piece takes the piece's pixel under it, so pieces that meet edge to edge leave no seam. A
    /// piece finer than the canvas is averaged down to it first. A stencil mask only darkens.
    /// </summary>
    /// <param name="canvas">Gray pixels, rows top to bottom.</param>
    /// <param name="width">Canvas width in pixels.</param>
    /// <param name="height">Canvas height in pixels.</param>
    /// <param name="area">The page rectangle the canvas covers.</param>
    /// <param name="piece">The piece's own pixels.</param>
    /// <param name="transform">The piece's unit square to the page (axis aligned).</param>
    /// <param name="mask">A stencil mask: black where it paints, white where it leaves the page alone.</param>
    public static void Paint(byte[] canvas, int width, int height, PdfRectangle area, OcrBitmap piece, PdfMatrix transform, bool mask)
    {
        double scaleX = width / area.Width, scaleY = height / area.Height;

        // The piece's size on the canvas, along its own axes, which a quarter turn swaps.
        bool turned = Math.Abs(transform.A) < Math.Abs(transform.B);
        double across = turned ? Math.Abs(transform.B) * scaleY : Math.Abs(transform.A) * scaleX;
        double down = turned ? Math.Abs(transform.C) * scaleX : Math.Abs(transform.D) * scaleY;
        int pw = piece.Width, ph = piece.Height;
        byte[] pixels = piece.Pixels;
        int tw = Math.Max(1, Math.Min(pw, (int)Math.Round(across))), th = Math.Max(1, Math.Min(ph, (int)Math.Round(down)));
        if (tw < pw || th < ph)
        {
            pixels = ImageOps.Shrink(pixels, pw, ph, tw, th);
            (pw, ph) = (tw, th);
        }

        PdfMatrix inverse = transform.Invert();
        PdfRectangle bounds = Bounds(transform);
        (int x0, int x1, int y0, int y1) = Span(bounds, area, scaleX, scaleY, width, height);
        for (int y = y0; y < y1; y++)
        {
            double pageY = area.Top - ((y + 0.5) / scaleY);
            for (int x = x0; x < x1; x++)
            {
                // Image space is the unit square, its first row at the top (ISO 32000-1, 8.9.4).
                PdfPoint unit = inverse.Transform(new PdfPoint(area.Left + ((x + 0.5) / scaleX), pageY));
                if (!(unit.X >= 0 && unit.X < 1 && unit.Y > 0 && unit.Y <= 1)) continue;
                int u = Math.Min(pw - 1, (int)(unit.X * pw)), v = Math.Min(ph - 1, (int)((1 - unit.Y) * ph));
                byte value = pixels[((long)v * pw) + u];
                long at = ((long)y * width) + x;
                canvas[at] = mask ? Math.Min(canvas[at], value) : value;
            }
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>The page rectangle a unit square lands on.</summary>
    public static PdfRectangle Bounds(PdfMatrix m)
    {
        PdfPoint[] corners = [m.Transform(new(0, 0)), m.Transform(new(1, 0)), m.Transform(new(0, 1)), m.Transform(new(1, 1))];
        return new PdfRectangle(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
    }

    /// <summary>The canvas columns and rows a page rectangle touches, clamped to the canvas.</summary>
    private static (int X0, int X1, int Y0, int Y1) Span(PdfRectangle bounds, PdfRectangle area, double scaleX, double scaleY, int width, int height)
    {
        static int Clamp(double value, int max) => (int)Math.Max(0, Math.Min(max, value));
        return (
            Clamp(Math.Floor((bounds.Left - area.Left) * scaleX), width),
            Clamp(Math.Ceiling((bounds.Right - area.Left) * scaleX), width),
            Clamp(Math.Floor((area.Top - bounds.Top) * scaleY), height),
            Clamp(Math.Ceiling((area.Top - bounds.Bottom) * scaleY), height));
    }
}
