using PdfNetStack.Drawing;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Visual effects (CSS Backgrounds 3, CSS Images 3, CSS Transforms 1): rounded corners,
/// background layers with images and gradients, box shadows, transforms, object-fit,
/// outlines and text shadows.
/// </summary>
internal sealed partial class Painter
{
    // A tiny repeated background on a large box must not write millions of drawing operations.
    private const int MaxTiles = 4096;

    // Steps used to approximate a shadow's blur with stacked translucent layers.
    private const int BlurSteps = 8;

    internal static List<List<CssValue>> Layers(List<CssValue> value)
    {
        var layers = new List<List<CssValue>> { new() };
        foreach (CssValue v in value)
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Comma) layers.Add([]);
            else if (!v.IsWhitespace) layers[^1].Add(v);
        }

        return layers;
    }

    /// <summary>A length or percentage token in px against <paramref name="basis"/>; null for anything else.</summary>
    private static double? Px(CssValue v, double basis)
    {
        if (CssGrammar.IsMath(v)) return CssMath.Evaluate(v, basis, out _);
        if (!v.IsToken) return null;
        return v.Token.Type switch
        {
            CssTokenType.Dimension when v.Token.Unit == "px" => v.Token.Number,
            CssTokenType.Number when v.Token.Number == 0 => 0,
            CssTokenType.Percentage => v.Token.Number / 100 * basis,
            _ => null,
        };
    }

    private static double Degrees(CssToken t) => t.Unit switch
    {
        "rad" => t.Number * 180 / Math.PI,
        "grad" => t.Number * 0.9,
        "turn" => t.Number * 360,
        _ => t.Number,
    };

    /// <summary>The corner radii of a border box, scaled down where adjacent radii would overlap (CSS Backgrounds 3, 5.5).</summary>
    internal static PdfCornerRadii Radii(ComputedStyle s, double w, double h)
    {
        (double X, double Y) Corner(string corner)
        {
            List<CssValue> v = s["border-" + corner + "-radius"].Where(x => !x.IsWhitespace).ToList();
            if (v.Count == 0) return (0, 0);
            double rx = Math.Max(0, Px(v[0], w) ?? 0);
            double ry = Math.Max(0, Px(v.Count > 1 ? v[1] : v[0], h) ?? 0);
            return rx > 0 && ry > 0 ? (rx, ry) : (0, 0);
        }

        (double tlx, double tly) = Corner("top-left");
        (double trx, double try_) = Corner("top-right");
        (double brx, double bry) = Corner("bottom-right");
        (double blx, double bly) = Corner("bottom-left");
        double f = 1;
        if (tlx + trx > w) f = Math.Min(f, w / (tlx + trx));
        if (blx + brx > w) f = Math.Min(f, w / (blx + brx));
        if (tly + bly > h) f = Math.Min(f, h / (tly + bly));
        if (try_ + bry > h) f = Math.Min(f, h / (try_ + bry));
        return new PdfCornerRadii(tlx * f, tly * f, trx * f, try_ * f, brx * f, bry * f, blx * f, bly * f);
    }

    /// <summary>Radii of a box inset by the given edges, as for the padding box inside the borders.</summary>
    private static PdfCornerRadii Inset(PdfCornerRadii r, Edges e) => new(
        Math.Max(0, r.TopLeftX - e.Left), Math.Max(0, r.TopLeftY - e.Top),
        Math.Max(0, r.TopRightX - e.Right), Math.Max(0, r.TopRightY - e.Top),
        Math.Max(0, r.BottomRightX - e.Right), Math.Max(0, r.BottomRightY - e.Bottom),
        Math.Max(0, r.BottomLeftX - e.Left), Math.Max(0, r.BottomLeftY - e.Bottom));

    private static PdfCornerRadii Grow(PdfCornerRadii r, double by) => new(
        Math.Max(0, r.TopLeftX + by), Math.Max(0, r.TopLeftY + by), Math.Max(0, r.TopRightX + by), Math.Max(0, r.TopRightY + by),
        Math.Max(0, r.BottomRightX + by), Math.Max(0, r.BottomRightY + by), Math.Max(0, r.BottomLeftX + by), Math.Max(0, r.BottomLeftY + by));

    private static PdfPath Shape(double x, double y, double w, double h, PdfCornerRadii r) =>
        r.IsZero ? new PdfPath().AddRectangle(x, y, w, h) : new PdfPath().AddRoundedRectangle(x, y, w, h, r);

    /// <summary>Outer box shadows (CSS Backgrounds 3, 7.1), under the background, never inside the box.</summary>
    private void Shadows(ComputedStyle s, double x, double y, double w, double h, PdfCornerRadii radii, double opacity)
    {
        List<CssValue> value = s["box-shadow"];
        if (value.Count == 1 && value[0].IsIdent("none")) return;

        // The first shadow is on top, so they paint last to first.
        foreach (List<CssValue> layer in Enumerable.Reverse(Layers(value)))
        {
            if (layer.Any(v => v.IsIdent("inset"))) continue;
            List<double> lengths = layer.Select(v => Px(v, 0)).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (lengths.Count < 2) continue;
            CssValue? colorValue = layer.FirstOrDefault(v => CssColor.Parse(v) is not null && Px(v, 0) is null);
            CssColor color = colorValue is null ? s.Color() : CssColor.Parse(colorValue)!.Value;
            if (color.IsCurrentColor) color = s.Color();
            if (color.A <= 0) continue;
            double blur = lengths.Count > 2 ? Math.Max(0, lengths[2]) : 0, spread = lengths.Count > 3 ? lengths[3] : 0;
            double sx = x + lengths[0] - spread, sy = y + lengths[1] - spread, sw = w + (2 * spread), sh = h + (2 * spread);
            if (sw <= 0 || sh <= 0) continue;

            using (_g.Save())
            {
                // Clip the box itself out: a shadow shows only around it.
                PdfPath outside = new PdfPath { FillRule = PdfFillRule.EvenOdd }
                    .AddRectangle(sx - (2 * blur) - 1, sy - (2 * blur) - 1, sw + (4 * blur) + 2, sh + (4 * blur) + 2);
                outside = radii.IsZero ? outside.AddRectangle(x, y, w, h) : outside.AddRoundedRectangle(x, y, w, h, radii);
                _g.IntersectClip(outside);

                if (blur <= 0)
                {
                    _g.DrawPath(null, new PdfSolidBrush(ToPdf(color, opacity)), Shape(sx, sy, sw, sh, Grow(radii, spread)));
                    continue;
                }

                // Layers from wide and faint to narrow: their sum fades out across the blur.
                var brush = new PdfSolidBrush(ToPdf(color, opacity / BlurSteps));
                for (int k = 0; k < BlurSteps; k++)
                {
                    double grow = blur * (1 - (2 * (k + 0.5) / BlurSteps));
                    if (sw + (2 * grow) <= 0 || sh + (2 * grow) <= 0) continue;
                    _g.DrawPath(null, brush, Shape(sx - grow, sy - grow, sw + (2 * grow), sh + (2 * grow), Grow(radii, spread + grow)));
                }
            }
        }
    }

    /// <summary>Background layers: color at the bottom, then images and gradients, the first listed on top.</summary>
    private void Background(ComputedStyle s, double x, double y, double w, double h, Edges border, Edges padding, double opacity, PdfCornerRadii radii)
    {
        if (w <= 0 || h <= 0) return;
        Edges contentInset = new(border.Top + padding.Top, border.Right + padding.Right, border.Bottom + padding.Bottom, border.Left + padding.Left);
        (double X, double Y, double W, double H, PdfCornerRadii R) Box(string keyword) => keyword switch
        {
            "padding-box" => (x + border.Left, y + border.Top, w - border.Horizontal, h - border.Vertical, Inset(radii, border)),
            "content-box" => (x + contentInset.Left, y + contentInset.Top, w - contentInset.Horizontal, h - contentInset.Vertical, Inset(radii, contentInset)),
            _ => (x, y, w, h, radii),
        };

        List<List<CssValue>> clips = Layers(s["background-clip"]);
        string Keyword(List<List<CssValue>> list, int i, string fallback) =>
            list[i % list.Count].FirstOrDefault(v => v.IsToken && v.Token.Type == CssTokenType.Ident)?.Token.Value.ToLowerInvariant() ?? fallback;

        // The color uses the last layer's clip.
        List<List<CssValue>> images = Layers(s["background-image"]);
        CssColor color = s.Color("background-color");
        if (color.A > 0)
        {
            var clip = Box(Keyword(clips, images.Count - 1, "border-box"));
            if (clip.W > 0 && clip.H > 0) _g.DrawPath(null, new PdfSolidBrush(ToPdf(color, opacity)), Shape(clip.X, clip.Y, clip.W, clip.H, clip.R));
        }

        List<List<CssValue>> origins = Layers(s["background-origin"]);
        List<List<CssValue>> sizes = Layers(s["background-size"]);
        List<List<CssValue>> positions = Layers(s["background-position"]);
        List<List<CssValue>> repeats = Layers(s["background-repeat"]);
        for (int i = images.Count - 1; i >= 0; i--)
        {
            CssValue? image = images[i].FirstOrDefault();
            if (image is null || image.IsIdent("none")) continue;
            var clip = Box(Keyword(clips, i, "border-box"));
            var area = Box(Keyword(origins, i, "padding-box"));
            if (clip.W <= 0 || clip.H <= 0 || area.W <= 0 || area.H <= 0) continue;
            using (_g.Save())
            {
                _g.IntersectClip(Shape(clip.X, clip.Y, clip.W, clip.H, clip.R));
                if (image.Function is string function && function.EndsWith("gradient", StringComparison.Ordinal))
                {
                    Gradient(image, area.X, area.Y, area.W, area.H, opacity);
                }
                else if (Url(image) is string url && _images?.Invoke(url) is HtmlImage picture)
                {
                    Tiles(picture, area, clip, sizes[i % sizes.Count], positions[i % positions.Count], repeats[i % repeats.Count]);
                }
            }
        }
    }

    private static string? Url(CssValue v) =>
        v.IsToken && v.Token.Type == CssTokenType.Url ? v.Token.Value
        : v.Function == "url" ? v.Children.FirstOrDefault(c => c.IsToken && c.Token.Type == CssTokenType.String)?.Token.Value
        : null;

    /// <summary>A background image placed by background-size and background-position, repeated as asked.</summary>
    private void Tiles(HtmlImage image, (double X, double Y, double W, double H, PdfCornerRadii R) area, (double X, double Y, double W, double H, PdfCornerRadii R) clip, List<CssValue> size, List<CssValue> position, List<CssValue> repeat)
    {
        double iw = image.Width, ih = image.Height;
        if (iw <= 0 || ih <= 0) return;

        // background-size: cover, contain, or a width and a height where auto keeps the ratio.
        double tw = iw, th = ih;
        if (size.Count > 0 && size[0].IsIdent("cover") || size.Count > 0 && size[0].IsIdent("contain"))
        {
            double scale = size[0].IsIdent("cover") ? Math.Max(area.W / iw, area.H / ih) : Math.Min(area.W / iw, area.H / ih);
            (tw, th) = (iw * scale, ih * scale);
        }
        else if (size.Count > 0)
        {
            double? sw = Px(size[0], area.W), sh = size.Count > 1 ? Px(size[1], area.H) : null;
            if (sw is double a && sh is double b) (tw, th) = (a, b);
            else if (sw is double a2) (tw, th) = (a2, a2 * ih / iw);
            else if (sh is double b2) (tw, th) = (b2 * iw / ih, b2);
        }

        if (tw <= 0.01 || th <= 0.01) return;
        (double px, double py) = Position(position, area.W - tw, area.H - th);
        double x0 = area.X + px, y0 = area.Y + py;

        string r = string.Join(" ", repeat.Where(v => v.IsToken).Select(v => v.Token.Value.ToLowerInvariant()));
        bool repeatX = r is "repeat" or "repeat-x" or "space" or "round" or "repeat repeat" or "repeat no-repeat" or "" || r.StartsWith("repeat ", StringComparison.Ordinal);
        bool repeatY = r is "repeat" or "repeat-y" or "space" or "round" or "repeat repeat" or "no-repeat repeat" or "";
        if (r == "repeat no-repeat") repeatY = false;

        double startX = repeatX ? x0 - (Math.Ceiling((x0 - clip.X) / tw) * tw) : x0;
        double startY = repeatY ? y0 - (Math.Ceiling((y0 - clip.Y) / th) * th) : y0;
        double endX = repeatX ? clip.X + clip.W : x0 + tw;
        double endY = repeatY ? clip.Y + clip.H : y0 + th;
        int tiles = 0;
        for (double ty = startY; ty < endY - 0.01; ty += th)
        {
            for (double tx = startX; tx < endX - 0.01; tx += tw)
            {
                if (++tiles > MaxTiles) return;
                image.Draw(_g, tx, ty, tw, th);
            }
        }
    }

    /// <summary>A two value position (CSS Backgrounds 3, 3.6): keywords, lengths and percentages of the free space.</summary>
    private static (double X, double Y) Position(List<CssValue> value, double freeX, double freeY)
    {
        double x = freeX / 2, y = freeY / 2;
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        if (items.Count == 0) return (0, 0);

        // Keywords may come in either order; a lone length is horizontal.
        bool xSet = false, ySet = false;
        for (int i = 0; i < items.Count; i++)
        {
            CssValue v = items[i];
            string? k = v.IsToken && v.Token.Type == CssTokenType.Ident ? v.Token.Value.ToLowerInvariant() : null;
            double? offset = i + 1 < items.Count ? Px(items[i + 1], 0) : null;
            switch (k)
            {
                case "left":
                    x = offset ?? 0;
                    xSet = true;
                    if (offset is not null) i++;
                    break;
                case "right":
                    x = freeX - (offset ?? 0);
                    xSet = true;
                    if (offset is not null) i++;
                    break;
                case "top":
                    y = offset ?? 0;
                    ySet = true;
                    if (offset is not null) i++;
                    break;
                case "bottom":
                    y = freeY - (offset ?? 0);
                    ySet = true;
                    if (offset is not null) i++;
                    break;
                case "center":
                    break;
                default:
                    if (!xSet)
                    {
                        x = Px(v, freeX) ?? x;
                        xSet = true;
                    }
                    else if (!ySet)
                    {
                        y = Px(v, freeY) ?? y;
                        ySet = true;
                    }

                    break;
            }
        }

        return (x, y);
    }

    /// <summary>linear-gradient() and radial-gradient() over a box (CSS Images 3, 3).</summary>
    private void Gradient(CssValue gradient, double x, double y, double w, double h, double opacity)
    {
        List<List<CssValue>> args = Layers(gradient.Children);
        if (args.Count == 0) return;
        bool radial = gradient.Function!.Contains("radial");
        List<CssValue> first = args[0];
        bool hasSetup = first.Count > 0 && (first[0].IsIdent("to") || first[0].IsIdent("circle") || first[0].IsIdent("ellipse") || first[0].IsIdent("at")
            || first.Any(v => v.IsIdent("closest-side") || v.IsIdent("farthest-corner") || v.IsIdent("closest-corner") || v.IsIdent("farthest-side"))
            || (first[0].IsToken && first[0].Token.Type == CssTokenType.Dimension && first[0].Token.Unit is "deg" or "rad" or "grad" or "turn")
            || (radial && CssColor.Parse(first[0]) is null));
        List<List<CssValue>> stopArgs = hasSetup ? args.Skip(1).ToList() : args;

        if (radial)
        {
            // Centre, then the shape out to the farthest corner, the default size.
            (double cx, double cy) = (x + (w / 2), y + (h / 2));
            int at = hasSetup ? first.FindIndex(v => v.IsIdent("at")) : -1;
            if (at >= 0)
            {
                (double px, double py) = Position(first.Skip(at + 1).ToList(), w, h);
                (cx, cy) = (x + px, y + py);
            }

            bool circle = hasSetup && first.Any(v => v.IsIdent("circle"));
            double dx = Math.Max(cx - x, x + w - cx), dy = Math.Max(cy - y, y + h - cy);
            double rx = circle ? Math.Sqrt((dx * dx) + (dy * dy)) : dx * Math.Sqrt(2);
            double ry = circle ? rx : dy * Math.Sqrt(2);
            if (rx <= 0 || ry <= 0) return;
            List<PdfGradientStop>? stops = Stops(stopArgs, rx, opacity);
            if (stops is null) return;

            // The brush is circular: an ellipse is a circle scaled vertically.
            using (_g.Save())
            {
                _g.TranslateTransform(cx, cy);
                _g.ScaleTransform(1, ry / rx);
                double scaledTop = (y - cy) * rx / ry, scaledHeight = h * rx / ry;
                _g.DrawRectangle(null, new PdfRadialGradientBrush(new PdfPoint(0, 0), 0, new PdfPoint(0, 0), rx, stops), x - cx, scaledTop, w, scaledHeight);
            }

            return;
        }

        // The gradient line: an angle (0deg points up, clockwise) or "to" a side or corner.
        double angle = 180;
        if (hasSetup && first[0].IsToken && first[0].Token.Type == CssTokenType.Dimension) angle = Degrees(first[0].Token);
        else if (hasSetup && first[0].IsIdent("to"))
        {
            bool top = first.Any(v => v.IsIdent("top")), bottom = first.Any(v => v.IsIdent("bottom"));
            bool left = first.Any(v => v.IsIdent("left")), right = first.Any(v => v.IsIdent("right"));
            if ((top || bottom) && (left || right))
            {
                // To a corner: perpendicular to the diagonal between the other two corners.
                double a = Math.Atan2(w, h) * 180 / Math.PI;
                angle = (top, right) switch
                {
                    (true, true) => a,
                    (true, false) => 360 - a,
                    (false, true) => 180 - a,
                    _ => 180 + a,
                };
            }
            else
            {
                angle = top ? 0 : right ? 90 : bottom ? 180 : left ? 270 : 180;
            }
        }

        double radians = angle * Math.PI / 180;
        double sin = Math.Sin(radians), cos = Math.Cos(radians);
        double length = Math.Abs(w * sin) + Math.Abs(h * cos);
        if (length <= 0) return;
        List<PdfGradientStop>? lineStops = Stops(stopArgs, length, opacity);
        if (lineStops is null) return;
        double mx = x + (w / 2), my = y + (h / 2);
        var start = new PdfPoint(mx - (sin * length / 2), my + (cos * length / 2));
        var end = new PdfPoint(mx + (sin * length / 2), my - (cos * length / 2));
        _g.DrawRectangle(null, new PdfLinearGradientBrush(start, end, lineStops), x, y, w, h);
    }

    /// <summary>Color stops with their positions fixed up (CSS Images 3, 3.5.3): ends at 0 and 1, gaps spread evenly, never going back.</summary>
    internal static List<PdfGradientStop>? Stops(List<List<CssValue>> args, double length, double opacity)
    {
        var colors = new List<CssColor>();
        var positions = new List<double?>();
        foreach (List<CssValue> arg in args)
        {
            CssValue? colorValue = arg.FirstOrDefault(v => CssColor.Parse(v) is not null && Px(v, length) is null);
            if (colorValue is null) continue; // an interpolation hint alone
            List<double> at = arg.Where(v => v != colorValue).Select(v => Px(v, length)).Where(v => v is not null).Select(v => v!.Value / length).ToList();
            CssColor color = CssColor.Parse(colorValue)!.Value;
            if (color.IsCurrentColor) color = CssColor.Black;
            colors.Add(color);
            positions.Add(at.Count > 0 ? at[0] : null);
            if (at.Count > 1)
            {
                colors.Add(color);
                positions.Add(at[1]);
            }
        }

        if (colors.Count == 0) return null;
        if (colors.Count == 1)
        {
            colors.Add(colors[0]);
            positions.Add(null);
        }

        positions[0] ??= 0;
        positions[^1] ??= 1;
        for (int i = 1; i < positions.Count; i++)
        {
            if (positions[i] is double p && positions[i - 1] is double q && p < q) positions[i] = q;
        }

        for (int i = 1; i < positions.Count; i++)
        {
            if (positions[i] is not null) continue;
            int next = i;
            while (positions[next] is null) next++;
            double from = positions[i - 1]!.Value, to = positions[next]!.Value;
            for (int k = i; k < next; k++) positions[k] = from + ((to - from) * (k - i + 1) / (next - i + 1));
        }

        // The brush runs from 0 to 1; stops outside are clamped there.
        return [.. colors.Select((c, i) => new PdfGradientStop(Math.Max(0, Math.Min(1, positions[i]!.Value)), ToPdf(c, opacity)))];
    }

    private static bool HasTransform(ComputedStyle s) => s["transform"] is { Count: > 0 } v && !(v.Count == 1 && v[0].IsIdent("none"));

    /// <summary>Applies transform around transform-origin (CSS Transforms 1, 6).</summary>
    private void Transform(BoxFragment f)
    {
        List<CssValue> value = f.Style["transform"];
        List<CssValue> origin = f.Style["transform-origin"].Where(v => !v.IsWhitespace).ToList();
        (double ox, double oy) = Position(origin, f.Width, f.Height);
        if (origin.Count == 0) (ox, oy) = (f.Width / 2, f.Height / 2);
        ox += f.X;
        oy += f.Y;

        _g.TranslateTransform(ox, oy);
        foreach (CssValue fn in value.Where(v => v.Function is not null))
        {
            List<CssValue> a = fn.Children.Where(c => !c.IsWhitespace && !(c.IsToken && c.Token.Type == CssTokenType.Comma)).ToList();
            double L(int i, double basis) => i < a.Count ? Px(a[i], basis) ?? 0 : 0;
            double N(int i, double fallback) => i < a.Count && a[i].IsToken && a[i].Token.Type is CssTokenType.Number ? a[i].Token.Number
                : i < a.Count && a[i].IsToken && a[i].Token.Type is CssTokenType.Percentage ? a[i].Token.Number / 100 : fallback;
            double A(int i) => i < a.Count && a[i].IsToken && a[i].Token.Type is CssTokenType.Dimension or CssTokenType.Number ? Degrees(a[i].Token) : 0;
            PdfMatrix? m = fn.Function switch
            {
                "translate" => PdfMatrix.CreateTranslation(L(0, f.Width), L(1, f.Height)),
                "translatex" => PdfMatrix.CreateTranslation(L(0, f.Width), 0),
                "translatey" => PdfMatrix.CreateTranslation(0, L(0, f.Height)),
                "scale" => PdfMatrix.CreateScale(N(0, 1), N(1, N(0, 1))),
                "scalex" => PdfMatrix.CreateScale(N(0, 1), 1),
                "scaley" => PdfMatrix.CreateScale(1, N(0, 1)),
                "rotate" or "rotatez" => Rotation(A(0)),
                "skew" => new PdfMatrix(1, Math.Tan(A(1) * Math.PI / 180), Math.Tan(A(0) * Math.PI / 180), 1, 0, 0),
                "skewx" => new PdfMatrix(1, 0, Math.Tan(A(0) * Math.PI / 180), 1, 0, 0),
                "skewy" => new PdfMatrix(1, Math.Tan(A(0) * Math.PI / 180), 0, 1, 0, 0),
                "matrix" when a.Count == 6 => new PdfMatrix(N(0, 1), N(1, 0), N(2, 0), N(3, 1), N(4, 0), N(5, 0)),
                _ => null,
            };
            if (m is PdfMatrix matrix) _g.MultiplyTransform(matrix);
        }

        _g.TranslateTransform(-ox, -oy);
    }

    /// <summary>rotate(a) as CSS defines it, clockwise on a y-down canvas: matrix(cos, sin, -sin, cos, 0, 0).</summary>
    private static PdfMatrix Rotation(double degrees)
    {
        double r = degrees * Math.PI / 180;
        return new PdfMatrix(Math.Cos(r), Math.Sin(r), -Math.Sin(r), Math.Cos(r), 0, 0);
    }

    /// <summary>An image in its content box per object-fit and object-position (CSS Images 3, 5), clipped to rounded corners.</summary>
    private void Image(BoxFragment f, ReplacedBox image, PdfCornerRadii radii)
    {
        double cw = f.ContentWidth, ch = f.ContentHeight, iw = image.IntrinsicWidth, ih = image.IntrinsicHeight;
        if (cw <= 0 || ch <= 0 || iw <= 0 || ih <= 0) return;
        double contain = Math.Min(cw / iw, ch / ih);
        (double w, double h) = f.Style.Keyword("object-fit") switch
        {
            "contain" => (iw * contain, ih * contain),
            "cover" => (iw * Math.Max(cw / iw, ch / ih), ih * Math.Max(cw / iw, ch / ih)),
            "none" => (iw, ih),
            "scale-down" => (iw * Math.Min(1, contain), ih * Math.Min(1, contain)),
            _ => (cw, ch),
        };
        (double px, double py) = Position(f.Style["object-position"], cw - w, ch - h);
        using (_g.Save())
        {
            var content = new Edges(f.Border.Top + f.Padding.Top, f.Border.Right + f.Padding.Right, f.Border.Bottom + f.Padding.Bottom, f.Border.Left + f.Padding.Left);
            PdfCornerRadii inner = Inset(radii, content);
            if (!inner.IsZero) _g.IntersectClip(Shape(f.ContentX, f.ContentY, cw, ch, inner));
            else if (w > cw + 0.01 || h > ch + 0.01) _g.IntersectClip(f.ContentX, f.ContentY, cw, ch);
            image.Image.Draw(_g, f.ContentX + px, f.ContentY + py, w, h);
        }
    }

    /// <summary>The outline, outside the border box by outline-offset (CSS UI 4, 4).</summary>
    private void Outline(ComputedStyle s, double x, double y, double w, double h, double opacity)
    {
        string style = s.Keyword("outline-style");
        if (style is "none" or "hidden" or "") return;
        double width = s.Keyword("outline-width") switch
        {
            "thin" => 1,
            "medium" => 3,
            "thick" => 5,
            _ => Math.Max(0, s.Length("outline-width") ?? 0),
        };
        if (width <= 0) return;
        CssColor color = s.Keyword("outline-color") == "invert" ? s.Color() : s.Color("outline-color");
        if (color.A <= 0) return;
        double offset = (s.Length("outline-offset") ?? 0) + (width / 2);
        var pen = new PdfPen(ToPdf(color, opacity), width)
        {
            DashPattern = style switch
            {
                "dashed" => [width * 3, width * 3],
                "dotted" => [0, width * 2],
                _ => [],
            },
            LineCap = style == "dotted" ? PdfLineCap.Round : PdfLineCap.Butt,
        };
        _g.DrawRectangle(pen, null, x - offset, y - offset, w + (2 * offset), h + (2 * offset));
    }

    /// <summary>text-shadow offsets in their colors, under the text; blur is not drawn.</summary>
    private void TextShadows(TextFragment t, List<Fonts.ShapedGlyph> glyphs, double opacity)
    {
        List<CssValue> value = t.Style["text-shadow"];
        if (value.Count == 1 && value[0].IsIdent("none")) return;
        foreach (List<CssValue> layer in Enumerable.Reverse(Layers(value)))
        {
            List<double> lengths = layer.Select(v => Px(v, 0)).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (lengths.Count < 2) continue;
            CssValue? colorValue = layer.FirstOrDefault(v => CssColor.Parse(v) is not null && Px(v, 0) is null);
            CssColor color = colorValue is null ? t.Style.Color() : CssColor.Parse(colorValue)!.Value;
            if (color.IsCurrentColor) color = t.Style.Color();
            if (color.A <= 0) continue;
            _g.DrawGlyphs(glyphs, t.Font, t.Size, null, new PdfSolidBrush(ToPdf(color, opacity)), t.X + lengths[0], t.Baseline + lengths[1], t.Text);
        }
    }
}
