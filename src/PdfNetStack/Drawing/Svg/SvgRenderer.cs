using System.Globalization;
using PdfNetStack.Fonts;

namespace PdfNetStack.Drawing.Svg;

/// <summary>
/// Draws an SVG tree onto a canvas in SVG user units (SVG 1.1 and SVG 2, static subset): shapes,
/// paths, groups, use and symbol, nested viewports, solid and gradient paint, clip paths, text
/// in the standard fonts and embedded raster images. Filters, masks, patterns and animation are
/// not drawn.
/// </summary>
internal sealed class SvgRenderer
{
    private const int MaxUseDepth = 16;
    private const int MaxDrawn = 200_000;

    // Work done whether or not anything paints: use fans out, so a small file can ask for 10^16
    // visits of empty groups. Every element entered and every clip shape counts.
    private const int MaxVisited = 1_000_000;
    private const double DefaultFontSize = 16;

    // Properties that pass from parent to child (SVG 1.1, Appendix N).
    private static readonly HashSet<string> Inherited = new(StringComparer.OrdinalIgnoreCase)
    {
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "color", "font-family", "font-size", "font-weight",
        "font-style", "text-anchor", "visibility", "clip-rule",
    };

    private static readonly HashSet<string> Properties = new(Inherited, StringComparer.OrdinalIgnoreCase)
    {
        "opacity", "display", "clip-path", "stop-color", "stop-opacity", "transform",
    };

    private readonly PdfGraphics _g;
    private readonly Dictionary<string, SvgElement> _ids = new(StringComparer.Ordinal);
    private readonly List<(string Selector, Dictionary<string, string> Declarations)> _rules = [];
    private int _drawn;
    private int _visited;

    private SvgRenderer(PdfGraphics graphics, SvgElement root)
    {
        _g = graphics;
        Index(root);
    }

