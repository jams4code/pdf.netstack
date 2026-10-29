using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>Margins, borders and padding from a computed style.</summary>
internal static class BoxMetrics
{
    /// <summary>Margins; auto counts as 0 here, block layout resolves it.</summary>
    public static Edges Margins(ComputedStyle s, double basis) => new(
        s.Length("margin-top", basis) ?? 0,
        s.Length("margin-right", basis) ?? 0,
        s.Length("margin-bottom", basis) ?? 0,
        s.Length("margin-left", basis) ?? 0);

    public static Edges Paddings(ComputedStyle s, double basis) => new(
        Math.Max(0, s.Length("padding-top", basis) ?? 0),
        Math.Max(0, s.Length("padding-right", basis) ?? 0),
        Math.Max(0, s.Length("padding-bottom", basis) ?? 0),
        Math.Max(0, s.Length("padding-left", basis) ?? 0));

    public static Edges Borders(ComputedStyle s) => new(Border(s, "top"), Border(s, "right"), Border(s, "bottom"), Border(s, "left"));

    /// <summary>A border width in px: 0 for no style, keywords as browsers draw them.</summary>
    public static double Border(ComputedStyle s, string side)
    {
        if (s.Keyword("border-" + side + "-style") is "none" or "hidden" or "") return 0;
        string property = "border-" + side + "-width";
        return s.Keyword(property) switch
        {
            "thin" => 1,
            "medium" => 3,
            "thick" => 5,
            _ => Math.Max(0, s.Length(property) ?? 0),
        };
    }

    public static bool IsAuto(ComputedStyle s, string property) => s[property].Count == 1 && s[property][0].IsIdent("auto");
}

/// <summary>Adjoining vertical margins being collapsed (CSS 2.1, 8.3.1): the largest positive and the most negative.</summary>
internal readonly record struct MarginCollapse(double Positive, double Negative)
{
    public static MarginCollapse None => default;

    public double Value => Positive + Negative;

    public static MarginCollapse operator +(MarginCollapse m, double margin) =>
        margin >= 0 ? m with { Positive = Math.Max(m.Positive, margin) } : m with { Negative = Math.Min(m.Negative, margin) };
}

/// <summary>Lays out the box tree into fragments on a continuous canvas of a given width, in CSS px.</summary>
internal sealed partial class LayoutEngine
{
    private readonly Dictionary<ComputedStyle, FontSet> _fonts = [];
    private readonly Dictionary<Box, (double Min, double Max)> _intrinsic = [];

    public LayoutEngine(FontResolver fonts) => Fonts = fonts;

    public FontResolver Fonts { get; }

    public FontSet FontsFor(ComputedStyle style)
    {
        if (!_fonts.TryGetValue(style, out FontSet? set)) _fonts[style] = set = Fonts.Resolve(style);
        return set;
    }

    /// <summary>Lays out the root box across <paramref name="width"/>.</summary>
    public BoxFragment Layout(BlockBox root, double width)
    {
        Placed placed = LayoutBlockLevel(root, 0, width, 0, MarginCollapse.None, null);
        return placed.Fragment;
    }

    /// <summary>A laid out block-level box and the margins still collapsing below it.</summary>
    private readonly record struct Placed(BoxFragment Fragment, MarginCollapse Bottom, bool CollapsedThrough);

    // Boxes whose margins do not collapse with their children (CSS 2.1, 9.4.1).
    private static bool EstablishesContext(Box box) =>
        box.Parent is null
        || box is InlineBlockBox or TableCellBox or TableCaptionBox or FlexBox or TableBox or ReplacedBox
        || box.Style.Keywords("display") is "flow-root" or "inline flow-root"
        || box.Style.Keyword("overflow-x") is not ("visible" or "clip")
        || box.Style.Keyword("float") != "none"
        || box.Style.Keyword("position") is "absolute" or "fixed"
        || box.Parent is FlexBox;

