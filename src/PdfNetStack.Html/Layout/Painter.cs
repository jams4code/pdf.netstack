using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Paints fragments onto a page canvas whose user space is CSS px (CSS 2.1, Appendix E): a
/// stacking context paints its background and borders, then the descendants with a negative
/// z-index, then its content in tree order, then the descendants with a positive z-index. Within
/// the content, boxes paint in tree order: backgrounds, borders, then content; text with its
/// decorations; images; links over anchors.
/// </summary>
internal sealed partial class Painter
{
    /// <summary>The parts of a box to paint: all of it, or its own background and borders apart from the rest.</summary>
    internal enum Phase
    {
        All,
        Background,
        Foreground,
    }

    // A box painted in its stacking context's z-index layer, with the overflow clips of the boxes between them.
    private sealed record Layer(BoxFragment Box, int Z, List<BoxFragment> Clips);

    // Bounds the curves of one wavy decoration: far more than any page is wide.
    private const int MaxWaves = 4096;

    private readonly PdfGraphics _g;
    private readonly double _top;
    private readonly double _bottom;
    private readonly Func<string, (PdfPage Page, double Top)?> _anchor;
    private readonly Box? _skipBackground;
    private readonly Func<string, HtmlImage?>? _images;
    private readonly Tagging? _tags;
    private readonly bool _artifact;

