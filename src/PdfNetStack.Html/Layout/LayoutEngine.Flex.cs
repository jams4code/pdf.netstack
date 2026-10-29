using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Flex layout (CSS Flexible Box Layout 1): items sized from their flex basis, collected into
/// lines, flexed per section 9.7 with min and max violations frozen in turn, then placed with
/// auto margins, justify-content, align-items, align-self and align-content.
/// </summary>
internal sealed partial class LayoutEngine
{
    private sealed class FlexItem(Box box)
    {
        public Box Box { get; } = box;

        public Edges Margin { get; set; }

        public Edges Border { get; set; }

        public Edges Padding { get; set; }

        public bool AutoStart { get; set; }

        public bool AutoEnd { get; set; }

        public bool AutoCrossStart { get; set; }

        public bool AutoCrossEnd { get; set; }

        public double Grow { get; set; }

        public double Shrink { get; set; }

        /// <summary>The flex base size: the content box along the main axis.</summary>
        public double Basis { get; set; }

        public double Min { get; set; }

        public double Max { get; set; } = double.PositiveInfinity;

        /// <summary>The base size clamped by min and max.</summary>
        public double Hypothetical { get; set; }

        /// <summary>The flexed content box size along the main axis.</summary>
        public double Main { get; set; }

        public bool Frozen { get; set; }

        /// <summary>Margins, borders and padding along the main axis.</summary>
        public double MainExtra { get; set; }

        public BoxFragment Fragment { get; set; } = null!;

        public double OuterMain => Main + MainExtra;
    }

    private static bool IsRow(ComputedStyle s) => s.Keyword("flex-direction") is "row" or "row-reverse" or "";

    private static double Gap(ComputedStyle s, string property, double basis) =>
        s.Keyword(property) == "normal" ? 0 : Math.Max(0, s.Length(property, basis) ?? 0);

    private List<FlexItem> FlexItems(FlexBox box, double contentWidth, bool row)
    {
        var items = new List<FlexItem>();
        foreach (Box child in box.Children.OrderBy(c => (int)c.Style.Number("order")))
        {
            ComputedStyle s = child.Style;
            var item = new FlexItem(child)
            {
                Margin = BoxMetrics.Margins(s, contentWidth),
                Border = BoxMetrics.Borders(s),
                Padding = BoxMetrics.Paddings(s, contentWidth),
                Grow = Math.Max(0, s.Number("flex-grow")),
                Shrink = Math.Max(0, s.Number("flex-shrink")),
                AutoStart = BoxMetrics.IsAuto(s, row ? "margin-left" : "margin-top"),
                AutoEnd = BoxMetrics.IsAuto(s, row ? "margin-right" : "margin-bottom"),
                AutoCrossStart = BoxMetrics.IsAuto(s, row ? "margin-top" : "margin-left"),
                AutoCrossEnd = BoxMetrics.IsAuto(s, row ? "margin-bottom" : "margin-right"),
            };
            item.MainExtra = row
                ? item.Margin.Horizontal + item.Border.Horizontal + item.Padding.Horizontal
                : item.Margin.Vertical + item.Border.Vertical + item.Padding.Vertical;
            items.Add(item);
        }

        return items;
    }

    /// <summary>A size property along an axis as a content-box length, or null when it is auto or not definite.</summary>
    private static double? DefiniteSize(ComputedStyle s, string property, double? basis, Edges border, Edges padding, bool horizontal)
    {
        if (BoxMetrics.IsAuto(s, property) || s.Keyword(property) is "none" or "content" or "min-content" or "max-content" or "fit-content") return null;
        if (HasPercent(s, property) && basis is null) return null;
        if (s.Length(property, basis ?? 0) is not double value) return null;
        double frame = horizontal ? border.Horizontal + padding.Horizontal : border.Vertical + padding.Vertical;
        return s.Keyword("box-sizing") == "border-box" ? Math.Max(0, value - frame) : value;
    }

