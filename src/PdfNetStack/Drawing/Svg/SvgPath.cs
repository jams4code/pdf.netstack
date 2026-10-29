
namespace PdfNetStack.Drawing.Svg;

/// <summary>
/// SVG path data (SVG 1.1, 8.3): M, L, H, V, C, S, Q, T, A and Z, absolute and relative, with
/// implicit repetition. Arcs become cubic Bezier curves. Parsing stops at the first error, keeping
/// what came before, as the specification asks.
/// </summary>
internal static class SvgPath
{
    private const int MaxCommands = 500_000;

    public static PdfPath? Parse(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return null;
        var path = new PdfPath();
        var reader = new Reader(d!);
        double x = 0, y = 0, startX = 0, startY = 0;
        double lastControlX = 0, lastControlY = 0;
        char command = ' ', previous = ' ';
        int commands = 0;
        bool started = false;
        while (commands++ < MaxCommands)
        {
            reader.SkipSeparators();
            if (reader.AtEnd) break;
            if (char.IsLetter(reader.Peek)) command = reader.Next();
            else if (command is ' ' or 'Z' or 'z') break; // numbers after Z are an error

            // After M, further pairs are L; after m, l.
            else if (command == 'M') command = 'L';
            else if (command == 'm') command = 'l';

            bool relative = char.IsLower(command);
            double ox = relative ? x : 0, oy = relative ? y : 0;
            char upper = char.ToUpperInvariant(command);
            if (upper != 'M' && upper != 'Z' && !started)
            {
                // A path must start with a move.
                break;
            }

            switch (upper)
            {
                case 'Z':
                    path.Close();
                    (x, y) = (startX, startY);
                    break;
                case 'M':
                    if (!reader.Pair(out double mx, out double my)) return Done(path, started);
                    (x, y) = (ox + mx, oy + my);
                    (startX, startY) = (x, y);
                    path.MoveTo(x, y);
                    started = true;
                    break;
                case 'L':
                    if (!reader.Pair(out double lx, out double ly)) return Done(path, started);
                    (x, y) = (ox + lx, oy + ly);
                    path.LineTo(x, y);
                    break;
                case 'H':
                    if (!reader.Number(out double hx)) return Done(path, started);
                    x = ox + hx;
                    path.LineTo(x, y);
                    break;
                case 'V':
                    if (!reader.Number(out double vy)) return Done(path, started);
                    y = oy + vy;
                    path.LineTo(x, y);
                    break;
                case 'C':
                    if (!reader.Pair(out double x1, out double y1) || !reader.Pair(out double x2, out double y2) || !reader.Pair(out double cx, out double cy)) return Done(path, started);
                    path.CurveTo(ox + x1, oy + y1, ox + x2, oy + y2, ox + cx, oy + cy);
                    (lastControlX, lastControlY) = (ox + x2, oy + y2);
                    (x, y) = (ox + cx, oy + cy);
                    break;
                case 'S':
                    if (!reader.Pair(out double sx2, out double sy2) || !reader.Pair(out double sx, out double sy)) return Done(path, started);
                    bool smooth = char.ToUpperInvariant(previous) is 'C' or 'S';
                    double rx1 = smooth ? (2 * x) - lastControlX : x, ry1 = smooth ? (2 * y) - lastControlY : y;
                    path.CurveTo(rx1, ry1, ox + sx2, oy + sy2, ox + sx, oy + sy);
                    (lastControlX, lastControlY) = (ox + sx2, oy + sy2);
                    (x, y) = (ox + sx, oy + sy);
                    break;
                case 'Q':
                    if (!reader.Pair(out double qx1, out double qy1) || !reader.Pair(out double qx, out double qy)) return Done(path, started);
                    Quadratic(path, x, y, ox + qx1, oy + qy1, ox + qx, oy + qy);
                    (lastControlX, lastControlY) = (ox + qx1, oy + qy1);
                    (x, y) = (ox + qx, oy + qy);
                    break;
                case 'T':
                    if (!reader.Pair(out double tx, out double ty)) return Done(path, started);
                    bool quad = char.ToUpperInvariant(previous) is 'Q' or 'T';
                    double tcx = quad ? (2 * x) - lastControlX : x, tcy = quad ? (2 * y) - lastControlY : y;
                    Quadratic(path, x, y, tcx, tcy, ox + tx, oy + ty);
                    (lastControlX, lastControlY) = (tcx, tcy);
                    (x, y) = (ox + tx, oy + ty);
                    break;
                case 'A':
                    if (!reader.Number(out double arx) || !reader.Number(out double ary) || !reader.Number(out double rotation)
                        || !reader.Flag(out bool large) || !reader.Flag(out bool sweep) || !reader.Pair(out double ax, out double ay))
                    {
                        return Done(path, started);
                    }

                    Arc(path, x, y, arx, ary, rotation, large, sweep, ox + ax, oy + ay);
                    (x, y) = (ox + ax, oy + ay);
                    break;
                default:
                    return Done(path, started);
            }

            previous = command;
        }

        return Done(path, started);
    }

