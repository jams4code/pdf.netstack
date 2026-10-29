using PdfNetStack.Drawing;

namespace PdfNetStack.Ocr.Processing;

/// <summary>
/// Turns a DB text detector's probability map into text boxes (Liao et al., "Real-time Scene Text
/// Detection with Differentiable Binarization", 2020): threshold, connected regions, the smallest
/// rectangle around each, kept when the mean probability inside is high, then grown back by the
/// margin the model was trained to shrink text by.
/// </summary>
internal static class TextBoxes
{
    /// <summary>Regions considered per image; a page with more is noise, not text.</summary>
    public const int MaxCandidates = 3000;

    private const int MinSide = 3;

    /// <param name="probability">The map, row major, width by height.</param>
    /// <param name="threshold">Pixel probability that counts as text.</param>
    /// <param name="boxThreshold">Mean probability a box needs.</param>
    /// <param name="unclipRatio">How far boxes grow: area times this over perimeter.</param>
    /// <param name="scaleX">Image pixels per map pixel, across.</param>
    /// <param name="scaleY">Image pixels per map pixel, down.</param>
    /// <param name="imageWidth">Image width, to clamp boxes.</param>
    /// <param name="imageHeight">Image height, to clamp boxes.</param>
    /// <returns>Boxes in image pixels, corners top left, top right, bottom right, bottom left, in reading order.</returns>
    public static List<PdfPoint[]> Find(float[] probability, int width, int height, double threshold, double boxThreshold, double unclipRatio, double scaleX, double scaleY, int imageWidth, int imageHeight)
    {
        var label = new int[probability.Length];
        var boxes = new List<PdfPoint[]>();
        var stack = new Stack<int>();
        int regions = 0;
        for (int start = 0; start < probability.Length && regions < MaxCandidates; start++)
        {
            if (label[start] != 0 || probability[start] <= threshold) continue;
            regions++;

            // Flood the region (8 connected), keeping its edge pixels: their hull is the region's.
            var edge = new List<PdfPoint>();
            label[start] = regions;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % width, y = i / width;
                bool isEdge = false;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            isEdge = true;
                            continue;
                        }

                        int n = (ny * width) + nx;
                        if (probability[n] <= threshold)
                        {
                            isEdge = true;
                            continue;
                        }

                        if (label[n] != 0) continue;
                        label[n] = regions;
                        stack.Push(n);
                    }
                }

                if (isEdge) edge.Add(new PdfPoint(x, y));
            }

            (PdfPoint[] corners, double w, double h) = Geometry.MinAreaRectangle(edge);
            if (Math.Min(w, h) < MinSide) continue;
            if (Score(probability, width, height, corners) < boxThreshold) continue;

            // Grow by the shrink the training applied (DB, section 3.3): area times ratio over perimeter.
            double margin = w * h * unclipRatio / (2 * (w + h));
            PdfPoint[] grown = Grow(corners, margin);
            if (Math.Min(w, h) + (2 * margin) < MinSide + 2) continue;

            PdfPoint[] scaled = [.. grown.Select(p => new PdfPoint(
                Math.Max(0, Math.Min(imageWidth, p.X * scaleX)),
                Math.Max(0, Math.Min(imageHeight, p.Y * scaleY))))];
            boxes.Add(Geometry.Ordered(scaled));
        }

        return ReadingOrder(boxes);
    }

    /// <summary>Mean probability inside the box, over the pixels it covers.</summary>
    private static double Score(float[] probability, int width, int height, PdfPoint[] quad)
    {
        int x0 = Math.Max(0, (int)Math.Floor(quad.Min(p => p.X))), x1 = Math.Min(width - 1, (int)Math.Ceiling(quad.Max(p => p.X)));
        int y0 = Math.Max(0, (int)Math.Floor(quad.Min(p => p.Y))), y1 = Math.Min(height - 1, (int)Math.Ceiling(quad.Max(p => p.Y)));
        double sum = 0;
        int count = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (!Geometry.Inside(quad, x, y)) continue;
                sum += probability[(y * width) + x];
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>Moves each side of a rectangle (corners in order around it) outward by <paramref name="margin"/>.</summary>
    private static PdfPoint[] Grow(PdfPoint[] r, double margin)
    {
        double l1 = Geometry.Distance(r[0], r[1]), l2 = Geometry.Distance(r[0], r[3]);
        double e1x = (r[1].X - r[0].X) / l1, e1y = (r[1].Y - r[0].Y) / l1;
        double e2x = (r[3].X - r[0].X) / l2, e2y = (r[3].Y - r[0].Y) / l2;
        PdfPoint Move(PdfPoint p, int s1, int s2) => new(p.X + (margin * ((s1 * e1x) + (s2 * e2x))), p.Y + (margin * ((s1 * e1y) + (s2 * e2y))));
        return [Move(r[0], -1, -1), Move(r[1], 1, -1), Move(r[2], 1, 1), Move(r[3], -1, 1)];
    }

    /// <summary>Top to bottom, then left to right among boxes whose tops are within half a line of each other.</summary>
    public static List<PdfPoint[]> ReadingOrder(List<PdfPoint[]> boxes)
    {
        List<PdfPoint[]> sorted = [.. boxes.OrderBy(b => b[0].Y).ThenBy(b => b[0].X)];
        for (int i = 1; i < sorted.Count; i++)
        {
            for (int j = i; j > 0; j--)
            {
                PdfPoint[] above = sorted[j - 1], here = sorted[j];
                double line = Math.Min(Geometry.Distance(above[0], above[3]), Geometry.Distance(here[0], here[3]));
                if (Math.Abs(here[0].Y - above[0].Y) < line / 2 && here[0].X < above[0].X) (sorted[j - 1], sorted[j]) = (here, above);
                else break;
            }
        }

        return sorted;
    }
}