    /// <param name="box">The block-level box.</param>
    /// <param name="cbX">The containing block's left content edge.</param>
    /// <param name="cbWidth">The containing block's width, for auto widths and percentages.</param>
    /// <param name="cursorY">Where the box would start before its margins.</param>
    /// <param name="pending">Margins above it still collapsing.</param>
    /// <param name="cbHeight">The containing block's height when definite, for percentage heights.</param>
    /// <param name="forcedWidth">A content width decided by the parent, as for table cells and shrink-to-fit boxes.</param>
    /// <param name="borderOverride">Border widths decided by the parent, as for cells in the collapsing border model.</param>
    private Placed LayoutBlockLevel(Box box, double cbX, double cbWidth, double cursorY, MarginCollapse pending, double? cbHeight, double? forcedWidth = null, Edges? borderOverride = null)
    {
        if (box is ReplacedBox replaced) return LayoutReplacedBlock(replaced, cbX, cbWidth, cursorY, pending, cbHeight);
        if (box is TableBox table) return LayoutTable(table, cbX, cbWidth, cursorY, pending, shrink: false);
        if (box is FlexBox flex) return LayoutFlex(flex, cbX, cbWidth, cursorY, pending, cbHeight, forcedWidth);

        ComputedStyle style = box.Style;
        Edges margin = BoxMetrics.Margins(style, cbWidth);
        Edges border = borderOverride ?? BoxMetrics.Borders(style);
        Edges padding = BoxMetrics.Paddings(style, cbWidth);
        double contentWidth = forcedWidth ?? ResolveWidth(box, cbWidth, ref margin, border, padding);

        var fragment = new BoxFragment(box) { Border = border, Padding = padding };
        bool context = EstablishesContext(box);
        MarginCollapse top = pending + margin.Top;
        bool collapseTop = border.Top == 0 && padding.Top == 0 && !context;
        double contentX = cbX + margin.Left + border.Left + padding.Left;

        double? y = collapseTop ? null : cursorY + top.Value;
        double cursor = y is double y0 ? y0 + border.Top + padding.Top : cursorY;
        MarginCollapse inner = collapseTop ? top : MarginCollapse.None;
        double? ownHeight = SpecifiedHeight(style, cbHeight, border, padding);

        if (box.Children.Count > 0 && box.Children.All(c => c.IsInlineLevel))
        {
            var inline = new InlineLayout(this, style, box, box.Children, contentWidth);
            if (!inline.IsEmpty)
            {
                double lineTop = cursor + inner.Value;
                y ??= lineTop;
                List<LineFragment> lines = inline.Layout(contentX, lineTop, out double height);
                fragment.Children.AddRange(lines);
                if (lines.Count > 0)
                {
                    fragment.FirstBaseline = lines[0].Baseline;
                    fragment.LastBaseline = lines[^1].Baseline;
                }

                cursor = lineTop + height;
                inner = MarginCollapse.None;
            }
        }
        else
        {
            foreach (Box child in box.Children)
            {
                Placed placed = LayoutBlockLevel(child, contentX, contentWidth, cursor, inner, ownHeight);
                fragment.Children.Add(placed.Fragment);
                if (placed.CollapsedThrough)
                {
                    inner = placed.Bottom;
                    continue;
                }

                y ??= placed.Fragment.Y;
                fragment.FirstBaseline ??= placed.Fragment.FirstBaseline;
                if (placed.Fragment.LastBaseline is double last) fragment.LastBaseline = last;
                cursor = placed.Fragment.Bottom;
                inner = placed.Bottom;
            }
        }

        bool empty = y is null;
        y ??= cursorY + top.Value;
        double contentTop = y.Value + border.Top + padding.Top;
        bool collapseBottom = border.Bottom == 0 && padding.Bottom == 0 && !context && ownHeight is null;
        double contentHeight;
        MarginCollapse bottom;
        if (collapseBottom)
        {
            contentHeight = Math.Max(0, cursor - contentTop);
            bottom = (empty ? top : inner) + margin.Bottom;
        }
        else
        {
            contentHeight = Math.Max(0, cursor + inner.Value - contentTop);
            bottom = MarginCollapse.None + margin.Bottom;
        }

        contentHeight = ClampHeight(style, ownHeight ?? contentHeight, cbHeight, border, padding);
        fragment.X = cbX + margin.Left;
        fragment.Y = y.Value;
        fragment.Width = contentWidth + border.Horizontal + padding.Horizontal;
        fragment.Height = contentHeight + border.Vertical + padding.Vertical;
        fragment.Margin = margin;

        if (box is BlockBox { Marker: Box marker }) PlaceMarker(fragment, marker);
        bool collapsedThrough = empty && collapseTop && collapseBottom && contentHeight == 0;
        if (collapsedThrough) return new Placed(fragment, bottom, true);
        Offset(fragment, style, cbWidth, cbHeight);
        return new Placed(fragment, bottom, false);
    }

