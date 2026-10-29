using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Paints fragments onto a page canvas whose user space is CSS px (CSS 2.1, Appendix E,
/// simplified to tree order): backgrounds, borders, then content; text with its decorations;
/// images; links over anchors.
/// </summary>
internal sealed class Painter
{
    private readonly PdfGraphics _g;
    private readonly double _top;
    private readonly double _bottom;
    private readonly Func<string, (PdfPage Page, double Top)?> _anchor;
    private readonly Box? _skipBackground;

    /// <param name="graphics">The canvas, already scaled to px with the slice at the top of the page area.</param>
    /// <param name="top">The slice of the canvas this page shows.</param>
    /// <param name="bottom">The end of the slice.</param>
    /// <param name="anchor">Finds the page and position of an element id, for internal links.</param>
    /// <param name="skipBackground">The box whose background was propagated to the page and is not painted again.</param>
    public Painter(PdfGraphics graphics, double top, double bottom, Func<string, (PdfPage Page, double Top)?> anchor, Box? skipBackground)
    {
        _g = graphics;
        _top = top;
        _bottom = bottom;
        _anchor = anchor;
        _skipBackground = skipBackground;
    }

    internal static PdfColor ToPdf(CssColor c, double opacity = 1) => PdfColor.Rgb(c.R, c.G, c.B, Math.Max(0, Math.Min(1, c.A * opacity)));

    private bool Visible(Fragment f) => f.Bottom >= _top - 1 && f.Y <= _bottom + 1;

    private static double Opacity(ComputedStyle s)
    {
        List<CssValue> v = s["opacity"];
        if (v.Count != 1 || !v[0].IsToken) return 1;
        double o = v[0].Token.Type == CssTokenType.Percentage ? v[0].Token.Number / 100 : v[0].Token.Number;
        return Math.Max(0, Math.Min(1, o));
    }

    public void Paint(BoxFragment f, double opacity = 1)
    {
        opacity *= Opacity(f.Style);
        if (opacity <= 0) return;
        bool visible = f.Style.Keyword("visibility") == "visible";
        bool decorated = f.Box is not TableWrapperBox && !HiddenEmptyCell(f);
        if (visible && Visible(f))
        {
            if (decorated && !ReferenceEquals(f.Box, _skipBackground)) Background(f.Style, f.X, f.Y, f.Width, f.Height, f.Border, f.Padding, opacity);
            if (decorated) Borders(f.Style, f.X, f.Y, f.Width, f.Height, f.Border, true, true, opacity, f.CollapsedBorders);
            if (f.Box is ReplacedBox image && f.ContentWidth > 0 && f.ContentHeight > 0)
            {
                _g.DrawImage(image.Image, f.ContentX, f.ContentY, f.ContentWidth, f.ContentHeight);
            }

            Link(f.Box.Element, f.X, f.Y, f.Width, f.Height);
        }

        bool clip = f.Style.Keyword("overflow-x") is not "visible" || f.Style.Keyword("overflow-y") is not "visible";
        using (clip ? _g.Save() : (PdfGraphicsState?)null)
        {
            if (clip) _g.IntersectClip(f.X + f.Border.Left, f.Y + f.Border.Top, f.Width - f.Border.Horizontal, f.Height - f.Border.Vertical);
            if (f.Marker is LineFragment marker && visible) PaintLine(marker, opacity);
            foreach (Fragment child in f.Children)
            {
                if (child is BoxFragment b) Paint(b, opacity);
                else if (child is LineFragment line && Visible(line)) PaintLine(line, opacity);
            }
        }
    }

    /// <summary>Paints lines outside the box tree, such as margin box content.</summary>
    public void PaintLines(IEnumerable<LineFragment> lines)
    {
        foreach (LineFragment line in lines) PaintLine(line, 1);
    }

    private void PaintLine(LineFragment line, double opacity)
    {
        foreach (Fragment item in line.Items)
        {
            switch (item)
            {
                case InlineFragment inline when inline.Box.Style.Keyword("visibility") == "visible":
                    double o = opacity * Opacity(inline.Box.Style);
                    Background(inline.Box.Style, inline.X, inline.Y, inline.Width, inline.Height, inline.Border, inline.Padding, o);
                    Borders(inline.Box.Style, inline.X, inline.Y, inline.Width, inline.Height, inline.Border, inline.StartEdge, inline.EndEdge, o);
                    Link(inline.Box.Element, inline.X, inline.Y, inline.Width, inline.Height);
                    break;
                case TextFragment text when text.Style.Keyword("visibility") == "visible":
                    Text(text, opacity);
                    break;
                case BoxFragment atomic:
                    Paint(atomic, opacity);
                    break;
            }
        }
    }

    /// <summary>empty-cells: hide draws no background or borders for a cell without content, in the separated border model.</summary>
    private static bool HiddenEmptyCell(BoxFragment f) =>
        f.Box is TableCellBox && f.CollapsedBorders is null && f.Style.Keyword("empty-cells") == "hide" && f.Children.Count == 0;

    private void Background(ComputedStyle s, double x, double y, double w, double h, Edges border, Edges padding, double opacity)
    {
        CssColor color = s.Color("background-color");
        if (color.A <= 0 || w <= 0 || h <= 0) return;

        // background-clip: the border box by default, or the padding or content box.
        string clip = s.Keyword("background-clip");
        if (clip is "padding-box" or "content-box")
        {
            x += border.Left;
            y += border.Top;
            w -= border.Horizontal;
            h -= border.Vertical;
            if (clip == "content-box")
            {
                x += padding.Left;
                y += padding.Top;
                w -= padding.Horizontal;
                h -= padding.Vertical;
            }
        }

        if (w > 0 && h > 0) _g.DrawRectangle(null, new PdfSolidBrush(ToPdf(color, opacity)), x, y, w, h);
    }

    /// <summary>The page's background: the root's, or the body's when the root has none (CSS 2.1, 14.2).</summary>
    public static (CssColor Color, Box? Source) CanvasBackground(BoxFragment root)
    {
        if (root.Style.Color("background-color") is { A: > 0 } own) return (own, root.Box);
        BoxFragment? body = root.Children.OfType<BoxFragment>().FirstOrDefault(c => c.Box.Element?.Name == "body");
        if (body is not null && body.Style.Color("background-color") is { A: > 0 } bodyColor) return (bodyColor, body.Box);
        return (CssColor.Transparent, null);
    }

    /// <param name="collapsed">Resolved edges for a cell in the collapsing border model, top right bottom left; null for the box's own.</param>
    private void Borders(ComputedStyle s, double x, double y, double w, double h, Edges border, bool startEdge, bool endEdge, double opacity, CollapsedEdge[]? collapsed = null)
    {
        if (border.Top + border.Right + border.Bottom + border.Left <= 0) return;
        double left = startEdge ? border.Left : 0, right = endEdge ? border.Right : 0;
        double x2 = x + w, y2 = y + h;

        // Each side is a trapezoid, so corners meet on the diagonal as in browsers.
        Side(s, "top", collapsed?[0], border.Top, opacity, [new(x, y), new(x2, y), new(x2 - right, y + border.Top), new(x + left, y + border.Top)], x, y + (border.Top / 2), x2, y + (border.Top / 2));
        Side(s, "bottom", collapsed?[2], border.Bottom, opacity, [new(x, y2), new(x2, y2), new(x2 - right, y2 - border.Bottom), new(x + left, y2 - border.Bottom)], x, y2 - (border.Bottom / 2), x2, y2 - (border.Bottom / 2));
        if (startEdge) Side(s, "left", collapsed?[3], left, opacity, [new(x, y), new(x + left, y + border.Top), new(x + left, y2 - border.Bottom), new(x, y2)], x + (left / 2), y, x + (left / 2), y2);
        if (endEdge) Side(s, "right", collapsed?[1], right, opacity, [new(x2, y), new(x2 - right, y + border.Top), new(x2 - right, y2 - border.Bottom), new(x2, y2)], x2 - (right / 2), y, x2 - (right / 2), y2);
    }

    private void Side(ComputedStyle s, string side, CollapsedEdge? edge, double width, double opacity, PdfPoint[] trapezoid, double x1, double y1, double x2, double y2)
    {
        if (width <= 0) return;
        string style = edge?.Style ?? s.Keyword("border-" + side + "-style");
        CssColor color = edge?.Color ?? s.Color("border-" + side + "-color");
        if (color.A <= 0) return;
        PdfColor pdf = ToPdf(color, opacity);
        switch (style)
        {
            case "dashed" or "dotted":
                bool dotted = style == "dotted";
                var pen = new PdfPen(pdf, width)
                {
                    DashPattern = dotted ? [0, width * 2] : [width * 3, width * 3],
                    LineCap = dotted ? PdfLineCap.Round : PdfLineCap.Butt,
                };
                _g.DrawLine(pen, x1, y1, x2, y2);
                break;
            case "double" when width >= 3:
                double third = width / 3;
                bool horizontal = y1 == y2;
                double dx = horizontal ? 0 : third, dy = horizontal ? third : 0;
                var thin = new PdfPen(pdf, third);
                _g.DrawLine(thin, x1 - dx, y1 - dy, x2 - dx, y2 - dy);
                _g.DrawLine(thin, x1 + dx, y1 + dy, x2 + dx, y2 + dy);
                break;
            default:
                // solid, double too thin to split, and groove, ridge, inset and outset drawn flat.
                _g.DrawPolygon(null, new PdfSolidBrush(pdf), trapezoid);
                break;
        }
    }

    private void Text(TextFragment t, double opacity)
    {
        if (!Visible(t) || t.Glyphs.Count == 0) return;
        CssColor color = t.Style.Color();
        if (color.A > 0)
        {
            List<ShapedGlyph> glyphs = t.Glyphs;
            if (t.JustifySpace > 0)
            {
                double extra = t.JustifySpace * 1000 / t.Size;
                glyphs = glyphs.Select(g => t.Text.Length > 0 && g.CodePoint is ' ' or 0xA0 ? g with { Advance = g.Advance + extra } : g).ToList();
            }

            _g.DrawGlyphs(glyphs, t.Font, t.Size, null, new PdfSolidBrush(ToPdf(color, opacity)), t.X, t.Baseline, t.Text);
        }

        foreach (ComputedStyle d in t.Decorations) Decorate(t, d, opacity);
    }

    /// <summary>Underline, overline and line-through (CSS Text Decoration 3), in the decorating box's color.</summary>
    private void Decorate(TextFragment t, ComputedStyle decorating, double opacity)
    {
        string lines = decorating.Keywords("text-decoration-line");
        CssColor color = decorating.Color("text-decoration-color");
        if (color.A <= 0) return;
        PdfFont font = t.Font;
        double size = t.Size;
        double thickness = decorating.Length("text-decoration-thickness", size) ?? Math.Max(1, font.UnderlineThickness * size);
        string style = decorating.Keyword("text-decoration-style");
        var pen = new PdfPen(ToPdf(color, opacity), thickness)
        {
            DashPattern = style switch
            {
                "dashed" => [thickness * 3, thickness * 2],
                "dotted" => [0, thickness * 2],
                _ => [],
            },
            LineCap = style == "dotted" ? PdfLineCap.Round : PdfLineCap.Butt,
        };

        void Line(double y)
        {
            _g.DrawLine(pen, t.X, y, t.X + t.Width, y);
            if (style == "double") _g.DrawLine(pen, t.X, y + (thickness * 2), t.X + t.Width, y + (thickness * 2));
        }

        if (lines.Contains("underline")) Line(t.Baseline + (font.UnderlinePosition * size));
        if (lines.Contains("overline")) Line(t.Baseline - (font.Ascent * size));
        if (lines.Contains("line-through")) Line(t.Baseline - (font.StrikeoutPosition * size));
    }

    private void Link(Dom.HtmlElement? element, double x, double y, double w, double h)
    {
        if (element is not { Name: "a" or "area" } || element.GetAttribute("href") is not string href || w <= 0 || h <= 0) return;
        href = href.Trim();
        if (href.Length > 0 && href[0] == '#')
        {
            if (_anchor(Uri.UnescapeDataString(href.Remove(0, 1))) is (PdfPage page, double top)) _g.AddLink(x, y, w, h, page, top);
        }
        else if (Uri.TryCreate(href, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" or "mailto" or "tel" or "ftp")
        {
            _g.AddLink(x, y, w, h, uri);
        }
    }
}