    /// <summary>Draws <paramref name="root"/> scaled so its viewport fills width by height, in canvas units.</summary>
    public static void Render(PdfGraphics graphics, SvgElement root, double viewportWidth, double viewportHeight)
    {
        var renderer = new SvgRenderer(graphics, root);
        var style = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fill"] = "black",
            ["stroke"] = "none",
            ["stroke-width"] = "1",
            ["color"] = "black",
            ["font-size"] = "16",
            ["font-family"] = "serif",
        };
        renderer.Viewport(root, 0, 0, viewportWidth, viewportHeight, style, 0);
    }

    private void Index(SvgElement root)
    {
        var stack = new Stack<SvgElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            SvgElement e = stack.Pop();
            if (e["id"] is string id && !_ids.ContainsKey(id)) _ids[id] = e;
            if (e.Name == "style") ParseStyleSheet(e.Text.ToString());
            foreach (SvgElement c in e.Children) stack.Push(c);
        }
    }

    /// <summary>Rules of a style element with type, class and id selectors, the way design tools export them.</summary>
    private void ParseStyleSheet(string css)
    {
        int i = 0;
        while (i < css.Length && _rules.Count < 10_000)
        {
            int open = css.IndexOf('{', i);
            if (open < 0) break;
            int close = css.IndexOf('}', open);
            if (close < 0) break;
            string selectors = StripComments(css.Substring(i, open - i)).Trim();
            Dictionary<string, string> declarations = Declarations(css.Substring(open + 1, close - open - 1));
            foreach (string selector in selectors.Split(','))
            {
                string sel = selector.Trim();
                if (sel.Length > 0 && sel[0] != '@') _rules.Add((sel, declarations));
            }

            i = close + 1;
        }
    }

    private static string StripComments(string s)
    {
        int start;
        while ((start = s.IndexOf("/*", StringComparison.Ordinal)) >= 0)
        {
            int end = s.IndexOf("*/", start + 2, StringComparison.Ordinal);
            s = s.Remove(start, (end < 0 ? s.Length : end + 2) - start);
        }

        return s;
    }

    private static Dictionary<string, string> Declarations(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in StripComments(text).Split(';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string value = part.Substring(colon + 1).Replace("!important", "").Trim();
            result[part.Substring(0, colon).Trim()] = value;
        }

        return result;
    }

    private bool Matches(string selector, SvgElement e)
    {
        // A single compound: tag, .class, #id, or tag.class; anything else does not match.
        string tag = "";
        var classes = new List<string>();
        string? id = null;
        int i = 0;
        while (i < selector.Length)
        {
            char c = selector[i];
            int start = ++i;
            if (c is '.' or '#')
            {
                while (i < selector.Length && (char.IsLetterOrDigit(selector[i]) || selector[i] is '-' or '_')) i++;
                if (c == '.') classes.Add(selector.Substring(start, i - start));
                else id = selector.Substring(start, i - start);
            }
            else if (char.IsLetter(c) || c == '*')
            {
                i = start - 1;
                while (i < selector.Length && (char.IsLetterOrDigit(selector[i]) || selector[i] is '-' or '_' or '*')) i++;
                tag = selector.Substring(start - 1, i - start + 1).ToLowerInvariant();
            }
            else
            {
                return false;
            }
        }

        if (tag.Length > 0 && tag != "*" && tag != e.Name) return false;
        if (id is not null && e["id"] != id) return false;
        if (classes.Count == 0) return tag.Length > 0 || id is not null;
        string[] own = (e["class"] ?? "").Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return classes.All(own.Contains);
    }

    /// <summary>
    /// The element's style: inherited values, then presentation attributes, then style rules, then
    /// the style attribute, each overriding the one before.
    /// </summary>
    private Dictionary<string, string> Style(SvgElement e, Dictionary<string, string> parent)
    {
        var style = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> p in parent)
        {
            if (Inherited.Contains(p.Key)) style[p.Key] = p.Value;
        }

        foreach (KeyValuePair<string, string> a in e.Attributes)
        {
            if (Properties.Contains(a.Key) && a.Key != "transform") style[a.Key] = a.Value.Trim();
        }

        foreach ((string selector, Dictionary<string, string> declarations) in _rules)
        {
            if (!Matches(selector, e)) continue;
            foreach (KeyValuePair<string, string> d in declarations) style[d.Key] = d.Value;
        }

        if (e["style"] is string inline)
        {
            foreach (KeyValuePair<string, string> d in Declarations(inline)) style[d.Key] = d.Value;
        }

        foreach (string key in style.Keys.ToList())
        {
            if (style[key] == "inherit") style[key] = parent.TryGetValue(key, out string? v) ? v : "";
        }

        return style;
    }

    private static double Number(string? s, double fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string t = s!.Trim();
        int end = 0;
        while (end < t.Length && (char.IsDigit(t[end]) || t[end] is '.' or '-' or '+' or 'e' or 'E'))
        {
            if (t[end] is 'e' or 'E' && (end + 1 >= t.Length || !(char.IsDigit(t[end + 1]) || t[end + 1] is '-' or '+'))) break;
            end++;
        }

        return Compat.TryParseDouble(t, 0, end, out double v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : fallback;
    }

    /// <summary>A length in user units: absolute units at 96 per inch, em of the font size, percentages of <paramref name="basis"/>.</summary>
    internal static double Length(string? s, double basis, double fontSize = DefaultFontSize, double fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string t = s!.Trim();
        double n = Number(t, double.NaN);
        if (double.IsNaN(n)) return fallback;
        string unit = t.TrimStart('-', '+', '.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimStart('e', 'E', '-', '+', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Trim().ToLowerInvariant();
        double length = unit switch
        {
            "%" => n / 100 * basis,
            "pt" => n * 96 / 72,
            "pc" => n * 16,
            "mm" => n * 96 / 25.4,
            "cm" => n * 96 / 2.54,
            "in" => n * 96,
            "em" => n * fontSize,
            "ex" => n * fontSize / 2,
            _ => n,
        };

        // 1e308in overflows: an infinite length is as good as none.
        return double.IsNaN(length) || double.IsInfinity(length) ? fallback : length;
    }

    private static List<double> Numbers(string? s)
    {
        var list = new List<double>();
        if (s is null) return list;
        int i = 0;
        while (i < s.Length && list.Count < 1_000_000)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            if (i >= s.Length) break;
            int start = i;
            if (s[i] is '+' or '-') i++;
            bool dot = false;
            while (i < s.Length && (char.IsDigit(s[i]) || (s[i] == '.' && !dot)))
            {
                if (s[i] == '.') dot = true;
                i++;
            }

            if (i < s.Length && s[i] is 'e' or 'E')
            {
                int save = i++;
                if (i < s.Length && s[i] is '+' or '-') i++;
                if (i < s.Length && char.IsDigit(s[i])) while (i < s.Length && char.IsDigit(s[i])) i++;
                else i = save;
            }

            if (i == start)
            {
                i++;
                continue;
            }

            if (Compat.TryParseDouble(s, start, i - start, out double v) && !double.IsInfinity(v) && !double.IsNaN(v)) list.Add(v);
        }

        return list;
    }

    /// <summary>A color: hex, rgb(), rgba(), hsl(), hsla(), a name, transparent or currentColor.</summary>
    internal static PdfColor? Color(string? value, Dictionary<string, string> style)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value!.Trim();
        string lower = v.ToLowerInvariant();
        if (lower == "currentcolor") return style.TryGetValue("color", out string? c) && !c.Equals("currentcolor", StringComparison.OrdinalIgnoreCase) ? Color(c, style) : PdfColor.Black;
        if (lower == "transparent") return PdfColor.Rgb(0, 0, 0, 0);
        if (v[0] == '#')
        {
            string hex = v.Substring(1);
            if (!hex.All(Uri.IsHexDigit) || hex.Length is not (3 or 4 or 6 or 8)) return null;
            if (hex.Length <= 4) hex = string.Concat(hex.Select(ch => new string(ch, 2)));
            int Byte(int k) => Convert.ToInt32(hex.Substring(k * 2, 2), 16);
            return PdfColor.FromRgb((byte)Byte(0), (byte)Byte(1), (byte)Byte(2), hex.Length == 8 ? Byte(3) / 255.0 : 1);
        }

        int paren = lower.IndexOf('(');
        if (paren > 0 && Compat.EndsWith(lower, ')'))
        {
            string function = lower.Substring(0, paren).Trim();
            string[] parts = lower.Substring(paren + 1, lower.Length - paren - 2).Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            double Component(string p, double scale) => Compat.EndsWith(p, '%') ? Number(p) / 100 * scale : Number(p);
            double alpha = parts.Length > 3 ? Math.Max(0, Math.Min(1, Component(parts[3], 1))) : 1;
            if (function is "rgb" or "rgba")
            {
                return PdfColor.Rgb(Clamp(Component(parts[0], 255) / 255), Clamp(Component(parts[1], 255) / 255), Clamp(Component(parts[2], 255) / 255), alpha);
            }

            if (function is "hsl" or "hsla")
            {
                double h = ((Number(parts[0]) % 360) + 360) % 360, s = Clamp(Component(parts[1], 1)), l = Clamp(Component(parts[2], 1));
                double F(int n)
                {
                    double k = (n + (h / 30)) % 12;
                    return l - (s * Math.Min(l, 1 - l) * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)));
                }

                return PdfColor.Rgb(F(0), F(8), F(4), alpha);
            }

            return null;
        }

        return NamedColors.Rgb.TryGetValue(lower, out var named) ? PdfColor.FromRgb(named.R, named.G, named.B) : null;
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>A transform list (SVG 1.1, 7.6): matrix, translate, scale, rotate (about a point), skewX, skewY.</summary>
    internal static PdfMatrix Transform(string? value)
    {
        PdfMatrix result = PdfMatrix.Identity;
        if (string.IsNullOrWhiteSpace(value)) return result;
        int i = 0;
        string s = value!;
        while (i < s.Length)
        {
            int open = s.IndexOf('(', i);
            if (open < 0) break;
            int close = s.IndexOf(')', open);
            if (close < 0) break;
            string name = s.Substring(i, open - i).Trim(' ', ',', '\t', '\n', '\r').ToLowerInvariant();
            List<double> a = Numbers(s.Substring(open + 1, close - open - 1));
            double A(int k, double fallback = 0) => k < a.Count ? a[k] : fallback;
            PdfMatrix m = name switch
            {
                "matrix" when a.Count >= 6 => new PdfMatrix(a[0], a[1], a[2], a[3], a[4], a[5]),
                "translate" => PdfMatrix.CreateTranslation(A(0), A(1)),
                "scale" => PdfMatrix.CreateScale(A(0, 1), A(1, A(0, 1))),
                "rotate" => Rotate(A(0), A(1), A(2)),
                "skewx" => new PdfMatrix(1, 0, Math.Tan(A(0) * Math.PI / 180), 1, 0, 0),
                "skewy" => new PdfMatrix(1, Math.Tan(A(0) * Math.PI / 180), 0, 1, 0, 0),
                _ => PdfMatrix.Identity,
            };

            // Functions apply right to left: the list is a product in written order.
            result = PdfMatrix.Multiply(m, result);
            i = close + 1;
        }

        return result;
    }

    private static PdfMatrix Rotate(double degrees, double cx, double cy)
    {
        double r = degrees * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
        var rotation = new PdfMatrix(cos, sin, -sin, cos, 0, 0);
        if (cx == 0 && cy == 0) return rotation;
        return PdfMatrix.Multiply(PdfMatrix.Multiply(PdfMatrix.CreateTranslation(-cx, -cy), rotation), PdfMatrix.CreateTranslation(cx, cy));
    }

    /// <summary>Lays out a viewport (the root, a nested svg, or a used symbol) and draws its content.</summary>
    private void Viewport(SvgElement e, double x, double y, double width, double height, Dictionary<string, string> parentStyle, int depth)
    {
        Dictionary<string, string> style = Style(e, parentStyle);
        if (style.TryGetValue("display", out string? display) && display == "none") return;
        using PdfGraphicsState state = _g.Save();
        _g.TranslateTransform(x, y);
        if (e.Parent is not null && V(style, "overflow") is not ("visible" or "auto")) _g.IntersectClip(0, 0, width, height);
        ApplyViewBox(e["viewbox"], e["preserveaspectratio"], width, height);
        Children(e, style, depth);
    }

    /// <summary>Maps a viewBox into a viewport with preserveAspectRatio (SVG 1.1, 7.8): xMidYMid meet by default.</summary>
    internal void ApplyViewBox(string? viewBox, string? preserve, double width, double height)
    {
        List<double> vb = Numbers(viewBox);
        if (vb.Count != 4 || vb[2] <= 0 || vb[3] <= 0) return;
        double sx = width / vb[2], sy = height / vb[3];
        string p = (preserve ?? "xMidYMid meet").Trim();
        if (!p.StartsWith("none", StringComparison.OrdinalIgnoreCase))
        {
            bool slice = p.EndsWith("slice", StringComparison.OrdinalIgnoreCase);
            double scale = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
            string align = p.Split(' ')[0];
            double fx = Compat.Contains(align, "xMid", StringComparison.OrdinalIgnoreCase) ? 0.5 : Compat.Contains(align, "xMax", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            double fy = Compat.Contains(align, "YMid", StringComparison.Ordinal) ? 0.5 : Compat.Contains(align, "YMax", StringComparison.Ordinal) ? 1 : 0;
            _g.TranslateTransform((width - (vb[2] * scale)) * fx, (height - (vb[3] * scale)) * fy);
            sx = sy = scale;
        }

        _g.ScaleTransform(sx, sy);
        _g.TranslateTransform(-vb[0], -vb[1]);
    }

    private void Children(SvgElement e, Dictionary<string, string> style, int depth)
    {
        foreach (SvgElement child in e.Children)
        {
            if (_drawn > MaxDrawn || _visited > MaxVisited) return;
            Element(child, style, depth);
        }
    }

    private void Element(SvgElement e, Dictionary<string, string> parentStyle, int depth)
    {
        if (++_visited > MaxVisited) return;
        if (e.Name is "defs" or "symbol" or "lineargradient" or "radialgradient" or "clippath" or "mask" or "pattern" or "filter" or "style" or "title" or "desc" or "metadata" or "script") return;
        Dictionary<string, string> style = Style(e, parentStyle);
        if (V(style, "display") == "none") return;

        using PdfGraphicsState state = _g.Save();
        if (e["transform"] is string transform) _g.MultiplyTransform(Transform(transform));
        if (style.TryGetValue("clip-path", out string? clip) && Reference(clip) is SvgElement clipPath) Clip(clipPath, style);

        // Group opacity is applied to the colors drawn inside, which matches when shapes do not overlap.
        double opacity = Clamp(Number(V(style, "opacity"), 1));
        double inherited = Number(V(parentStyle, "-opacity"), 1);
        style["-opacity"] = (opacity * inherited).ToString(CultureInfo.InvariantCulture);

        switch (e.Name)
        {
            case "g" or "a" or "switch":
                if (e.Name == "switch")
                {
                    if (e.Children.FirstOrDefault() is SvgElement first) Element(first, style, depth);
                }
                else
                {
                    Children(e, style, depth);
                }

                break;
            case "svg":
                Viewport(e, Length(e["x"], 0), Length(e["y"], 0), Length(e["width"], 0, fallback: 100), Length(e["height"], 0, fallback: 100), parentStyle, depth);
                break;
            case "use":
                Use(e, style, depth);
                break;
            case "text":
                Text(e, style);
                break;
            case "image":
                Image(e, style);
                break;
            default:
                if (Shape(e, style) is PdfPath path) Paint(path, style);
                break;
        }
    }

    private SvgElement? Reference(string value)
    {
        string v = value.Trim();
        if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int close = v.IndexOf(')');
            v = v.Substring(4, (close < 0 ? v.Length : close) - 4).Trim(' ', '"', '\'');
        }

        return Compat.StartsWith(v, '#') && _ids.TryGetValue(v.Remove(0, 1), out SvgElement? e) ? e : null;
    }

    private void Use(SvgElement e, Dictionary<string, string> style, int depth)
    {
        if (depth >= MaxUseDepth || e["href"] is not string href || Reference(href) is not SvgElement target) return;
        double x = Length(e["x"], 0), y = Length(e["y"], 0);
        if (target.Name is "symbol" or "svg")
        {
            double w = Length(e["width"], 0, fallback: Length(target["width"], 0, fallback: 100));
            double h = Length(e["height"], 0, fallback: Length(target["height"], 0, fallback: 100));
            Dictionary<string, string> inner = Style(target, style);
            using PdfGraphicsState state = _g.Save();
            _g.TranslateTransform(x, y);
            ApplyViewBox(target["viewbox"], target["preserveaspectratio"], w, h);
            Children(target, inner, depth + 1);
            return;
        }

        using (_g.Save())
        {
            _g.TranslateTransform(x, y);
            Element(target, style, depth + 1);
        }
    }

    /// <summary>The geometry of a basic shape or path in user units; null for elements that are not shapes.</summary>
    private PdfPath? Shape(SvgElement e, Dictionary<string, string> style)
    {
        double fs = Length(V(style, "font-size"), 0, DefaultFontSize, DefaultFontSize);
        double L(string attribute, double fallback = 0) => Length(e[attribute], 0, fs, fallback);
        var path = new PdfPath();
        switch (e.Name)
        {
            case "rect":
                double w = L("width"), h = L("height");
                if (w <= 0 || h <= 0) return null;
                double rx = L("rx", -1), ry = L("ry", -1);
                if (rx < 0) rx = ry;
                if (ry < 0) ry = rx;
                rx = Math.Min(Math.Max(0, rx), w / 2);
                ry = Math.Min(Math.Max(0, ry), h / 2);
                return rx > 0 && ry > 0 ? path.AddRoundedRectangle(L("x"), L("y"), w, h, new PdfCornerRadii(rx, ry, rx, ry, rx, ry, rx, ry)) : path.AddRectangle(L("x"), L("y"), w, h);
            case "circle":
                double r = L("r");
                return r > 0 ? path.AddEllipse(L("cx") - r, L("cy") - r, 2 * r, 2 * r) : null;
            case "ellipse":
                double erx = L("rx"), ery = L("ry");
                return erx > 0 && ery > 0 ? path.AddEllipse(L("cx") - erx, L("cy") - ery, 2 * erx, 2 * ery) : null;
            case "line":
                return path.MoveTo(L("x1"), L("y1")).LineTo(L("x2"), L("y2"));
            case "polyline" or "polygon":
                List<double> points = Numbers(e["points"]);
                if (points.Count < 4) return null;
                path.MoveTo(points[0], points[1]);
                for (int k = 2; k + 1 < points.Count; k += 2) path.LineTo(points[k], points[k + 1]);
                if (e.Name == "polygon") path.Close();
                return path;
            case "path":
                return SvgPath.Parse(e["d"]);
            default:
                return null;
        }
    }

    // Dictionary.GetValueOrDefault is not on .NET Standard 2.0.
    private static string? V(Dictionary<string, string> style, string key) => style.TryGetValue(key, out string? v) ? v : null;

    private static string Get(Dictionary<string, string> style, string key, string fallback) =>
        style.TryGetValue(key, out string? v) && v.Length > 0 ? v : fallback;

    /// <summary>Fills and strokes a path as the style asks: colors, gradients, fill rule, dashes, caps and joins.</summary>
    private void Paint(PdfPath path, Dictionary<string, string> style)
    {
        if (Get(style, "visibility", "visible") is "hidden" or "collapse") return;
        _drawn++;
        double opacity = Number(V(style, "-opacity"), 1);
        path.FillRule = Get(style, "fill-rule", "nonzero").Equals("evenodd", StringComparison.OrdinalIgnoreCase) ? PdfFillRule.EvenOdd : PdfFillRule.NonZero;

        string fill = Get(style, "fill", "black");
        double fillOpacity = Clamp(Number(V(style, "fill-opacity"), 1)) * opacity;
        if (!fill.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (Reference(fill) is SvgElement gradient && gradient.Name is "lineargradient" or "radialgradient")
            {
                Gradient(path, gradient, style, fillOpacity);
            }
            else if (Color(Reference(fill) is null && fill.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ? FallbackColor(fill) : fill, style) is PdfColor color)
            {
                _g.DrawPath(null, new PdfSolidBrush(color.WithAlpha(color.Alpha * fillOpacity)), path);
            }
        }

        string stroke = Get(style, "stroke", "none");
        double width = Length(V(style, "stroke-width"), 0, fallback: 1);
        if (stroke.Equals("none", StringComparison.OrdinalIgnoreCase) || width <= 0) return;

        // A gradient stroke uses its first stop's color: pens are solid.
        PdfColor? strokeColor = Reference(stroke) is SvgElement strokeGradient ? Stops(strokeGradient, style).FirstOrDefault().Color : Color(stroke, style);
        if (strokeColor is not PdfColor sc) return;
        double strokeOpacity = Clamp(Number(V(style, "stroke-opacity"), 1)) * opacity;
        List<double> dashes = Numbers(V(style, "stroke-dasharray")).Select(Math.Abs).ToList();
        if (dashes.Count % 2 == 1) dashes.AddRange(dashes.ToList());
        var pen = new PdfPen(sc.WithAlpha(sc.Alpha * strokeOpacity), width)
        {
            LineCap = Get(style, "stroke-linecap", "butt").ToLowerInvariant() switch
            {
                "round" => PdfLineCap.Round,
                "square" => PdfLineCap.Square,
                _ => PdfLineCap.Butt,
            },
            LineJoin = Get(style, "stroke-linejoin", "miter").ToLowerInvariant() switch
            {
                "round" => PdfLineJoin.Round,
                "bevel" => PdfLineJoin.Bevel,
                _ => PdfLineJoin.Miter,
            },
            MiterLimit = Math.Max(1, Number(V(style, "stroke-miterlimit"), 4)),
            DashPattern = dashes.Count > 0 && dashes.Any(d => d > 0) ? dashes : [],
            DashOffset = Number(V(style, "stroke-dashoffset")),
        };
        _g.DrawPath(pen, null, path);
    }

    // url(#missing) red: the color after the reference is the fallback.
    private static string FallbackColor(string paint)
    {
        int close = paint.IndexOf(')');
        return close < 0 ? "none" : paint.Substring(close + 1).Trim();
    }

    /// <summary>A gradient's stops, following href to the gradient that defines them.</summary>
    private List<PdfGradientStop> Stops(SvgElement gradient, Dictionary<string, string> style)
    {
        SvgElement source = gradient;
        for (int hops = 0; hops < 8 && source.Children.All(c => c.Name != "stop") && source["href"] is string href && Reference(href) is SvgElement next; hops++) source = next;
        var stops = new List<PdfGradientStop>();
        double last = 0;
        foreach (SvgElement stop in source.Children.Where(c => c.Name == "stop"))
        {
            Dictionary<string, string> s = Style(stop, style);
            string offsetText = stop["offset"] ?? "0";
            double offset = Compat.EndsWith(offsetText.TrimEnd(), '%') ? Number(offsetText) / 100 : Number(offsetText);
            offset = Math.Max(last, Clamp(offset));
            last = offset;
            PdfColor color = Color(Get(s, "stop-color", "black"), s) ?? PdfColor.Black;
            stops.Add(new PdfGradientStop(offset, color.WithAlpha(color.Alpha * Clamp(Number(V(s, "stop-opacity"), 1)))));
        }

        return stops;
    }

    /// <summary>
    /// Fills a path with a gradient (SVG 1.1, 13.2): clipped to the path, in object bounding box or
    /// user space units, with gradientTransform; the stops pad beyond the ends.
    /// </summary>
    private void Gradient(PdfPath path, SvgElement gradient, Dictionary<string, string> style, double opacity)
    {
        List<PdfGradientStop> stops = Stops(gradient, style).Select(s => s with { Color = s.Color.WithAlpha(s.Color.Alpha * opacity) }).ToList();
        if (stops.Count == 0) return;
        if (stops.Count == 1)
        {
            _g.DrawPath(null, new PdfSolidBrush(stops[0].Color), path);
            return;
        }

        // Attributes not on the gradient come from the one its href names, a few hops at most.
        string? Attr(string name)
        {
            SvgElement? g = gradient;
            for (int hops = 0; g is not null && hops < 8; hops++)
            {
                if (g[name] is string v) return v;
                g = g["href"] is string h ? Reference(h) : null;
            }

            return null;
        }

        (double minX, double minY, double maxX, double maxY) = SvgPath.Bounds(path);
        bool user = string.Equals(Attr("gradientunits"), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
        double w = maxX - minX, h = maxY - minY;
        if (!user && (w <= 0 || h <= 0)) return;

        using PdfGraphicsState state = _g.Save();
        _g.IntersectClip(path);
        PdfMatrix space = user ? PdfMatrix.Identity : new PdfMatrix(w, 0, 0, h, minX, minY);
        PdfMatrix full = PdfMatrix.Multiply(Transform(Attr("gradienttransform")), space);
        _g.MultiplyTransform(full);

        // Cover the path's bounds, mapped back into the gradient's space.
        PdfMatrix inverse = full.Invert();
        PdfPoint[] corners = [inverse.Transform(new(minX, minY)), inverse.Transform(new(maxX, minY)), inverse.Transform(new(minX, maxY)), inverse.Transform(new(maxX, maxY))];
        double cx0 = corners.Min(c => c.X), cy0 = corners.Min(c => c.Y), cx1 = corners.Max(c => c.X), cy1 = corners.Max(c => c.Y);
        double basisW = user ? w : 1, basisH = user ? h : 1;
        double P(string name, double basis, double fallback)
        {
            string? v = Attr(name);
            if (v is null) return fallback;
            return Compat.EndsWith(v.TrimEnd(), '%') ? Number(v) / 100 * basis : (user ? Length(v, basis) : Number(v));
        }

        PdfBrush brush;
        if (gradient.Name == "lineargradient")
        {
            brush = new PdfLinearGradientBrush(
                new PdfPoint(P("x1", basisW, 0), P("y1", basisH, 0)),
                new PdfPoint(P("x2", basisW, user ? w : 1), P("y2", basisH, 0)),
                stops);
        }
        else
        {
            double cx = P("cx", basisW, user ? minX + (w / 2) : 0.5), cy = P("cy", basisH, user ? minY + (h / 2) : 0.5);
            double r = P("r", Math.Sqrt(((basisW * basisW) + (basisH * basisH)) / 2), user ? Math.Max(w, h) / 2 : 0.5);
            double fx = P("fx", basisW, cx), fy = P("fy", basisH, cy);
            if (r <= 0) return;
            brush = new PdfRadialGradientBrush(new PdfPoint(fx, fy), 0, new PdfPoint(cx, cy), r, stops);
        }

        _g.DrawRectangle(null, brush, cx0, cy0, cx1 - cx0, cy1 - cy0);
    }

    /// <summary>clip-path: the union of the clip path's shapes, in user space.</summary>
    private void Clip(SvgElement clipPath, Dictionary<string, string> style)
    {
        var union = new PdfPath();
        bool any = false;
        foreach (SvgElement child in clipPath.Children)
        {
            if (++_visited > MaxVisited) break;
            SvgElement shape = child.Name == "use" && child["href"] is string href && Reference(href) is SvgElement used ? used : child;
            if (Shape(shape, style) is not PdfPath p) continue;
            if (child["transform"] is string t)
            {
                PdfMatrix m = Transform(t);
                p = SvgPath.Transformed(p, m);
            }

            union.Append(p);
            any = true;
        }

        if (!any) union.AddRectangle(0, 0, 0, 0);
        _g.IntersectClip(union);
    }

    /// <summary>Text and tspan in a standard font: position, size, weight, style and text-anchor.</summary>
    private void Text(SvgElement e, Dictionary<string, string> style)
    {
        double x = Numbers(e["x"]).FirstOrDefault(), y = Numbers(e["y"]).FirstOrDefault();
        x += Numbers(e["dx"]).FirstOrDefault();
        y += Numbers(e["dy"]).FirstOrDefault();
        var runs = new List<(string Text, Dictionary<string, string> Style, double? X, double? Y, double Dx, double Dy)>();
        string own = Collapse(e.Text.ToString());
        if (own.Length > 0) runs.Add((own, style, null, null, 0, 0));
        foreach (SvgElement span in e.Children.Where(c => c.Name == "tspan"))
        {
            string text = Collapse(span.Text.ToString());
            if (text.Length == 0) continue;
            List<double> sx = Numbers(span["x"]), sy = Numbers(span["y"]);
            runs.Add((text, Style(span, style), sx.Count > 0 ? sx[0] : null, sy.Count > 0 ? sy[0] : null, Numbers(span["dx"]).FirstOrDefault(), Numbers(span["dy"]).FirstOrDefault()));
        }

        if (runs.Count == 0 || Get(style, "visibility", "visible") is "hidden" or "collapse") return;
        double opacity = Number(V(style, "-opacity"), 1);

        // text-anchor moves the whole chunk by its measured width.
        double total = runs.Sum(r => Font(r.Style).MeasureString(Renderable(r.Text, Font(r.Style)), FontSize(r.Style)));
        string anchor = Get(style, "text-anchor", "start").ToLowerInvariant();
        double cursor = x - (anchor == "middle" ? total / 2 : anchor == "end" ? total : 0);
        foreach (var run in runs)
        {
            if (run.X is double ax) cursor = ax;
            if (run.Y is double ay) y = ay;
            cursor += run.Dx;
            y += run.Dy;
            PdfFont font = Font(run.Style);
            double size = FontSize(run.Style);
            string text = Renderable(run.Text, font);
            if (Color(Get(run.Style, "fill", "black"), run.Style) is PdfColor color && !Get(run.Style, "fill", "black").Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                _g.DrawString(text, font, size, new PdfSolidBrush(color.WithAlpha(color.Alpha * opacity * Clamp(Number(V(run.Style, "fill-opacity"), 1)))), cursor, y);
                _drawn++;
            }

            cursor += font.MeasureString(text, size);
        }
    }

    private static string Collapse(string text) => string.Join(" ", text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    private static double FontSize(Dictionary<string, string> style) => Math.Max(0.1, Length(V(style, "font-size"), DefaultFontSize, DefaultFontSize, DefaultFontSize));

    /// <summary>The standard font closest to the family, weight and style.</summary>
    private static PdfFont Font(Dictionary<string, string> style)
    {
        string family = Get(style, "font-family", "serif").ToLowerInvariant();
        string weight = Get(style, "font-weight", "normal").ToLowerInvariant();
        bool bold = weight is "bold" or "bolder" || (int.TryParse(weight, NumberStyles.None, CultureInfo.InvariantCulture, out int w) && w >= 600);
        bool italic = Get(style, "font-style", "normal").ToLowerInvariant() is "italic" or "oblique";
        if (family.Contains("mono") || family.Contains("courier"))
        {
            return bold ? (italic ? PdfStandardFont.CourierBoldOblique : PdfStandardFont.CourierBold) : (italic ? PdfStandardFont.CourierOblique : PdfStandardFont.Courier);
        }

        if (family.Contains("serif") && !family.Contains("sans") || family.Contains("times") || family.Contains("georgia"))
        {
            return bold ? (italic ? PdfStandardFont.TimesBoldItalic : PdfStandardFont.TimesBold) : (italic ? PdfStandardFont.TimesItalic : PdfStandardFont.TimesRoman);
        }

        return bold ? (italic ? PdfStandardFont.HelveticaBoldOblique : PdfStandardFont.HelveticaBold) : (italic ? PdfStandardFont.HelveticaOblique : PdfStandardFont.Helvetica);
    }

    // Characters the font lacks become question marks rather than failing the drawing.
    private static string Renderable(string text, PdfFont font)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int cp = char.ConvertToUtf32(text, i);
            sb.Append(font.HasGlyph(cp) ? char.ConvertFromUtf32(cp) : "?");
        }

        return sb.ToString();
    }

    /// <summary>An embedded JPEG or PNG image (a data URI), placed with preserveAspectRatio.</summary>
    private void Image(SvgElement e, Dictionary<string, string> style)
    {
        if (e["href"] is not string href || !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        int comma = href.IndexOf(',');
        if (comma < 0 || href.IndexOf(";base64", 0, comma, StringComparison.OrdinalIgnoreCase) < 0) return;
        PdfImage image;
        try
        {
            image = PdfImage.Load(Convert.FromBase64String(href.Substring(comma + 1).Trim()));
        }
        catch (FormatException)
        {
            return;
        }

        double x = Length(e["x"], 0), y = Length(e["y"], 0);
        double iw = image.PixelWidth, ih = image.PixelHeight;
        double w = Length(e["width"], 0, fallback: iw), h = Length(e["height"], 0, fallback: ih);
        if (w <= 0 || h <= 0 || iw <= 0 || ih <= 0) return;
        _drawn++;
        using PdfGraphicsState state = _g.Save();
        _g.TranslateTransform(x, y);
        _g.IntersectClip(0, 0, w, h);
        ApplyViewBox(string.Format(CultureInfo.InvariantCulture, "0 0 {0} {1}", iw, ih), e["preserveaspectratio"], w, h);
        _g.DrawImage(image, 0, 0, iw, ih);
    }
}