    /// <summary>Resolves flexible lengths on one line (CSS Flexbox 1, 9.7).</summary>
    private static void ResolveFlexibleLengths(List<FlexItem> line, double available)
    {
        double used = line.Sum(i => i.Hypothetical + i.MainExtra);
        bool grow = used < available;
        foreach (FlexItem item in line)
        {
            item.Main = item.Hypothetical;
            double factor = grow ? item.Grow : item.Shrink;
            item.Frozen = factor == 0 || (grow && item.Basis > item.Hypothetical) || (!grow && item.Basis < item.Hypothetical);
        }

        double initialFree = available - line.Sum(i => (i.Frozen ? i.Main : i.Basis) + i.MainExtra);
        for (int round = 0; round <= line.Count && line.Any(i => !i.Frozen); round++)
        {
            List<FlexItem> open = line.Where(i => !i.Frozen).ToList();
            double free = available - line.Sum(i => (i.Frozen ? i.Main : i.Basis) + i.MainExtra);
            double sumGrow = open.Sum(i => i.Grow);
            if (grow && sumGrow < 1) free = Math.Min(free, initialFree * sumGrow);
            double sumShrink = open.Sum(i => i.Shrink * i.Basis);
            foreach (FlexItem item in open)
            {
                item.Main = grow
                    ? item.Basis + (sumGrow > 0 ? free * item.Grow / sumGrow : 0)
                    : item.Basis + (sumShrink > 0 ? free * item.Shrink * item.Basis / sumShrink : 0);
            }

            // Clamp, then freeze the violators in the direction of the total violation.
            double total = 0;
            var violation = new Dictionary<FlexItem, double>();
            foreach (FlexItem item in open)
            {
                double clamped = Math.Max(item.Min, Math.Min(item.Max, Math.Max(0, item.Main)));
                violation[item] = clamped - item.Main;
                total += clamped - item.Main;
                item.Main = clamped;
            }

            foreach (FlexItem item in open)
            {
                if (total == 0 || (total > 0 && violation[item] > 0) || (total < 0 && violation[item] < 0)) item.Frozen = true;
            }
        }
    }

    /// <summary>The start offset and the space between items for justify-content, once auto margins have taken their share.</summary>
    private static (double Start, double Between) Justify(string justify, double free, int count)
    {
        if (free <= 0)
        {
            // Overflow aligns to the start, except centre and end which stay true to their side.
            return justify switch
            {
                "center" => (free / 2, 0),
                "flex-end" or "end" or "right" => (free, 0),
                _ => (0, 0),
            };
        }

        return justify switch
        {
            "flex-end" or "end" or "right" => (free, 0),
            "center" => (free / 2, 0),
            "space-between" => count > 1 ? (0, free / (count - 1)) : (0, 0),
            "space-around" => (free / count / 2, free / count),
            "space-evenly" => (free / (count + 1), free / (count + 1)),
            _ => (0, 0),
        };
    }

    private static string AlignSelf(FlexItem item, ComputedStyle container)
    {
        string self = item.Box.Style.Keyword("align-self");
        string align = self is "auto" or "" ? container.Keyword("align-items") : self;
        return align switch
        {
            "normal" or "" => "stretch",
            "start" or "self-start" or "flex-start" => "start",
            "end" or "self-end" or "flex-end" => "end",
            "first" => "baseline",
            _ => align,
        };
    }

    private Placed LayoutFlex(FlexBox box, double cbX, double cbWidth, double cursorY, MarginCollapse pending, double? cbHeight, double? forcedWidth)
    {
        ComputedStyle s = box.Style;
        Edges margin = BoxMetrics.Margins(s, cbWidth);
        Edges border = BoxMetrics.Borders(s);
        Edges padding = BoxMetrics.Paddings(s, cbWidth);
        double contentWidth = forcedWidth ?? ResolveWidth(box, cbWidth, ref margin, border, padding);
        double? ownHeight = SpecifiedHeight(s, cbHeight, border, padding);
        double y = cursorY + (pending + margin.Top).Value;
        double contentX = cbX + margin.Left + border.Left + padding.Left;
        double contentTop = y + border.Top + padding.Top;
        bool row = IsRow(s);

        var fragment = new BoxFragment(box) { X = cbX + margin.Left, Y = y, Border = border, Padding = padding, Margin = margin };
        double contentHeight = row
            ? LayoutFlexRow(box, fragment, contentX, contentTop, contentWidth, ownHeight)
            : LayoutFlexColumn(box, fragment, contentX, contentTop, contentWidth, ownHeight);

        contentHeight = ClampHeight(s, ownHeight ?? contentHeight, cbHeight, border, padding);
        fragment.Width = contentWidth + border.Horizontal + padding.Horizontal;
        fragment.Height = contentHeight + border.Vertical + padding.Vertical;
        fragment.FirstBaseline = fragment.Children.OfType<BoxFragment>().Select(c => c.FirstBaseline).FirstOrDefault(b => b is not null);
        fragment.LastBaseline = fragment.FirstBaseline;
        Offset(fragment, s, cbWidth, cbHeight);
        return new Placed(fragment, MarginCollapse.None + margin.Bottom, false);
    }

