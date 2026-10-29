using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>Room floats take beside a band of lines: insets from each side, and where the band changes.</summary>
internal readonly record struct Exclusion(double Left, double Right, double Next);

/// <summary>The floats of one block formatting context, as margin boxes on the canvas (CSS 2.1, 9.5).</summary>
internal sealed class FloatContext
{
    private readonly List<(double Left, double Top, double Right, double Bottom, bool IsLeft)> _floats = [];

    public bool IsEmpty => _floats.Count == 0;

    /// <summary>The lowest float's bottom edge, or negative infinity when there is none.</summary>
    public double Bottom => _floats.Count == 0 ? double.NegativeInfinity : _floats.Max(f => f.Bottom);

    /// <summary>The free span inside [<paramref name="left"/>, <paramref name="right"/>] over a band, and where the first float beside it ends.</summary>
    public (double Left, double Right, double Next) Free(double top, double height, double left, double right)
    {
        double l = left, r = right, next = double.PositiveInfinity;
        double bottom = top + Math.Max(height, 1e-6);
        foreach (var f in _floats)
        {
            if (f.Bottom <= top || f.Top >= bottom) continue;
            if (f.IsLeft) l = Math.Max(l, f.Right);
            else r = Math.Min(r, f.Left);
            next = Math.Min(next, f.Bottom);
        }

        return (l, r, next);
    }

    /// <summary>Where content clears the floats on one or both sides.</summary>
    public double Clear(string clear)
    {
        double y = double.NegativeInfinity;
        foreach (var f in _floats)
        {
            if (clear == "both" || (clear is "left" or "inline-start" && f.IsLeft) || (clear is "right" or "inline-end" && !f.IsLeft)) y = Math.Max(y, f.Bottom);
        }

        return y;
    }

    /// <summary>
    /// Places a float's margin box: no higher than <paramref name="top"/> or an earlier float, as
    /// far to its side as it goes, and lower down when it does not fit beside the others.
    /// </summary>
    public (double X, double Y) Place(double width, double height, bool left, double top, double areaLeft, double areaRight)
    {
        if (_floats.Count > 0) top = Math.Max(top, _floats.Max(f => f.Top));
        double y = top;
        for (int guard = 0; guard <= _floats.Count; guard++)
        {
            (double l, double r, double next) = Free(y, height, areaLeft, areaRight);
            if (r - l >= width - 1e-9 || double.IsInfinity(next))
            {
                double x = left ? l : r - width;
                _floats.Add((x, y, x + width, y + height, left));
                return (x, y);
            }

            y = next;
        }

        // Below every float there is the whole width.
        y = Math.Max(y, Bottom);
        double fx = left ? areaLeft : areaRight - width;
        _floats.Add((fx, y, fx + width, y + height, left));
        return (fx, y);
    }
}

/// <summary>Floats and absolute and fixed positioning (CSS 2.1, 9.5, 9.6 and 10.3.7, 10.6.4).</summary>
internal sealed partial class LayoutEngine
{
    private sealed record Pending(Box Box, double StaticX, double StaticY);

    private FloatContext _floats = new();
    private readonly Stack<List<Pending>> _positioned = new();
    private readonly List<Pending> _fixedPending = [];

    /// <summary>Boxes with position: fixed, laid out against the page area and drawn on every page.</summary>
    public List<BoxFragment> Fixed { get; } = [];

    private static bool IsOutOfFlow(Box box) => box.Style.Keyword("position") is "absolute" or "fixed";

    private static bool IsFloat(Box box) => box.Style.Keyword("float") is not ("none" or "") && !IsOutOfFlow(box);

    private static bool IsPositioned(Box box) => box.Style.Keyword("position") is "relative" or "absolute" or "fixed" or "sticky";

    /// <summary>Records an absolutely positioned or fixed box at its static position, to lay out once its containing block is known.</summary>
    private void Defer(Box box, double staticX, double staticY)
    {
        var pending = new Pending(box, staticX, staticY);
        if (box.Style.Keyword("position") == "fixed") _fixedPending.Add(pending);
        else _positioned.Peek().Add(pending);
    }