    private static PdfPath? Done(PdfPath path, bool started) => started ? path : null;

    private static void Quadratic(PdfPath path, double x0, double y0, double qx, double qy, double x, double y) =>
        path.CurveTo(x0 + (2.0 / 3 * (qx - x0)), y0 + (2.0 / 3 * (qy - y0)), x + (2.0 / 3 * (qx - x)), y + (2.0 / 3 * (qy - y)), x, y);

    /// <summary>An elliptical arc from endpoint form (SVG 1.1, F.6.5), as Bezier curves of at most 90 degrees.</summary>
    private static void Arc(PdfPath path, double x1, double y1, double rx, double ry, double angle, bool large, bool sweep, double x2, double y2)
    {
        if (x1 == x2 && y1 == y2) return;
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            path.LineTo(x2, y2);
            return;
        }

        double phi = angle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
        double x1p = (cos * dx) + (sin * dy), y1p = (-sin * dx) + (cos * dy);

        // Radii too small for the endpoints are scaled up just enough.
        double lambda = (x1p * x1p / (rx * rx)) + (y1p * y1p / (ry * ry));
        if (lambda > 1)
        {
            double s = Math.Sqrt(lambda);
            rx *= s;
            ry *= s;
        }

        double num = (rx * rx * ry * ry) - (rx * rx * y1p * y1p) - (ry * ry * x1p * x1p);
        double den = (rx * rx * y1p * y1p) + (ry * ry * x1p * x1p);
        double coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, num / den));
        double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;
        double cx = (cos * cxp) - (sin * cyp) + ((x1 + x2) / 2), cy = (sin * cxp) + (cos * cyp) + ((y1 + y2) / 2);

        static double Angle(double ux, double uy, double vx, double vy)
        {
            double a = Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
            return a;
        }

        double theta = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        double step = delta / segments;
        double t = 4.0 / 3 * Math.Tan(step / 4);
        for (int k = 0; k < segments; k++)
        {
            double a1 = theta + (k * step), a2 = a1 + step;
            double c1 = Math.Cos(a1), s1 = Math.Sin(a1), c2 = Math.Cos(a2), s2 = Math.Sin(a2);
            (double X, double Y) P(double px, double py) => ((cos * rx * px) - (sin * ry * py) + cx, (sin * rx * px) + (cos * ry * py) + cy);
            var cp1 = P(c1 - (t * s1), s1 + (t * c1));
            var cp2 = P(c2 + (t * s2), s2 - (t * c2));
            var end = P(c2, s2);
            path.CurveTo(cp1.X, cp1.Y, cp2.X, cp2.Y, end.X, end.Y);
        }
    }

    /// <summary>The box around every point and control point of a path.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(PdfPath path) => path.ControlBounds();

    public static PdfPath Transformed(PdfPath path, PdfMatrix m) => path.Transformed(m);

    private sealed class Reader(string s)
    {
        private int _i;

        public bool AtEnd => _i >= s.Length;

        public char Peek => s[_i];

        public char Next() => s[_i++];

        public void SkipSeparators()
        {
            while (_i < s.Length && (char.IsWhiteSpace(s[_i]) || s[_i] == ',')) _i++;
        }

        public bool Number(out double value)
        {
            SkipSeparators();
            value = 0;
            int start = _i;
            if (_i < s.Length && s[_i] is '+' or '-') _i++;
            bool digits = false, dot = false;
            while (_i < s.Length && (char.IsDigit(s[_i]) || (s[_i] == '.' && !dot)))
            {
                if (s[_i] == '.') dot = true;
                else digits = true;
                _i++;
            }

            if (digits && _i < s.Length && s[_i] is 'e' or 'E')
            {
                int save = _i++;
                if (_i < s.Length && s[_i] is '+' or '-') _i++;
                if (_i < s.Length && char.IsDigit(s[_i])) while (_i < s.Length && char.IsDigit(s[_i])) _i++;
                else _i = save;
            }

            if (!digits)
            {
                _i = start;
                return false;
            }

            return Compat.TryParseDouble(s, start, _i - start, out value) && !double.IsInfinity(value) && !double.IsNaN(value);
        }

        public bool Pair(out double x, out double y)
        {
            y = 0;
            return Number(out x) && Number(out y);
        }

        // Arc flags are single digits and may run into the next number: "a1 1 0 00 10 10".
        public bool Flag(out bool value)
        {
            SkipSeparators();
            value = false;
            if (_i >= s.Length || s[_i] is not ('0' or '1')) return false;
            value = s[_i++] == '1';
            return true;
        }
    }
}