    /// <summary>Lays out the item at the origin with a content width; returns its border box fragment.</summary>
    private BoxFragment LayoutItem(FlexItem item, double containerWidth, double? containerHeight, double contentWidth)
    {
        BoxFragment f = LayoutBlockLevel(item.Box, 0, containerWidth, 0, MarginCollapse.None, containerHeight, Math.Max(0, contentWidth)).Fragment;
        f.Margin = item.Margin;
        return f;
    }

    private double LayoutFlexRow(FlexBox box, BoxFragment container, double contentX, double contentTop, double width, double? height)
    {
        ComputedStyle s = box.Style;
        List<FlexItem> items = FlexItems(box, width, row: true);
        double columnGap = Gap(s, "column-gap", width), rowGap = Gap(s, "row-gap", height ?? 0);
        bool wrap = s.Keyword("flex-wrap") != "nowrap";

        foreach (FlexItem item in items)
        {
            ComputedStyle cs = item.Box.Style;
            (double minContent, double maxContent) = ContentIntrinsic(item.Box);
            double? specifiedWidth = DefiniteSize(cs, "width", width, item.Border, item.Padding, horizontal: true);
            item.Basis = cs.Keyword("flex-basis") switch
            {
                "auto" => specifiedWidth ?? maxContent,
                "content" or "max-content" => maxContent,
                "min-content" => minContent,
                _ => DefiniteSize(cs, "flex-basis", width, item.Border, item.Padding, horizontal: true) ?? specifiedWidth ?? maxContent,
            };
            if (item.Box is ReplacedBox image)
            {
                BoxFragment natural = LayoutReplaced(image, width, height);
                item.Basis = natural.ContentWidth;
            }

            // The automatic minimum: no narrower than the content, unless a smaller width is asked for.
            item.Min = cs.Keyword("min-width") == "auto" && cs.Keyword("overflow-x") is "visible" or "clip"
                ? Math.Min(minContent, specifiedWidth ?? double.PositiveInfinity)
                : DefiniteSize(cs, "min-width", width, item.Border, item.Padding, horizontal: true) ?? 0;
            item.Max = DefiniteSize(cs, "max-width", width, item.Border, item.Padding, horizontal: true) ?? double.PositiveInfinity;
            item.Hypothetical = Math.Max(item.Min, Math.Min(item.Max, item.Basis));
        }

        // Lines: as many items as fit, at least one.
        var lines = new List<List<FlexItem>>();
        foreach (FlexItem item in items)
        {
            if (lines.Count == 0)
            {
                lines.Add([]);
            }
            else if (wrap)
            {
                List<FlexItem> current = lines[^1];
                double used = current.Sum(i => i.Hypothetical + i.MainExtra) + (columnGap * current.Count);
                if (used + item.Hypothetical + item.MainExtra > width + 1e-9) lines.Add([]);
            }

            lines[^1].Add(item);
        }

        // Flex each line, then lay out its items at their widths to learn their heights.
        var crossSizes = new List<double>();
        foreach (List<FlexItem> line in lines)
        {
            ResolveFlexibleLengths(line, width - (columnGap * (line.Count - 1)));
            foreach (FlexItem item in line) item.Fragment = LayoutItem(item, width, height, item.Main);
            crossSizes.Add(line.Count == 0 ? 0 : line.Max(i => i.Fragment.Height + i.Margin.Vertical));
        }

        // A single line in a container of definite height takes all of it.
        if (lines.Count == 1 && height is double h) crossSizes[0] = h;

        // align-content shares the leftover height between lines.
        double linesHeight = crossSizes.Sum() + (rowGap * Math.Max(0, lines.Count - 1));
        double crossFree = height is double definite ? definite - linesHeight : 0;
        string alignContent = s.Keyword("align-content");
        double lineStart = 0, lineBetween = 0;
        if (lines.Count > 1 && crossFree > 0)
        {
            if (alignContent is "normal" or "stretch" or "")
            {
                for (int k = 0; k < crossSizes.Count; k++) crossSizes[k] += crossFree / crossSizes.Count;
            }
            else
            {
                (lineStart, lineBetween) = Justify(alignContent, crossFree, lines.Count);
            }
        }

        bool reverse = s.Keyword("flex-direction") == "row-reverse" ^ s.Keyword("direction") == "rtl";
        bool wrapReverse = s.Keyword("flex-wrap") == "wrap-reverse";
        double lineTop = contentTop + lineStart;
        var order = Enumerable.Range(0, lines.Count).ToList();
        if (wrapReverse) order.Reverse();
        var lineTops = new double[lines.Count];
        foreach (int k in order)
        {
            lineTops[k] = lineTop;
            lineTop += crossSizes[k] + rowGap + lineBetween;
        }

        for (int k = 0; k < lines.Count; k++)
        {
            List<FlexItem> line = lines[k];
            PlaceMain(line, s, contentX, width, columnGap, reverse, horizontal: true);
            PlaceCross(line, s, lineTops[k], crossSizes[k], horizontal: true);
            foreach (FlexItem item in line) container.Children.Add(item.Fragment);
        }

        return height ?? linesHeight;
    }