    /// <summary>Lays out a float as a shrink-to-fit block and places it beside earlier floats.</summary>
    private BoxFragment PlaceFloat(Box box, double areaLeft, double areaWidth, double top)
    {
        BoxFragment f = LayoutAtomic(box, areaWidth);
        double width = f.Width + f.Margin.Horizontal, height = f.Height + f.Margin.Vertical;
        bool rtl = box.Parent?.Style.Keyword("direction") == "rtl";
        bool left = box.Style.Keyword("float") switch
        {
            "left" => true,
            "inline-start" => !rtl,
            "inline-end" => rtl,
            _ => false,
        };
        (double x, double y) = _floats.Place(width, height, left, top, areaLeft, areaLeft + areaWidth);
        f.Translate(x + f.Margin.Left - f.X, y + f.Margin.Top - f.Y);
        return f;
    }

    /// <summary>The exclusions floats make for lines in a content box, or null when there are no floats.</summary>
    private Func<double, double, Exclusion>? Exclusions(double contentX, double contentWidth)
    {
        if (_floats.IsEmpty) return null;
        FloatContext floats = _floats;
        return (top, height) =>
        {
            (double l, double r, double next) = floats.Free(top, height, contentX, contentX + contentWidth);
            return new Exclusion(l - contentX, contentX + contentWidth - r, next);
        };
    }

    /// <summary>Lays out the boxes waiting for this containing block's padding box.</summary>
    private void LayoutPending(List<Pending> pending, BoxFragment container, double x, double y, double width, double height)
    {
        foreach (Pending p in pending)
        {
            BoxFragment f = LayoutAbsolute(p, x, y, width, height);

            // Negative z-index paints under the container's content, the rest over it.
            if (p.Box.Style.Number("z-index") < 0) container.Children.Insert(0, f);
            else container.Children.Add(f);
        }
    }

    /// <summary>An absolutely positioned box against a containing block (CSS 2.1, 10.3.7 and 10.6.4).</summary>
    private BoxFragment LayoutAbsolute(Pending p, double cbX, double cbY, double cbWidth, double cbHeight)
    {
        Box box = p.Box;
        ComputedStyle s = box.Style;
        Edges margin = BoxMetrics.Margins(s, cbWidth);
        Edges border = BoxMetrics.Borders(s);
        Edges padding = BoxMetrics.Paddings(s, cbWidth);
        double? left = BoxMetrics.IsAuto(s, "left") ? null : s.Length("left", cbWidth);
        double? right = BoxMetrics.IsAuto(s, "right") ? null : s.Length("right", cbWidth);
        double? top = BoxMetrics.IsAuto(s, "top") ? null : s.Length("top", cbHeight);
        double? bottom = BoxMetrics.IsAuto(s, "bottom") ? null : s.Length("bottom", cbHeight);
        double frame = border.Horizontal + padding.Horizontal;

        BoxFragment f;
        if (box is ReplacedBox replaced)
        {
            f = LayoutReplaced(replaced, cbWidth, cbHeight);
        }
        else
        {
            double? width = DefiniteSize(s, "width", cbWidth, border, padding, horizontal: true);
            if (width is null && left is double l && right is double r)
            {
                width = Math.Max(0, cbWidth - l - r - margin.Horizontal - frame);
            }
            else if (width is null)
            {
                (double min, double max) = ContentIntrinsic(box);
                double available = cbWidth - (left ?? 0) - (right ?? 0) - margin.Horizontal - frame;
                width = Math.Min(Math.Max(min, available), max);
            }

            f = LayoutBlockLevel(box, 0, cbWidth, 0, MarginCollapse.None, cbHeight, width).Fragment;
            if (BoxMetrics.IsAuto(s, "height") && top is double t && bottom is double b)
            {
                f.Height = Math.Max(f.Height, cbHeight - t - b - margin.Vertical);
            }
        }

        // Both horizontal margins auto with both insets and a width: the box is centred.
        if (left is double cl && right is double cr && BoxMetrics.IsAuto(s, "margin-left") && BoxMetrics.IsAuto(s, "margin-right"))
        {
            double free = Math.Max(0, cbWidth - cl - cr - f.Width) / 2;
            margin = margin with { Left = free, Right = free };
        }

        double x = left is double lx ? cbX + lx + margin.Left
            : right is double rx ? cbX + cbWidth - rx - margin.Right - f.Width
            : p.StaticX + margin.Left;
        double y = top is double ty ? cbY + ty + margin.Top
            : bottom is double by ? cbY + cbHeight - by - margin.Bottom - f.Height
            : p.StaticY + margin.Top;
        f.Margin = margin;
        f.Translate(x - f.X, y - f.Y);
        return f;
    }
}