    /// <summary>Width and horizontal margins of a block in normal flow (CSS 2.1, 10.3.3), with min and max (10.4).</summary>
    private static double ResolveWidth(Box box, double cbWidth, ref Edges margin, Edges border, Edges padding)
    {
        ComputedStyle s = box.Style;
        bool borderBox = s.Keyword("box-sizing") == "border-box";
        double frame = border.Horizontal + padding.Horizontal;
        double? width = BoxMetrics.IsAuto(s, "width") ? null : s.Length("width", cbWidth);
        if (width is double w && borderBox) width = Math.Max(0, w - frame);

        double used = width ?? Math.Max(0, cbWidth - margin.Horizontal - frame);
        double? max = s.Keyword("max-width") == "none" ? null : s.Length("max-width", cbWidth);
        double? min = s.Length("min-width", cbWidth);
        if (borderBox)
        {
            max = max - frame;
            min = min - frame;
        }

        if (max is double mx && used > mx) used = Math.Max(0, mx);
        if (min is double mn && used < mn) used = mn;

        // Auto margins take the rest: both centre the box; with neither, the end margin gives.
        // With an auto width the equation already holds and nothing changes.
        bool autoLeft = BoxMetrics.IsAuto(s, "margin-left"), autoRight = BoxMetrics.IsAuto(s, "margin-right");
        double rest = cbWidth - used - frame;
        bool rtl = box.Parent?.Style.Keyword("direction") == "rtl";
        if (autoLeft && autoRight)
        {
            double half = Math.Max(0, rest) / 2;
            margin = margin with { Left = half, Right = half };
        }
        else if (autoLeft || (rtl && !autoRight))
        {
            margin = margin with { Left = rest - margin.Right };
        }
        else
        {
            margin = margin with { Right = rest - margin.Left };
        }

        return used;
    }

    private static double? SpecifiedHeight(ComputedStyle s, double? cbHeight, Edges border, Edges padding)
    {
        if (BoxMetrics.IsAuto(s, "height")) return null;
        bool percent = s["height"].Any(v => v.IsToken && v.Token.Type == CssTokenType.Percentage) || s["height"].Any(CssGrammar.IsMath);
        if (percent && cbHeight is null) return null;
        double? h = s.Length("height", cbHeight ?? 0);
        if (h is double value && s.Keyword("box-sizing") == "border-box") h = Math.Max(0, value - border.Vertical - padding.Vertical);
        return h;
    }

    private static double ClampHeight(ComputedStyle s, double height, double? cbHeight, Edges border, Edges padding)
    {
        double frame = s.Keyword("box-sizing") == "border-box" ? border.Vertical + padding.Vertical : 0;
        if (s.Keyword("max-height") != "none" && s.Length("max-height", cbHeight ?? 0) is double max && (cbHeight is not null || !HasPercent(s, "max-height")))
        {
            height = Math.Min(height, Math.Max(0, max - frame));
        }

        if (s.Length("min-height", cbHeight ?? 0) is double min && (cbHeight is not null || !HasPercent(s, "min-height")))
        {
            height = Math.Max(height, min - frame);
        }

        return Math.Max(0, height);
    }

    private static bool HasPercent(ComputedStyle s, string property) =>
        s[property].Any(v => (v.IsToken && v.Token.Type == CssTokenType.Percentage) || CssGrammar.IsMath(v));

    /// <summary>position: relative moves the box after layout without affecting others.</summary>
    private static void Offset(Fragment fragment, ComputedStyle s, double cbWidth, double? cbHeight)
    {
        if (s.Keyword("position") != "relative") return;
        double dx = s.Length("left", cbWidth) ?? -(s.Length("right", cbWidth) ?? 0);
        double dy = s.Length("top", cbHeight ?? 0) ?? -(s.Length("bottom", cbHeight ?? 0) ?? 0);
        if (dx != 0 || dy != 0) fragment.Translate(dx, dy);
    }

