using System.Globalization;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Grid layout (CSS Grid Layout 1): explicit tracks from the templates (lengths, percentages,
/// fr, auto, min-content, max-content, minmax(), repeat() with counts and auto-fill),
/// implicit tracks from grid-auto-rows and grid-auto-columns, placement by lines, spans and
/// named areas, row-major auto-placement, then track sizing and alignment.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Hostile templates and spans must not claim unbounded tracks.
    private const int MaxTracks = 1000;

    private enum TrackKind
    {
        Fixed,
        Auto,
        MinContent,
        MaxContent,
        Flex,
    }

    /// <summary>A track's sizing function: a minimum and a maximum, each fixed, content based or flexible.</summary>
    private readonly record struct TrackSize(TrackKind MinKind, double Min, TrackKind MaxKind, double Max)
    {
        public static TrackSize Auto => new(TrackKind.Auto, 0, TrackKind.Auto, 0);

        public bool IsFlex => MaxKind == TrackKind.Flex;
    }

    private sealed class GridItem(Box box)
    {
        public Box Box { get; } = box;

        public int Row { get; set; } = -1;

        public int Column { get; set; } = -1;

        public int RowSpan { get; set; } = 1;

        public int ColumnSpan { get; set; } = 1;

        public BoxFragment Fragment { get; set; } = null!;
    }

    /// <summary>One track size from a value: a length, a percentage of <paramref name="basis"/>, fr, a keyword or minmax().</summary>
    private static TrackSize? ParseTrack(CssValue v, double? basis)
    {
        if (v.Function == "minmax")
        {
            List<CssValue> args = v.Children.Where(c => !c.IsWhitespace && !(c.IsToken && c.Token.Type == CssTokenType.Comma)).ToList();
            if (args.Count != 2 || ParseTrack(args[0], basis) is not TrackSize min || ParseTrack(args[1], basis) is not TrackSize max) return null;
            return new TrackSize(min.MinKind == TrackKind.Flex ? TrackKind.Auto : min.MinKind, min.Min, max.MaxKind, max.Max);
        }

        if (v.Function == "fit-content")
        {
            double limit = v.Children.Select(c => Px(c, basis)).FirstOrDefault(x => x is not null) ?? double.PositiveInfinity;
            return new TrackSize(TrackKind.Auto, 0, TrackKind.Fixed, limit);
        }

        if (v.IsToken && v.Token.Type == CssTokenType.Dimension && v.Token.Unit == "fr") return new TrackSize(TrackKind.Auto, 0, TrackKind.Flex, Math.Max(0, v.Token.Number));
        if (v.IsIdent("auto")) return TrackSize.Auto;
        if (v.IsIdent("min-content")) return new TrackSize(TrackKind.MinContent, 0, TrackKind.MinContent, 0);
        if (v.IsIdent("max-content")) return new TrackSize(TrackKind.MaxContent, 0, TrackKind.MaxContent, 0);
        if (v.IsToken && v.Token.Type == CssTokenType.Percentage && basis is null) return TrackSize.Auto;
        return Px(v, basis) is double px ? new TrackSize(TrackKind.Fixed, Math.Max(0, px), TrackKind.Fixed, Math.Max(0, px)) : null;
    }

    private static double? Px(CssValue v, double? basis)
    {
        if (CssGrammar.IsMath(v)) return CssMath.Evaluate(v, basis ?? 0, out _);
        if (!v.IsToken) return null;
        return v.Token.Type switch
        {
            CssTokenType.Dimension when v.Token.Unit == "px" => v.Token.Number,
            CssTokenType.Number when v.Token.Number == 0 => 0,
            CssTokenType.Percentage when basis is double b => v.Token.Number / 100 * b,
            _ => null,
        };
    }

    /// <summary>A track list (CSS Grid 1, 7.2): sizes and repeat(); line names are skipped.</summary>
    private static List<TrackSize> ParseTracks(List<CssValue> value, double? basis, double gap)
    {
        var tracks = new List<TrackSize>();
        foreach (CssValue v in value)
        {
            if (v.IsWhitespace || v.Block == CssTokenType.LeftBracket || v.IsIdent("none")) continue;
            if (v.Function == "repeat")
            {
                int comma = v.Children.FindIndex(c => c.IsToken && c.Token.Type == CssTokenType.Comma);
                if (comma < 0) continue;
                CssValue? count = v.Children.Take(comma).FirstOrDefault(c => !c.IsWhitespace);
                List<TrackSize> unit = ParseTracks(v.Children.Skip(comma + 1).ToList(), basis, gap);
                if (count is null || unit.Count == 0) continue;
                int times;
                if (count.IsIdent("auto-fill") || count.IsIdent("auto-fit"))
                {
                    // As many repetitions as fit, by each track's fixed size (CSS Grid 1, 7.2.3.2).
                    double size = unit.Sum(t => t.MaxKind == TrackKind.Fixed ? t.Max : t.MinKind == TrackKind.Fixed ? t.Min : 0) + (gap * unit.Count);
                    times = basis is double b && size > 0 ? Math.Max(1, (int)Math.Floor((b + gap) / size)) : 1;
                }
                else
                {
                    times = count.IsToken && count.Token.Type == CssTokenType.Number ? (int)Math.Max(1, Math.Min(MaxTracks, count.Token.Number)) : 1;
                }

                for (int k = 0; k < times && tracks.Count < MaxTracks; k++) tracks.AddRange(unit);
                continue;
            }

            if (ParseTrack(v, basis) is TrackSize track && tracks.Count < MaxTracks) tracks.Add(track);
        }

        return tracks;
    }

    /// <summary>Named areas (CSS Grid 1, 7.3): each name's rows and columns, kept only when they form a rectangle.</summary>
    private static Dictionary<string, (int Row, int Column, int RowEnd, int ColumnEnd)> ParseAreas(List<CssValue> value)
    {
        var cells = new Dictionary<string, List<(int Row, int Column)>>(StringComparer.Ordinal);
        int row = 0;
        foreach (CssValue v in value)
        {
            if (!v.IsToken || v.Token.Type != CssTokenType.String) continue;
            string[] names = v.Token.Value.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int c = 0; c < names.Length && c < MaxTracks; c++)
            {
                if (names[c].Trim('.').Length == 0) continue;
                if (!cells.TryGetValue(names[c], out var list)) cells[names[c]] = list = [];
                list.Add((row, c));
            }

            if (++row >= MaxTracks) break;
        }

        var areas = new Dictionary<string, (int, int, int, int)>(StringComparer.Ordinal);
        foreach (var named in cells)
        {
            int r0 = named.Value.Min(p => p.Row), r1 = named.Value.Max(p => p.Row) + 1;
            int c0 = named.Value.Min(p => p.Column), c1 = named.Value.Max(p => p.Column) + 1;
            if (named.Value.Count == (r1 - r0) * (c1 - c0)) areas[named.Key] = (r0, c0, r1, c1);
        }

        return areas;
    }

    /// <summary>A placement property: a line number (negative from the end), span n, an area name, or auto.</summary>
    private static (int? Line, int Span, string? Name) Placement(List<CssValue> value)
    {
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        bool span = items.Any(v => v.IsIdent("span"));
        CssValue? number = items.FirstOrDefault(v => v.IsToken && v.Token is { Type: CssTokenType.Number, IsInteger: true });
        CssValue? name = items.FirstOrDefault(v => v.IsToken && v.Token.Type == CssTokenType.Ident && !v.IsIdent("span") && !v.IsIdent("auto"));
        int n = number is null ? 1 : (int)Math.Max(-MaxTracks, Math.Min(MaxTracks, number.Token.Number));
        if (span) return (null, Math.Max(1, n), null);
        if (number is not null && n != 0) return (n, 1, null);
        return (null, 1, name?.Token.Value);
    }

    /// <summary>Resolves an item's start and span on one axis, or (-1, span) when it is placed automatically.</summary>
    private static (int Start, int Span) Resolve(List<CssValue> startValue, List<CssValue> endValue, int explicitTracks, Dictionary<string, (int Row, int Column, int RowEnd, int ColumnEnd)> areas, bool rows)
    {
        var start = Placement(startValue);
        var end = Placement(endValue);
        int? Line(int? line) => line is int l ? (l > 0 ? l - 1 : Math.Max(0, explicitTracks + 1 + l)) : null;

        int? s = Line(start.Line), e = Line(end.Line);
        if (start.Name is string a && areas.TryGetValue(a, out var area)) s = rows ? area.Row : area.Column;
        if (end.Name is string b && areas.TryGetValue(b, out var areaEnd)) e = rows ? areaEnd.RowEnd : areaEnd.ColumnEnd;

        if (s is int si && e is int ei) return ei == si ? (si, 1) : (Math.Min(si, ei), Math.Min(MaxTracks, Math.Abs(ei - si)));
        if (s is int only) return (only, end.Span);
        if (e is int endOnly) return (Math.Max(0, endOnly - start.Span), start.Span);
        return (-1, Math.Min(MaxTracks, Math.Max(start.Span, end.Span)));
    }

    private List<GridItem> PlaceItems(GridBox box, List<TrackSize> columns, List<TrackSize> rows, out int columnCount, out int rowCount)
    {
        int columnTotal;
        ComputedStyle s = box.Style;
        Dictionary<string, (int Row, int Column, int RowEnd, int ColumnEnd)> areas = ParseAreas(s["grid-template-areas"]);
        int explicitColumns = Math.Max(columns.Count, areas.Count == 0 ? 0 : areas.Values.Max(a => a.ColumnEnd));
        int explicitRows = Math.Max(rows.Count, areas.Count == 0 ? 0 : areas.Values.Max(a => a.RowEnd));
        var items = new List<GridItem>();
        foreach (Box child in box.Children.Where(c => !IsOutOfFlow(c)).OrderBy(c => (int)c.Style.Number("order")))
        {
            ComputedStyle cs = child.Style;
            (int row, int rowSpan) = Resolve(cs["grid-row-start"], cs["grid-row-end"], explicitRows, areas, rows: true);
            (int column, int columnSpan) = Resolve(cs["grid-column-start"], cs["grid-column-end"], explicitColumns, areas, rows: false);
            items.Add(new GridItem(child) { Row = row, Column = column, RowSpan = rowSpan, ColumnSpan = columnSpan });
        }

        columnTotal = Math.Min(MaxTracks, Math.Max(1, Math.Max(explicitColumns, items.Count == 0 ? 0 : items.Max(i => i.Column < 0 ? i.ColumnSpan : i.Column + i.ColumnSpan))));
        var occupied = new HashSet<long>();
        void Occupy(GridItem item)
        {
            for (int r = item.Row; r < item.Row + item.RowSpan; r++)
            {
                for (int c = item.Column; c < item.Column + item.ColumnSpan && occupied.Count < MaxGridSlots; c++) occupied.Add(Key(r, c));
            }
        }

        bool Free(int row, int column, int rowSpan, int columnSpan)
        {
            if (column + columnSpan > columnTotal) return false;
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = column; c < column + columnSpan; c++)
                {
                    if (occupied.Contains(Key(r, c))) return false;
                }
            }

            return true;
        }

        // Definite positions first, then items locked to a row, then the rest in row-major order (CSS Grid 1, 8.5).
        foreach (GridItem item in items.Where(i => i.Row >= 0 && i.Column >= 0)) Occupy(item);
        foreach (GridItem item in items.Where(i => i.Row >= 0 && i.Column < 0))
        {
            item.ColumnSpan = Math.Min(item.ColumnSpan, columnTotal);
            int c = 0;
            while (c + item.ColumnSpan <= columnTotal && !Free(item.Row, c, item.RowSpan, item.ColumnSpan)) c++;
            item.Column = c + item.ColumnSpan <= columnTotal ? c : 0;
            Occupy(item);
        }

        int cursorRow = 0, cursorColumn = 0;
        foreach (GridItem item in items.Where(i => i.Row < 0))
        {
            if (item.Column >= 0)
            {
                // A definite column: the first row at or after the cursor where it fits.
                if (item.Column < cursorColumn) cursorRow++;
                while (!Free(cursorRow, item.Column, item.RowSpan, item.ColumnSpan) && cursorRow < MaxTracks * 4) cursorRow++;
                item.Row = cursorRow;
                cursorColumn = item.Column + item.ColumnSpan;
            }
            else
            {
                item.ColumnSpan = Math.Min(item.ColumnSpan, columnTotal);
                while (!Free(cursorRow, cursorColumn, item.RowSpan, item.ColumnSpan) && cursorRow < MaxTracks * 4)
                {
                    cursorColumn++;
                    if (cursorColumn + item.ColumnSpan > columnTotal)
                    {
                        cursorColumn = 0;
                        cursorRow++;
                    }
                }

                item.Row = cursorRow;
                item.Column = cursorColumn;
                cursorColumn += item.ColumnSpan;
            }

            Occupy(item);
        }

        columnCount = columnTotal;
        rowCount = Math.Min(MaxTracks * 4, Math.Max(explicitRows, items.Count == 0 ? 0 : items.Max(i => i.Row + i.RowSpan)));
        return items;
    }

    /// <summary>Implicit tracks repeat grid-auto-rows or grid-auto-columns past the explicit ones.</summary>
    private static List<TrackSize> WithImplicit(List<TrackSize> tracks, int count, List<CssValue> auto, double? basis)
    {
        List<TrackSize> pattern = ParseTracks(auto, basis, 0);
        if (pattern.Count == 0) pattern = [TrackSize.Auto];
        var result = new List<TrackSize>(tracks.Take(count));
        for (int k = 0; result.Count < count; k++) result.Add(pattern[k % pattern.Count]);
        return result;
    }

    /// <summary>
    /// Track sizes (CSS Grid 1, 11.4 to 11.7, simplified): base sizes from fixed minimums and the
    /// items' content, growth toward the maximums, the leftover shared by fr, then auto tracks
    /// stretched to fill.
    /// </summary>
    private static double[] SizeTracks(List<TrackSize> tracks, IEnumerable<(int Start, int Span, double Min, double Max)> contributions, double? available, double gap)
    {
        int n = tracks.Count;
        var baseSize = new double[n];
        var limit = new double[n];
        for (int k = 0; k < n; k++)
        {
            baseSize[k] = tracks[k].MinKind == TrackKind.Fixed ? tracks[k].Min : 0;
            limit[k] = tracks[k].MaxKind == TrackKind.Fixed ? Math.Max(baseSize[k], tracks[k].Max) : tracks[k].IsFlex ? double.PositiveInfinity : 0;
        }

        // Content contributions, single spans first, then spanning items over their non-fixed tracks.
        foreach ((int start, int span, double min, double max) in contributions.OrderBy(c => c.Span))
        {
            int end = Math.Min(n, start + span);
            if (start >= end) continue;
            double gaps = gap * (end - start - 1);
            int[] flexible = Enumerable.Range(start, end - start).Where(k => tracks[k].MinKind != TrackKind.Fixed).ToArray();
            if (flexible.Length == 0) continue;
            double needMin = min - gaps - Enumerable.Range(start, end - start).Sum(k => baseSize[k]);
            if (needMin > 0)
            {
                foreach (int k in flexible) baseSize[k] += needMin / flexible.Length;
            }

            foreach (int k in flexible.Where(k => !tracks[k].IsFlex && tracks[k].MaxKind != TrackKind.Fixed))
            {
                double share = (tracks[k].MaxKind == TrackKind.MinContent ? min : max - gaps) / (span == 1 ? 1 : flexible.Length);
                limit[k] = Math.Max(limit[k], share);
            }
        }

        for (int k = 0; k < n; k++)
        {
            if (!tracks[k].IsFlex) limit[k] = Math.Max(limit[k], baseSize[k]);
        }

        double free = (available ?? double.PositiveInfinity) - baseSize.Sum() - (gap * Math.Max(0, n - 1));
        if (available is null)
        {
            // Without a definite size every track takes its maximum, and fr acts as auto.
            for (int k = 0; k < n; k++) baseSize[k] = tracks[k].IsFlex ? baseSize[k] : limit[k];
            return baseSize;
        }

        // Grow non-flexible tracks toward their limits, equally while there is room.
        int[] growable = Enumerable.Range(0, n).Where(k => !tracks[k].IsFlex && limit[k] > baseSize[k]).ToArray();
        while (free > 1e-9 && growable.Length > 0)
        {
            double share = free / growable.Length;
            double step = growable.Min(k => limit[k] - baseSize[k]);
            double take = Math.Min(share, step);
            foreach (int k in growable) baseSize[k] += take;
            free -= take * growable.Length;
            growable = growable.Where(k => limit[k] - baseSize[k] > 1e-9).ToArray();
        }

        // The rest goes to fr tracks; a track whose base exceeds its share keeps its base.
        int[] flex = Enumerable.Range(0, n).Where(k => tracks[k].IsFlex).ToArray();
        if (flex.Length > 0 && free > 0)
        {
            var frozen = new HashSet<int>();
            for (int round = 0; round <= flex.Length; round++)
            {
                double space = available.Value - (gap * Math.Max(0, n - 1)) - Enumerable.Range(0, n).Where(k => !tracks[k].IsFlex || frozen.Contains(k)).Sum(k => baseSize[k]);
                double factors = flex.Where(k => !frozen.Contains(k)).Sum(k => tracks[k].Max);
                if (factors <= 0) break;
                double unit = Math.Max(0, space) / Math.Max(1, factors);
                int[] newlyFrozen = flex.Where(k => !frozen.Contains(k) && baseSize[k] > tracks[k].Max * unit).ToArray();
                if (newlyFrozen.Length == 0)
                {
                    foreach (int k in flex.Where(k => !frozen.Contains(k))) baseSize[k] = tracks[k].Max * unit;
                    break;
                }

                foreach (int k in newlyFrozen) frozen.Add(k);
            }
        }
        else if (free > 0)
        {
            // No fr: auto tracks stretch to fill (justify-content and align-content normal).
            int[] autos = Enumerable.Range(0, n).Where(k => tracks[k].MaxKind == TrackKind.Auto).ToArray();
            foreach (int k in autos) baseSize[k] += free / autos.Length;
        }

        return baseSize;
    }

    private (double Min, double Max) ItemIntrinsic(GridItem item) => Intrinsic(item.Box);

    private Placed LayoutGrid(GridBox box, double cbX, double cbWidth, double cursorY, MarginCollapse pending, double? cbHeight, double? forcedWidth)
    {
        ComputedStyle s = box.Style;
        Edges margin = BoxMetrics.Margins(s, cbWidth);
        Edges border = BoxMetrics.Borders(s);
        Edges padding = BoxMetrics.Paddings(s, cbWidth);
        double width = forcedWidth ?? ResolveWidth(box, cbWidth, ref margin, border, padding);
        double? height = SpecifiedHeight(s, cbHeight, border, padding);
        double y = cursorY + (pending + margin.Top).Value;
        double contentX = cbX + margin.Left + border.Left + padding.Left;
        double contentTop = y + border.Top + padding.Top;
        double columnGap = Gap(s, "column-gap", width), rowGap = Gap(s, "row-gap", height ?? 0);

        List<TrackSize> columnTemplate = ParseTracks(s["grid-template-columns"], width, columnGap);
        List<TrackSize> rowTemplate = ParseTracks(s["grid-template-rows"], height, rowGap);
        List<GridItem> items = PlaceItems(box, columnTemplate, rowTemplate, out int columnCount, out int rowCount);
        List<TrackSize> columns = WithImplicit(columnTemplate, columnCount, s["grid-auto-columns"], width);
        List<TrackSize> rows = WithImplicit(rowTemplate, rowCount, s["grid-auto-rows"], height);

        double[] columnWidths = SizeTracks(columns, items.Select(i => (i.Column, i.ColumnSpan, ItemIntrinsic(i).Min, ItemIntrinsic(i).Max)), width, columnGap);
        var columnX = new double[columnCount + 1];
        columnX[0] = contentX;
        for (int k = 0; k < columnCount; k++) columnX[k + 1] = columnX[k] + columnWidths[k] + columnGap;

        // Items laid out in their column areas give the rows their content heights.
        foreach (GridItem item in items)
        {
            double areaWidth = columnX[item.Column + item.ColumnSpan] - columnX[item.Column] - columnGap;
            item.Fragment = LayoutGridItem(item, areaWidth, s);
        }

        double[] rowHeights = SizeTracks(rows, items.Select(i => (i.Row, i.RowSpan, i.Fragment.Height + i.Fragment.Margin.Vertical, i.Fragment.Height + i.Fragment.Margin.Vertical)), height, rowGap);
        var rowY = new double[rowCount + 1];
        rowY[0] = contentTop;
        for (int k = 0; k < rowCount; k++) rowY[k + 1] = rowY[k] + rowHeights[k] + rowGap;
        double contentHeight = height ?? Math.Max(0, rowY[rowCount] - contentTop - (rowCount > 0 ? rowGap : 0));

        var fragment = new BoxFragment(box) { X = cbX + margin.Left, Y = y, Border = border, Padding = padding, Margin = margin };
        foreach (GridItem item in items)
        {
            double areaX = columnX[item.Column], areaY = rowY[item.Row];
            double areaWidth = columnX[item.Column + item.ColumnSpan] - areaX - columnGap;
            double areaHeight = rowY[item.Row + item.RowSpan] - areaY - rowGap;
            BoxFragment f = item.Fragment;
            Edges m = f.Margin;
            string justify = GridSelf(item.Box.Style, s, "justify");
            string align = GridSelf(item.Box.Style, s, "align");
            if (align == "stretch" && BoxMetrics.IsAuto(item.Box.Style, "height")) f.Height = Math.Max(f.Height, areaHeight - m.Vertical);
            double dx = justify switch
            {
                "end" => areaWidth - f.Width - m.Horizontal,
                "center" => (areaWidth - f.Width - m.Horizontal) / 2,
                _ => 0,
            };
            double dy = align switch
            {
                "end" => areaHeight - f.Height - m.Vertical,
                "center" => (areaHeight - f.Height - m.Vertical) / 2,
                _ => 0,
            };
            f.Translate(areaX + m.Left + dx - f.X, areaY + m.Top + dy - f.Y);
            fragment.Children.Add(f);
        }

        // Pages may break on a row line that no item crosses.
        fragment.BreakLines = [.. Enumerable.Range(1, Math.Max(0, rowCount - 1))
            .Where(line => !items.Any(i => i.Row < line && i.Row + i.RowSpan > line))
            .Select(line => rowY[line] - rowGap)];

        contentHeight = ClampHeight(s, contentHeight, cbHeight, border, padding);
        fragment.Width = width + border.Horizontal + padding.Horizontal;
        fragment.Height = contentHeight + border.Vertical + padding.Vertical;
        fragment.FirstBaseline = items.OrderBy(i => i.Row).ThenBy(i => i.Column).Select(i => i.Fragment.FirstBaseline).FirstOrDefault(b => b is not null);
        fragment.LastBaseline = fragment.FirstBaseline;
        Offset(fragment, s, cbWidth, cbHeight);
        return new Placed(fragment, MarginCollapse.None + margin.Bottom, false);
    }

    /// <summary>justify-self or align-self, falling back to the container's justify-items or align-items; normal stretches.</summary>
    private static string GridSelf(ComputedStyle item, ComputedStyle container, string axis)
    {
        string self = item.Keyword(axis + "-self");
        string value = self is "auto" or "" ? container.Keyword(axis + "-items") : self;
        return value switch
        {
            "start" or "self-start" or "flex-start" or "left" => "start",
            "end" or "self-end" or "flex-end" or "right" => "end",
            "center" => "center",
            _ => "stretch",
        };
    }

    /// <summary>A grid item at the origin: stretched across its area, or shrunk to fit when aligned to a side.</summary>
    private BoxFragment LayoutGridItem(GridItem item, double areaWidth, ComputedStyle container)
    {
        ComputedStyle cs = item.Box.Style;
        Edges m = BoxMetrics.Margins(cs, areaWidth);
        Edges border = BoxMetrics.Borders(cs);
        Edges padding = BoxMetrics.Paddings(cs, areaWidth);
        double frame = border.Horizontal + padding.Horizontal;
        double? specified = DefiniteSize(cs, "width", areaWidth, border, padding, horizontal: true);
        double contentWidth;
        if (specified is double w) contentWidth = w;
        else if (GridSelf(cs, container, "justify") == "stretch") contentWidth = areaWidth - m.Horizontal - frame;
        else
        {
            (double min, double max) = ContentIntrinsic(item.Box);
            contentWidth = Math.Min(Math.Max(min, areaWidth - m.Horizontal - frame), max);
        }

        BoxFragment f = LayoutBlockLevel(item.Box, 0, areaWidth, 0, MarginCollapse.None, null, Math.Max(0, contentWidth)).Fragment;
        f.Margin = m;
        return f;
    }

    /// <summary>Intrinsic widths of a grid container's content box: the columns at their minimum and maximum sizes.</summary>
    private (double Min, double Max) GridIntrinsic(GridBox box)
    {
        ComputedStyle s = box.Style;
        double gap = Gap(s, "column-gap", 0);
        List<TrackSize> template = ParseTracks(s["grid-template-columns"], null, gap);
        List<GridItem> items = PlaceItems(box, template, ParseTracks(s["grid-template-rows"], null, 0), out int columns, out _);
        List<TrackSize> tracks = WithImplicit(template, columns, s["grid-auto-columns"], null);
        var contributions = items.Select(i => (i.Column, i.ColumnSpan, ItemIntrinsic(i).Min, ItemIntrinsic(i).Max)).ToList();
        double gaps = gap * Math.Max(0, columns - 1);
        double max = SizeTracks(tracks, contributions, null, gap).Sum() + gaps;
        double min = SizeTracks(tracks, contributions.Select(c => (c.Column, c.ColumnSpan, c.Min, c.Min)), null, gap).Sum() + gaps;
        return (min, Math.Max(min, max));
    }
}