    /// <param name="graphics">The canvas, already scaled to px with the slice at the top of the page area.</param>
    /// <param name="top">The slice of the canvas this page shows.</param>
    /// <param name="bottom">The end of the slice.</param>
    /// <param name="anchor">Finds the page and position of an element id, for internal links.</param>
    /// <param name="skipBackground">The box whose background was propagated to the page and is not painted again.</param>
    /// <param name="images">Loads background images by URL; null to draw none.</param>
    /// <param name="tags">The structure tree content is tagged into, or null for an untagged document.</param>
    /// <param name="artifact">Everything drawn is an artifact: repeated headers, fixed boxes, page margins.</param>
    public Painter(PdfGraphics graphics, double top, double bottom, Func<string, (PdfPage Page, double Top)?> anchor, Box? skipBackground, Func<string, HtmlImage?>? images = null, Tagging? tags = null, bool artifact = false)
    {
        _images = images;
        _tags = tags;
        _artifact = artifact;
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

    /// <summary>Paints a box and everything in it, as the root of a stacking context.</summary>
    public void Paint(BoxFragment f, double opacity = 1) => Paint(f, opacity, Phase.All, context: true);

    /// <summary>
    /// Paints part of a box as the root of a stacking context: <see cref="Phase.Background"/> paints
    /// its own background and borders, <see cref="Phase.Foreground"/> everything else, so content
    /// from outside the box can go between them.
    /// </summary>
    public void Paint(BoxFragment f, Phase phase) => Paint(f, 1, phase, context: true);

    /// <param name="context">The box roots a stacking context: its descendants with a z-index paint in its layers.</param>
    private void Paint(BoxFragment f, double opacity, Phase phase, bool context)
    {
        opacity *= Opacity(f.Style);
        if (opacity <= 0) return;
        bool visible = f.Style.Keyword("visibility") == "visible";
        bool decorated = f.Box is not TableWrapperBox && !HiddenEmptyCell(f);
        PdfCornerRadii radii = decorated ? Radii(f.Style, f.Width, f.Height) : default;

        // A transform moves the box and everything inside it; overflow clips what is inside.
        bool transformed = HasTransform(f.Style);
        bool clipped = Clips(f);
        using PdfGraphicsState? state = transformed || clipped ? _g.Save() : null;
        if (transformed) Transform(f);
        if (visible && Visible(f))
        {
            if (phase != Phase.Foreground && decorated && Decorates(f.Style, f.Border))
            {
                BeginArtifact();
                Shadows(f.Style, f.X, f.Y, f.Width, f.Height, radii, opacity);
                if (!ReferenceEquals(f.Box, _skipBackground)) Background(f.Style, f.X, f.Y, f.Width, f.Height, f.Border, f.Padding, opacity, radii);
                Borders(f.Style, f.X, f.Y, f.Width, f.Height, f.Border, true, true, opacity, f.CollapsedBorders, radii);
                Outline(f.Style, f.X, f.Y, f.Width, f.Height, opacity);
                End();
            }

            if (phase != Phase.Background && f.Box is ReplacedBox image)
            {
                BeginContent(Owner(f.Box));
                Image(f, image, radii);
                End();
            }

            if (phase != Phase.Background) Link(f.Box.Element, f.X, f.Y, f.Width, f.Height);
        }

        if (phase == Phase.Background) return;

        // overflow other than visible clips the content to the padding box, rounded with the corners.
        if (clipped) _g.IntersectClip(PaddingShape(f));

        // A stacking context paints its negative layers over its own background and under its
        // content, and its positive layers over everything (CSS 2.1, Appendix E); equal z-index
        // values keep tree order.
        IEnumerable<Layer> layers = (context || FormsStackingContext(f) ? Layers(f) : null) ?? [];
        foreach (Layer layer in layers.Where(l => l.Z < 0).OrderBy(l => l.Z)) PaintLayer(layer, opacity);

        if (f.Marker is LineFragment marker && visible) PaintLine(marker, opacity, f.Box.Element);
        foreach (Fragment child in f.Children)
        {
            if (child is BoxFragment b && !Layered(b)) Paint(b, opacity, Phase.All, context: false);
            else if (child is LineFragment line && Visible(line)) PaintLine(line, opacity);
        }

        foreach (Layer layer in layers.Where(l => l.Z > 0).OrderBy(l => l.Z)) PaintLayer(layer, opacity);
    }

    private static bool Clips(BoxFragment f) => f.Style.Keyword("overflow-x") is not "visible" || f.Style.Keyword("overflow-y") is not "visible";

    /// <summary>The padding box, rounded with the corners: where overflow clips content.</summary>
    private static PdfPath PaddingShape(BoxFragment f)
    {
        PdfCornerRadii radii = f.Box is not TableWrapperBox && !HiddenEmptyCell(f) ? Radii(f.Style, f.Width, f.Height) : default;
        return Shape(f.X + f.Border.Left, f.Y + f.Border.Top, f.Width - f.Border.Horizontal, f.Height - f.Border.Vertical, Inset(radii, f.Border));
    }

    /// <summary>
    /// The z-index of a box it applies to, positioned or a flex or grid item; null for auto or when
    /// it does not apply (CSS 2.1, 9.9.1; CSS Flexbox 1, 4.3; CSS Grid 1, 6.3).
    /// </summary>
    internal static int? ZIndex(BoxFragment f)
    {
        if (f.Style.Keyword("z-index") == "auto") return null;
        bool applies = f.Style.Keyword("position") is "relative" or "absolute" or "fixed" or "sticky" || f.Box.Parent is FlexBox or GridBox;
        double z = f.Style.Number("z-index");
        return applies ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, z)) : null;
    }

    /// <summary>A box painted in its stacking context's negative or positive layer rather than in tree order.</summary>
    private static bool Layered(BoxFragment f) => ZIndex(f) is int z && z != 0;

    /// <summary>Whether a box roots a stacking context: a z-index, opacity below one, a transform, or fixed or sticky positioning.</summary>
    private static bool FormsStackingContext(BoxFragment f) =>
        ZIndex(f) is not null || Opacity(f.Style) < 1 || HasTransform(f.Style) || f.Style.Keyword("position") is "fixed" or "sticky";

    /// <summary>
    /// The layered boxes of the stacking context <paramref name="root"/>, in tree order: its
    /// descendants with a z-index other than zero, outside any stacking context nested in it.
    /// Null when there are none.
    /// </summary>
    private static List<Layer>? Layers(Fragment root)
    {
        List<Layer>? layers = null;
        void Walk(Fragment f, List<BoxFragment> clips)
        {
            IEnumerable<Fragment> children = f switch
            {
                BoxFragment b => b.Children,
                LineFragment l => l.Items,
                _ => [],
            };
            foreach (Fragment child in children)
            {
                if (child is BoxFragment b)
                {
                    if (ZIndex(b) is int z && z != 0)
                    {
                        (layers ??= []).Add(new Layer(b, z, clips));
                        continue;
                    }

                    if (FormsStackingContext(b)) continue;
                    Walk(b, Clips(b) ? [.. clips, b] : clips);
                }
                else if (child is LineFragment)
                {
                    Walk(child, clips);
                }
            }
        }

        Walk(root, []);
        return layers;
    }

    /// <summary>Paints a layered box under the overflow clips of the boxes between it and its stacking context.</summary>
    private void PaintLayer(Layer layer, double opacity)
    {
        using PdfGraphicsState? state = layer.Clips.Count > 0 ? _g.Save() : null;
        foreach (BoxFragment clip in layer.Clips) _g.IntersectClip(PaddingShape(clip));
        Paint(layer.Box, opacity, Phase.All, context: true);
    }

    /// <summary>Paints lines outside the box tree, such as margin box content.</summary>
    public void PaintLines(IEnumerable<LineFragment> lines)
    {
        List<LineFragment> all = [.. lines];
        List<Layer> layers = [.. all.SelectMany(l => Layers(l) ?? [])];
        foreach (Layer layer in layers.Where(l => l.Z < 0).OrderBy(l => l.Z)) PaintLayer(layer, 1);
        foreach (LineFragment line in all) PaintLine(line, 1);
        foreach (Layer layer in layers.Where(l => l.Z > 0).OrderBy(l => l.Z)) PaintLayer(layer, 1);
    }

    /// <summary>Whether a box draws anything of its own: background, borders, shadow or outline.</summary>
    private static bool Decorates(ComputedStyle s, Edges border) =>
        s.Color("background-color").A > 0 || s.Keyword("background-image") != "none" || border.Top + border.Right + border.Bottom + border.Left > 0
        || s.Keyword("box-shadow") != "none" || s.Keyword("outline-style") is not ("none" or "");

    /// <summary>The element content belongs to: the box's own, else its nearest ancestor's.</summary>
    private static Dom.HtmlElement? Owner(Box? box)
    {
        for (Box? b = box; b is not null; b = b.Parent)
        {
            if (b.Element is not null) return b.Element;
        }

        return null;
    }

    /// <summary>Opens the marked content of a structure element, or an artifact in an artifact painter.</summary>
    private void BeginContent(Dom.HtmlElement? owner)
    {
        if (_tags is null || _g.Page is not PdfPage page) return;
        if (_artifact)
        {
            _g.BeginArtifact();
            return;
        }

        int mcid = _tags.Content(page, owner);
        _g.BeginMarkedContent(_tags.TagOf(owner), mcid);
    }

    private void BeginArtifact()
    {
        if (_tags is not null && _g.Page is not null) _g.BeginArtifact();
    }

    private void End()
    {
        if (_tags is not null && _g.Page is not null) _g.EndMarkedContent();
    }

    private void PaintLine(LineFragment line, double opacity, Dom.HtmlElement? fallbackOwner = null)
    {
        foreach (Fragment item in line.Items)
        {
            switch (item)
            {
                case InlineFragment inline when inline.Box.Style.Keyword("visibility") == "visible":
                    double o = opacity * Opacity(inline.Box.Style);
                    if (Decorates(inline.Box.Style, inline.Border))
                    {
                        BeginArtifact();
                        Background(inline.Box.Style, inline.X, inline.Y, inline.Width, inline.Height, inline.Border, inline.Padding, o, default);
                        Borders(inline.Box.Style, inline.X, inline.Y, inline.Width, inline.Height, inline.Border, inline.StartEdge, inline.EndEdge, o);
                        End();
                    }

                    Link(inline.Box.Element, inline.X, inline.Y, inline.Width, inline.Height);
                    break;
                case TextFragment text when text.Style.Keyword("visibility") == "visible":
                    Text(text, opacity, Owner(text.Owner) ?? fallbackOwner);
                    break;
                // An atomic inline paints whole, but its layered descendants belong to the enclosing stacking context.
                case BoxFragment atomic when !Layered(atomic):
                    Paint(atomic, opacity, Phase.All, context: false);
                    break;
            }
        }
    }

    /// <summary>empty-cells: hide draws no background or borders for a cell without content, in the separated border model.</summary>
    private static bool HiddenEmptyCell(BoxFragment f) =>
        f.Box is TableCellBox && f.CollapsedBorders is null && f.Style.Keyword("empty-cells") == "hide" && f.Children.Count == 0;

    /// <summary>The page's background: the root's, or the body's when the root has none (CSS 2.1, 14.2).</summary>
    public static (CssColor Color, Box? Source) CanvasBackground(BoxFragment root)
    {
        if (root.Style.Color("background-color") is { A: > 0 } own) return (own, root.Box);
        BoxFragment? body = root.Children.OfType<BoxFragment>().FirstOrDefault(c => c.Box.Element?.Name == "body");
        if (body is not null && body.Style.Color("background-color") is { A: > 0 } bodyColor) return (bodyColor, body.Box);
        return (CssColor.Transparent, null);
    }

    /// <param name="collapsed">Resolved edges for a cell in the collapsing border model, top right bottom left; null for the box's own.</param>
    /// <param name="radii">Rounded corners: the sides are drawn inside the rounded ring between the border and padding edges.</param>
    private void Borders(ComputedStyle s, double x, double y, double w, double h, Edges border, bool startEdge, bool endEdge, double opacity, CollapsedEdge[]? collapsed = null, PdfCornerRadii radii = default)
    {
        if (border.Top + border.Right + border.Bottom + border.Left <= 0) return;
        double left = startEdge ? border.Left : 0, right = endEdge ? border.Right : 0;
        double x2 = x + w, y2 = y + h;

        using PdfGraphicsState? state = radii.IsZero ? null : _g.Save();
        if (!radii.IsZero)
        {
            // Rounded: clip to the ring, then each side's trapezoid fills its part of it.
            PdfPath ring = new PdfPath { FillRule = PdfFillRule.EvenOdd }.AddRoundedRectangle(x, y, w, h, radii);
            double iw = w - left - right, ih = h - border.Vertical;
            if (iw > 0 && ih > 0) ring = ring.AddRoundedRectangle(x + left, y + border.Top, iw, ih, Inset(radii, border with { Left = left, Right = right }));
            _g.IntersectClip(ring);
        }

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

    private void Text(TextFragment t, double opacity, Dom.HtmlElement? owner)
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

            // A shadow repeats the text: an artifact, so the text reads once.
            if (t.Style.Keyword("text-shadow") != "none")
            {
                BeginArtifact();
                TextShadows(t, glyphs, opacity);
                End();
            }

            BeginContent(owner);
            _g.DrawGlyphs(glyphs, t.Font, t.Size, null, new PdfSolidBrush(ToPdf(color, opacity)), t.X, t.Baseline, t.Text);
            End();
        }

        if (t.Decorations.Count == 0) return;
        BeginArtifact();
        foreach (ComputedStyle d in t.Decorations) Decorate(t, d, opacity);
        End();
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

        // A wave swings around its line: under and over lines move away from the text by its amplitude.
        bool wavy = style == "wavy";
        double amplitude = wavy ? WaveAmplitude(thickness) : 0;

        void Line(double y)
        {
            if (wavy)
            {
                Wave(pen, t.X, t.Width, y, amplitude);
                return;
            }

            _g.DrawLine(pen, t.X, y, t.X + t.Width, y);
            if (style == "double") _g.DrawLine(pen, t.X, y + (thickness * 2), t.X + t.Width, y + (thickness * 2));
        }

        if (lines.Contains("underline")) Line(t.Baseline + (font.UnderlinePosition * size) + amplitude);
        if (lines.Contains("overline")) Line(t.Baseline - (font.Ascent * size) - amplitude);
        if (lines.Contains("line-through")) Line(t.Baseline - (font.StrikeoutPosition * size));
    }

    /// <summary>How far a wavy line swings from its center, from the line's thickness; a thin line keeps a visible wave.</summary>
    internal static double WaveAmplitude(double thickness) => 1.5 * Math.Max(1, thickness);

    /// <summary>
    /// A wavy line from <paramref name="x"/> across <paramref name="width"/>, centered on
    /// <paramref name="y"/>: arches of quadratic curves, alternately up and down, each two
    /// amplitudes wide, clipped to the run. The waves start on a whole wavelength of the canvas, so
    /// the runs of one decorated line join up.
    /// </summary>
    private void Wave(PdfPen pen, double x, double width, double y, double amplitude)
    {
        if (width <= 0) return;
        double half = 2 * amplitude;
        double start = Math.Floor(x / (2 * half)) * 2 * half;
        int count = (int)Math.Min(MaxWaves, Math.Ceiling((x + width - start) / half));
        var path = new PdfPath().MoveTo(start, y);
        for (int i = 0; i < count; i++)
        {
            // A quadratic arch reaches half its control point's height.
            double from = start + (i * half);
            path.QuadraticCurveTo(from + (half / 2), y + (i % 2 == 0 ? -2 : 2) * amplitude, from + half, y);
        }

        using PdfGraphicsState state = _g.Save();
        double reach = amplitude + pen.Width;
        _g.IntersectClip(x, y - reach, width, 2 * reach);
        _g.DrawPath(pen, null, path);
    }

    private void Link(Dom.HtmlElement? element, double x, double y, double w, double h)
    {
        if (element is not { Name: "a" or "area" } || element.GetAttribute("href") is not string href || w <= 0 || h <= 0) return;
        href = href.Trim();
        if (href.Length > 0 && href[0] == '#')
        {
            if (_anchor(Uri.UnescapeDataString(href.Remove(0, 1))) is (PdfPage page, double top)) Tag(_g.AddLink(x, y, w, h, page, top), element);
        }
        else if (Uri.TryCreate(href, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" or "mailto" or "tel" or "ftp")
        {
            Tag(_g.AddLink(x, y, w, h, uri), element);
        }
    }

    private void Tag(PdfAnnotation annotation, Dom.HtmlElement link)
    {
        if (_tags is not null && !_artifact && _g.Page is PdfPage page) _tags.Annotation(annotation, page, link);
    }
}