    private double LayoutFlexColumn(FlexBox box, BoxFragment container, double contentX, double contentTop, double width, double? height)
    {
        ComputedStyle s = box.Style;
        List<FlexItem> items = FlexItems(box, width, row: false);
        double rowGap = Gap(s, "row-gap", height ?? 0);

        foreach (FlexItem item in items)
        {
            ComputedStyle cs = item.Box.Style;
            string align = AlignSelf(item, s);

            // The cross size: stretched to the container, a width, or shrink to fit.
            double frame = item.Border.Horizontal + item.Padding.Horizontal;
            double? specifiedWidth = DefiniteSize(cs, "width", width, item.Border, item.Padding, horizontal: true);
            double itemWidth;
            if (specifiedWidth is double sw) itemWidth = sw;
            else if (align == "stretch" && !item.AutoCrossStart && !item.AutoCrossEnd) itemWidth = width - item.Margin.Horizontal - frame;
            else
            {
                (double minContent, double maxContent) = ContentIntrinsic(item.Box);
                itemWidth = Math.Min(Math.Max(minContent, width - item.Margin.Horizontal - frame), maxContent);
            }

            item.Fragment = LayoutItem(item, width, height, itemWidth);
            double natural = item.Fragment.ContentHeight;
            double? specifiedHeight = DefiniteSize(cs, "height", height, item.Border, item.Padding, horizontal: false);
            item.Basis = cs.Keyword("flex-basis") switch
            {
                "auto" => specifiedHeight ?? natural,
                "content" or "max-content" or "min-content" => natural,
                _ => DefiniteSize(cs, "flex-basis", height, item.Border, item.Padding, horizontal: false) ?? specifiedHeight ?? natural,
            };
            item.Min = cs.Keyword("min-height") == "auto" && cs.Keyword("overflow-y") is "visible" or "clip"
                ? Math.Min(natural, specifiedHeight ?? double.PositiveInfinity)
                : DefiniteSize(cs, "min-height", height, item.Border, item.Padding, horizontal: false) ?? 0;
            item.Max = DefiniteSize(cs, "max-height", height, item.Border, item.Padding, horizontal: false) ?? double.PositiveInfinity;
            item.Hypothetical = Math.Max(item.Min, Math.Min(item.Max, item.Basis));
            item.Main = item.Hypothetical;
        }

        // Without a definite height there is nothing to flex into: items keep their sizes.
        if (height is double available) ResolveFlexibleLengths(items, available - (rowGap * Math.Max(0, items.Count - 1)));
        foreach (FlexItem item in items) item.Fragment.Height = item.Main + item.Border.Vertical + item.Padding.Vertical;

        bool reverse = s.Keyword("flex-direction") == "column-reverse";
        double used = items.Sum(i => i.OuterMain) + (rowGap * Math.Max(0, items.Count - 1));
        double contentHeight = height ?? used;
        PlaceMain(items, s, contentTop, contentHeight, rowGap, reverse, horizontal: false);
        PlaceCross(items, s, contentX, width, horizontal: false);
        foreach (FlexItem item in items) container.Children.Add(item.Fragment);
        return contentHeight;
    }