    private Placed LayoutReplacedBlock(ReplacedBox box, double cbX, double cbWidth, double cursorY, MarginCollapse pending, double? cbHeight)
    {
        BoxFragment f = LayoutReplaced(box, cbWidth, cbHeight);
        Edges margin = f.Margin;
        if (BoxMetrics.IsAuto(box.Style, "margin-left") || BoxMetrics.IsAuto(box.Style, "margin-right"))
        {
            double rest = cbWidth - f.Width;
            bool left = BoxMetrics.IsAuto(box.Style, "margin-left"), right = BoxMetrics.IsAuto(box.Style, "margin-right");
            margin = left && right ? margin with { Left = Math.Max(0, rest) / 2, Right = Math.Max(0, rest) / 2 } : left ? margin with { Left = rest - margin.Right } : margin;
            f.Margin = margin;
        }

        MarginCollapse top = pending + margin.Top;
        f.Translate(cbX + margin.Left - f.X, cursorY + top.Value - f.Y);
        Offset(f, box.Style, cbWidth, cbHeight);
        return new Placed(f, MarginCollapse.None + margin.Bottom, false);
    }

    /// <summary>The used size of a replaced element (CSS 2.1, 10.3.2 and 10.6.2, with 10.4 for min and max), laid out at the origin.</summary>
    private static BoxFragment LayoutReplaced(ReplacedBox box, double cbWidth, double? cbHeight)
    {
        ComputedStyle s = box.Style;
        Edges margin = BoxMetrics.Margins(s, cbWidth);
        Edges border = BoxMetrics.Borders(s);
        Edges padding = BoxMetrics.Paddings(s, cbWidth);
        bool borderBox = s.Keyword("box-sizing") == "border-box";
        double frameW = border.Horizontal + padding.Horizontal, frameH = border.Vertical + padding.Vertical;
        double iw = box.IntrinsicWidth, ih = box.IntrinsicHeight;
        double ratio = ih > 0 ? iw / ih : 1;

        double? width = BoxMetrics.IsAuto(s, "width") ? null : s.Length("width", cbWidth);
        double? height = BoxMetrics.IsAuto(s, "height") || (HasPercent(s, "height") && cbHeight is null) ? null : s.Length("height", cbHeight ?? 0);
        if (borderBox)
        {
            width = width - frameW;
            height = height - frameH;
        }

        double w, h;
        if (width is double ws && height is double hs) (w, h) = (ws, hs);
        else if (width is double wo) (w, h) = (wo, wo / ratio);
        else if (height is double ho) (w, h) = (ho * ratio, ho);
        else (w, h) = (iw, ih);

        // max-width: 100% and friends keep the aspect ratio when the other side is auto.
        double? maxW = s.Keyword("max-width") == "none" ? null : s.Length("max-width", cbWidth) - (borderBox ? frameW : 0);
        double? minW = s.Length("min-width", cbWidth) - (borderBox ? frameW : 0);
        double? maxH = s.Keyword("max-height") == "none" || (HasPercent(s, "max-height") && cbHeight is null) ? null : s.Length("max-height", cbHeight ?? 0) - (borderBox ? frameH : 0);
        double? minH = HasPercent(s, "min-height") && cbHeight is null ? null : s.Length("min-height", cbHeight ?? 0) - (borderBox ? frameH : 0);
        if (maxW is double mxw && w > mxw)
        {
            if (height is null) h = mxw / ratio;
            w = mxw;
        }

        if (minW is double mnw && w < mnw)
        {
            if (height is null) h = mnw / ratio;
            w = mnw;
        }

        if (maxH is double mxh && h > mxh)
        {
            if (width is null) w = mxh * ratio;
            h = mxh;
        }

        if (minH is double mnh && h < mnh)
        {
            if (width is null) w = mnh * ratio;
            h = mnh;
        }

        return new BoxFragment(box)
        {
            X = margin.Left,
            Y = margin.Top,
            Width = Math.Max(0, w) + frameW,
            Height = Math.Max(0, h) + frameH,
            Margin = margin,
            Border = border,
            Padding = padding,
        };
    }

