using PdfNetStack.Drawing;

namespace PdfNetStack.Ocr.Processing;

/// <summary>Plane geometry for text boxes: convex hulls and minimum area rectangles.</summary>
internal static class Geometry
{
    /// <summary>The convex hull, counterclockwise in y up terms, by Andrew's monotone chain.</summary>
    public static List<PdfPoint> ConvexHull(List<PdfPoint> points)
    {
        if (points.Count < 3) return [.. points];
        List<PdfPoint> sorted = [.. points.OrderBy(p => p.X).ThenBy(p => p.Y)];
        var hull = new List<PdfPoint>(sorted.Count + 1);
        foreach (PdfPoint p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        int lower = hull.Count + 1;
        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            PdfPoint p = sorted[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static double Cross(PdfPoint o, PdfPoint a, PdfPoint b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    /// <summary>
    /// The smallest rectangle around the points: one of its sides lies along an edge of their
    /// convex hull (Freeman and Shapira, 1975), so trying each edge finds it.
    /// </summary>
    /// <returns>Corners in order around the rectangle, and its side lengths along and across the chosen edge.</returns>
    public static (PdfPoint[] Corners, double Width, double Height) MinAreaRectangle(List<PdfPoint> points)
    {
        List<PdfPoint> hull = ConvexHull(points);
        if (hull.Count == 0) return ([new(0, 0), new(0, 0), new(0, 0), new(0, 0)], 0, 0);
        if (hull.Count == 1) return ([hull[0], hull[0], hull[0], hull[0]], 0, 0);

        double best = double.MaxValue;
        PdfPoint[] corners = [];
        double width = 0, height = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            PdfPoint a = hull[i], b = hull[(i + 1) % hull.Count];
            double length = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
            if (length < 1e-12) continue;
            double ux = (b.X - a.X) / length, uy = (b.Y - a.Y) / length;
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (PdfPoint p in hull)
            {
                double u = (p.X * ux) + (p.Y * uy), v = (-p.X * uy) + (p.Y * ux);
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }

            double area = (maxU - minU) * (maxV - minV);
            if (area >= best) continue;
            best = area;
            width = maxU - minU;
            height = maxV - minV;
            PdfPoint At(double u, double v) => new((u * ux) - (v * uy), (u * uy) + (v * ux));
            corners = [At(minU, minV), At(maxU, minV), At(maxU, maxV), At(minU, maxV)];
        }

        return (corners, width, height);
    }

    /// <summary>
    /// Corners in reading order on a y down image: top left, top right, bottom right, bottom left.
    /// The two leftmost are the left side, each side's upper one its top.
    /// </summary>
    public static PdfPoint[] Ordered(PdfPoint[] corners)
    {
        PdfPoint[] byX = [.. corners.OrderBy(p => p.X).ThenBy(p => p.Y)];
        (PdfPoint topLeft, PdfPoint bottomLeft) = byX[0].Y <= byX[1].Y ? (byX[0], byX[1]) : (byX[1], byX[0]);
        (PdfPoint topRight, PdfPoint bottomRight) = byX[2].Y <= byX[3].Y ? (byX[2], byX[3]) : (byX[3], byX[2]);
        return [topLeft, topRight, bottomRight, bottomLeft];
    }

    /// <summary>True when the point is inside the convex quadrilateral (either winding).</summary>
    public static bool Inside(PdfPoint[] quad, double x, double y)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            PdfPoint a = quad[i], b = quad[(i + 1) % 4];
            double cross = ((b.X - a.X) * (y - a.Y)) - ((b.Y - a.Y) * (x - a.X));
            if (cross == 0) continue;
            int s = cross > 0 ? 1 : -1;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }

        return true;
    }

    public static double Distance(PdfPoint a, PdfPoint b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>A point of the quad's parallelogram: u along top left to top right, v down to bottom left, both 0 to 1.</summary>
    public static PdfPoint Along(PdfPoint[] quad, double u, double v) => new(
        quad[0].X + (u * (quad[1].X - quad[0].X)) + (v * (quad[3].X - quad[0].X)),
        quad[0].Y + (u * (quad[1].Y - quad[0].Y)) + (v * (quad[3].Y - quad[0].Y)));
}