    /// <summary>Places a line's items along the main axis: auto margins first, then justify-content.</summary>
    private static void PlaceMain(List<FlexItem> line, ComputedStyle container, double start, double size, double gap, bool reverse, bool horizontal)
    {
        if (line.Count == 0) return;
        double free = size - line.Sum(i => i.OuterMain) - (gap * (line.Count - 1));
        int autos = line.Sum(i => (i.AutoStart ? 1 : 0) + (i.AutoEnd ? 1 : 0));
        double autoShare = free > 0 && autos > 0 ? free / autos : 0;
        (double offset, double between) = autos > 0 && free > 0 ? (0, 0) : Justify(container.Keyword("justify-content"), free, line.Count);

        // Positions run from main-start, which is the right or bottom edge when the axis is reversed.
        double cursor = offset;
        foreach (FlexItem item in line)
        {
            double physicalStart = horizontal ? item.Margin.Left : item.Margin.Top;
            double physicalEnd = horizontal ? item.Margin.Right : item.Margin.Bottom;
            double leading = (reverse ? physicalEnd : physicalStart) + ((reverse ? item.AutoEnd : item.AutoStart) ? autoShare : 0);
            double trailing = (reverse ? physicalStart : physicalEnd) + ((reverse ? item.AutoStart : item.AutoEnd) ? autoShare : 0);
            double borderBox = item.Main + (horizontal ? item.Border.Horizontal + item.Padding.Horizontal : item.Border.Vertical + item.Padding.Vertical);
            double fromStart = cursor + leading;
            double target = reverse ? start + size - fromStart - borderBox : start + fromStart;
            BoxFragment f = item.Fragment;
            if (horizontal) f.Translate(target - f.X, 0);
            else f.Translate(0, target - f.Y);
            cursor += leading + borderBox + trailing + gap + between;
        }
    }

    /// <summary>Places a line's items across it: align-self, auto margins, stretching and baselines.</summary>
    private static void PlaceCross(List<FlexItem> line, ComputedStyle container, double start, double size, bool horizontal)
    {
        double baseline = 0;
        foreach (FlexItem item in line.Where(i => horizontal && AlignSelf(i, container) == "baseline"))
        {
            BoxFragment f = item.Fragment;
            baseline = Math.Max(baseline, item.Margin.Top + ((f.FirstBaseline ?? f.Bottom) - f.Y));
        }

        foreach (FlexItem item in line)
        {
            BoxFragment f = item.Fragment;
            ComputedStyle cs = item.Box.Style;
            string align = AlignSelf(item, container);
            double marginStart = horizontal ? item.Margin.Top : item.Margin.Left;
            double marginEnd = horizontal ? item.Margin.Bottom : item.Margin.Right;

            // Stretch: an auto cross size fills the line.
            bool autoCross = BoxMetrics.IsAuto(cs, horizontal ? "height" : "width");
            if (horizontal && align == "stretch" && autoCross && !item.AutoCrossStart && !item.AutoCrossEnd)
            {
                double stretched = size - marginStart - marginEnd;
                if (stretched > f.Height) f.Height = stretched;
            }

            double outer = (horizontal ? f.Height : f.Width) + marginStart + marginEnd;
            double free = size - outer;
            double offset;
            if (item.AutoCrossStart || item.AutoCrossEnd)
            {
                offset = item.AutoCrossStart && item.AutoCrossEnd ? Math.Max(0, free) / 2 : item.AutoCrossStart ? Math.Max(0, free) : 0;
            }
            else
            {
                offset = align switch
                {
                    "end" => free,
                    "center" => free / 2,
                    "baseline" when horizontal => baseline - (item.Margin.Top + ((f.FirstBaseline ?? f.Bottom) - f.Y)),
                    _ => 0,
                };
            }

            double target = start + offset + marginStart;
            if (horizontal) f.Translate(0, target - f.Y);
            else f.Translate(target - f.X, 0);
        }
    }

    /// <summary>Intrinsic widths of a flex container's content box.</summary>
    private (double Min, double Max) FlexIntrinsic(FlexBox box)
    {
        ComputedStyle s = box.Style;
        var contributions = box.Children.Select(Intrinsic).ToList();
        if (contributions.Count == 0) return (0, 0);
        if (!IsRow(s)) return (contributions.Max(c => c.Min), contributions.Max(c => c.Max));
        double gap = Gap(s, "column-gap", 0) * (contributions.Count - 1);
        double max = contributions.Sum(c => c.Max) + gap;
        double min = s.Keyword("flex-wrap") == "nowrap" ? contributions.Sum(c => c.Min) + gap : contributions.Max(c => c.Min);
        return (min, max);
    }
}