    /// <summary>An atomic inline laid out at the origin: its margin box's top left corner is (0, 0).</summary>
    public BoxFragment LayoutAtomic(Box box, double available)
    {
        if (box is ReplacedBox replaced) return LayoutReplaced(replaced, available, null);
        if (box is TableBox table)
        {
            BoxFragment wrapper = LayoutTable(table, 0, available, 0, MarginCollapse.None, shrink: true).Fragment;
            wrapper.Translate(wrapper.Margin.Left - wrapper.X, wrapper.Margin.Top - wrapper.Y);
            return wrapper;
        }

        // Inline blocks and inline flex containers shrink to fit (CSS 2.1, 10.3.9).
        ComputedStyle s = box.Style;
        Edges margin = BoxMetrics.Margins(s, available);
        Edges border = BoxMetrics.Borders(s);
        Edges padding = BoxMetrics.Paddings(s, available);
        double frame = border.Horizontal + padding.Horizontal;
        double? width = null;
        if (BoxMetrics.IsAuto(s, "width"))
        {
            (double min, double max) = ContentIntrinsic(box);
            width = Math.Min(Math.Max(min, available - margin.Horizontal - frame), max);
            if (s.Keyword("max-width") != "none" && s.Length("max-width", available) is double mx) width = Math.Min(width.Value, mx - (s.Keyword("box-sizing") == "border-box" ? frame : 0));
            if (s.Length("min-width", available) is double mn) width = Math.Max(width.Value, mn - (s.Keyword("box-sizing") == "border-box" ? frame : 0));
        }

        Placed placed = LayoutBlockLevel(box, 0, available, 0, MarginCollapse.None, null, width);
        BoxFragment f = placed.Fragment;
        f.Translate(margin.Left - f.X, margin.Top - f.Y);
        return f;
    }

    /// <summary>The min-content and max-content widths of a box's margin box (CSS Sizing 3, 5).</summary>
    public (double Min, double Max) Intrinsic(Box box)
    {
        if (_intrinsic.TryGetValue(box, out var cached)) return cached;
        ComputedStyle s = box.Style;
        Edges margin = BoxMetrics.Margins(s, 0);
        Edges border = BoxMetrics.Borders(s);
        Edges padding = BoxMetrics.Paddings(s, 0);
        double frame = margin.Horizontal + border.Horizontal + padding.Horizontal;
        (double min, double max) result;
        if (box is ReplacedBox replaced)
        {
            BoxFragment f = LayoutReplaced(replaced, 0, null);
            double w = f.Width + f.Margin.Horizontal;

            // A percentage width or max-width lets the image shrink to nothing at min-content.
            result = (HasPercent(s, "width") || HasPercent(s, "max-width") ? 0 : w, w);
        }
        else if (box is TableBox table)
        {
            (double min, double max) = TableIntrinsic(table);
            result = (min + margin.Horizontal, max + margin.Horizontal);
        }
        else if (!BoxMetrics.IsAuto(s, "width") && !HasPercent(s, "width") && s.Length("width") is double fixedWidth)
        {
            double w = s.Keyword("box-sizing") == "border-box" ? fixedWidth + margin.Horizontal : fixedWidth + frame;
            result = (w, w);
        }
        else
        {
            (double min, double max) = ContentIntrinsic(box);
            result = (min + frame, max + frame);
        }

        _intrinsic[box] = result;
        return result;
    }

    /// <summary>Intrinsic widths of the content box.</summary>
    private (double Min, double Max) ContentIntrinsic(Box box)
    {
        if (box.Children.Count == 0) return (0, 0);
        if (box is FlexBox flex) return FlexIntrinsic(flex);
        if (box.Children.All(c => c.IsInlineLevel))
        {
            return new InlineLayout(this, box.Style, box, box.Children, double.MaxValue / 4, intrinsic: true).IntrinsicWidths();
        }

        double min = 0, max = 0;
        foreach (Box child in box.Children)
        {
            (double cmin, double cmax) = Intrinsic(child);
            min = Math.Max(min, cmin);
            max = Math.Max(max, cmax);
        }

        return (min, max);
    }

    /// <summary>Puts an outside list marker beside the first line: before the content edge, on the first baseline.</summary>
    private void PlaceMarker(BoxFragment item, Box marker)
    {
        var layout = new InlineLayout(this, marker.Style, null, [marker], double.MaxValue / 4);
        if (layout.IsEmpty) return;
        List<LineFragment> lines = layout.Layout(0, 0, out _);
        LineFragment line = lines[0];
        double width = line.Items.Count == 0 ? 0 : line.Items.Max(i => i.X + i.Width) - line.Items.Min(i => i.X);
        bool rtl = item.Style.Keyword("direction") == "rtl";
        double x = rtl ? item.ContentX + item.ContentWidth : item.ContentX - width;
        double baseline = item.FirstBaseline ?? (item.ContentY + (line.Baseline - line.Y));
        line.Translate(x - line.X, baseline - line.Baseline);
        line.Width = width;
        item.Marker = line;
    }
}
