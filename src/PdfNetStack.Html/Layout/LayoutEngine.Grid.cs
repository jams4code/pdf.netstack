using System.Globalization;
using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Grid layout (CSS Grid Layout 1 and 2): explicit tracks from the templates (lengths, percentages,
/// fr, auto, min-content, max-content, minmax(), fit-content(), repeat() with counts and auto-fill)
/// and their line names, implicit tracks from grid-auto-rows and grid-auto-columns on either side
/// of the explicit grid, placement by line numbers, line names, spans and named areas,
/// auto-placement by rows or columns, sparse or dense, subgrids, then track sizing and alignment.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Hostile templates and spans must not claim unbounded tracks.
    private const int MaxTracks = 1000;

    // The subgrid frames of the grids being laid out: the tracks each parent hands its subgrids.
    private readonly Dictionary<GridBox, SubgridFrame> _subgrids = [];

    // Items a subgrid laid out while its parent measured the rows, kept for its final layout.
    private readonly Dictionary<Box, (double Width, BoxFragment Fragment)> _gridMeasured = [];

    private enum TrackKind
    {
        Fixed,
        Auto,
        MinContent,
        MaxContent,
        Flex,
    }

    private enum LineKind
    {
        Auto,
        Line,
        Span,
        Name,
    }

    /// <summary>A track's sizing function: a minimum and a maximum, each fixed, content based or flexible.</summary>
    private readonly record struct TrackSize(TrackKind MinKind, double Min, TrackKind MaxKind, double Max)
    {
        public static TrackSize Auto => new(TrackKind.Auto, 0, TrackKind.Auto, 0);

        public bool IsFlex => MaxKind == TrackKind.Flex;
    }

    /// <summary>A grid-*-start or grid-*-end value (CSS Grid 1, 8.3): auto, a line, a span, or a name alone.</summary>
    private readonly record struct GridLine(LineKind Kind, int Number, string? Name);

    /// <summary>A parsed track list: the sizes and the names of the lines around them, or a subgrid's line name list.</summary>
    private sealed class TrackList
    {
        public List<TrackSize> Sizes { get; } = [];

        /// <summary>The names of each line, one more than the tracks.</summary>
        public List<List<string>> Names { get; } = [[]];

        public bool IsSubgrid { get; set; }

        /// <summary>A subgrid's names for its own lines, from the first.</summary>
        public List<List<string>> SubgridNames { get; } = [];

        /// <summary>A subgrid's repeat(auto-fill, ...) of names: where it goes in the list and what it repeats.</summary>
        public (int Index, List<List<string>> Pattern)? AutoFillNames { get; set; }

        /// <summary>The tracks a repeat(auto-fit, ...) made: the first and how many.</summary>
        public (int Start, int Count)? AutoFit { get; set; }
    }

    /// <summary>One dimension of a grid: its explicit tracks and line names, and once placed, every track.</summary>
    private sealed class GridAxis
    {
        public List<TrackSize> Explicit { get; set; } = [];

        /// <summary>The names of the explicit grid's lines; one more entry than the explicit tracks.</summary>
        public List<List<string>> Names { get; set; } = [[]];

        public int Count => Names.Count - 1;

        /// <summary>The parent's tracks when this dimension is a subgrid.</summary>
        public SubgridAxis? Parent { get; set; }

        /// <summary>The explicit tracks a repeat(auto-fit, ...) made, which collapse when empty.</summary>
        public (int Start, int Count)? AutoFit { get; set; }

        /// <summary>Implicit tracks before the explicit grid.</summary>
        public int Offset { get; set; }

        /// <summary>Every track, implicit ones included.</summary>
        public List<TrackSize> Tracks { get; set; } = [];

        /// <summary>The names of every line, one more entry than <see cref="Tracks"/>.</summary>
        public List<List<string>> AllNames { get; set; } = [];

        public int Total => Tracks.Count;
    }

    /// <summary>What a subgrid takes from its parent in one dimension (CSS Grid 2, 9), and what it gives back.</summary>
    private sealed class SubgridAxis(List<List<string>> names, double gap)
    {
        /// <summary>The parent's names of the lines the subgrid spans.</summary>
        public List<List<string>> Names { get; } = names;

        public int Count => Names.Count - 1;

        /// <summary>The parent's gap between these tracks.</summary>
        public double Gap { get; } = gap;

        /// <summary>
        /// Each spanned track's start and end, from the start of the area the subgrid's margin box fills;
        /// null while the parent is still measuring them.
        /// </summary>
        public double[]? Starts { get; set; }

        public double[]? Ends { get; set; }

        /// <summary>The size of the area the subgrid's margin box fills.</summary>
        public double Extent { get; set; }

        /// <summary>The subgrid's items' sizes, reported while the parent measures its rows.</summary>
        public List<(int Start, int Span, double Size)> Measured { get; } = [];
    }

    /// <summary>A subgrid's dimensions taken from its parent, and its items' placement when the parent needed it first.</summary>
    private sealed class SubgridFrame
    {
        public SubgridAxis? Rows { get; set; }

        public SubgridAxis? Columns { get; set; }

        public GridPlan? Plan { get; set; }
    }

    /// <summary>A grid's items placed on its tracks, rows at index 0 and columns at 1.</summary>
    private sealed class GridPlan(List<GridItem> items, GridAxis rows, GridAxis columns)
    {
        public List<GridItem> Items { get; } = items;

        public GridAxis Rows { get; } = rows;

        public GridAxis Columns { get; } = columns;
    }

    private sealed class GridItem(Box box)
    {
        public Box Box { get; } = box;

        /// <summary>The first track on each axis, rows at index 0 and columns at 1; -1 until placed.</summary>
        public int[] Start { get; } = [-1, -1];

        public int[] Span { get; } = [1, 1];

        public int Row => Start[0];

        public int Column => Start[1];

        public int RowSpan => Span[0];

        public int ColumnSpan => Span[1];

        /// <summary>Set when the item is a subgrid in at least one dimension.</summary>
        public SubgridFrame? Subgrid { get; set; }

        public BoxFragment Fragment { get; set; } = null!;
    }

    /// <summary>Where a grid's tracks lie on one axis, and how its items' areas are cut from them.</summary>
    private sealed class TrackLines(double[] starts, double[] ends, double contentStart, double contentEnd, double inset, bool subgrid)
    {
        public double[] Starts { get; } = starts;

        public double[] Ends { get; } = ends;

        /// <summary>
        /// The area of the tracks from <paramref name="first"/>. In a subgrid, items on its first and
        /// last tracks stop at its content edges, and half the difference between its gap and its
        /// parent's goes to each side of an inner gap (CSS Grid 2, 9).
        /// </summary>
        public (double Start, double Size) Area(int first, int span)
        {
            int last = first + span - 1;
            double a = Starts[first], b = Ends[last];
            if (subgrid)
            {
                a = first == 0 ? contentStart : a + inset;
                b = last == Ends.Length - 1 ? contentEnd : b - inset;
            }

            return (a, Math.Max(0, b - a));
        }

        /// <summary>Tracks of the given sizes from <paramref name="start"/>, separated by <paramref name="gap"/>.</summary>
        public static TrackLines Sized(double start, double[] sizes, double gap)
        {
            var starts = new double[sizes.Length];
            var ends = new double[sizes.Length];
            double x = start;
            for (int k = 0; k < sizes.Length; k++)
            {
                starts[k] = x;
                ends[k] = x + sizes[k];
                x = ends[k] + gap;
            }

            return new TrackLines(starts, ends, start, sizes.Length == 0 ? start : ends[^1], 0, false);
        }
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

    /// <summary>The names in a bracketed line name list.</summary>
    private static List<string> LineNames(CssValue brackets) =>
        [.. brackets.Children.Where(c => c.IsToken && c.Token.Type == CssTokenType.Ident && !c.IsIdent("span") && !c.IsIdent("auto")).Select(c => c.Token.Value)];

    /// <summary>A repeat() function's count, or null for auto-fill and auto-fit, and the values it repeats.</summary>
    private static (int? Count, List<CssValue> Unit)? Repeat(CssValue v)
    {
        int comma = v.Children.FindIndex(c => c.IsToken && c.Token.Type == CssTokenType.Comma);
        if (comma < 0) return null;
        CssValue? count = v.Children.Take(comma).FirstOrDefault(c => !c.IsWhitespace);
        List<CssValue> unit = [.. v.Children.Skip(comma + 1)];
        if (count is null) return null;
        if (count.IsIdent("auto-fill") || count.IsIdent("auto-fit")) return (null, unit);
        return count.IsToken && count.Token.Type == CssTokenType.Number ? ((int)Math.Max(1, Math.Min(MaxTracks, count.Token.Number)), unit) : null;
    }

    /// <summary>
    /// A track list (CSS Grid 1, 7.2): sizes, repeat() and line names. With subgrid, the line name
    /// list instead (CSS Grid 2, 9.1).
    /// </summary>
    private static TrackList ParseTracks(List<CssValue> value, double? basis, double gap)
    {
        var list = new TrackList();
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        if (items.Count > 0 && items[0].IsIdent("subgrid"))
        {
            list.IsSubgrid = true;
            foreach (CssValue v in items.Skip(1))
            {
                if (v.Block == CssTokenType.LeftBracket && list.SubgridNames.Count <= MaxTracks)
                {
                    list.SubgridNames.Add(LineNames(v));
                }
                else if (v.Function == "repeat" && Repeat(v) is (var count, var unit))
                {
                    List<List<string>> names = [.. unit.Where(u => u.Block == CssTokenType.LeftBracket).Select(LineNames)];
                    if (names.Count == 0) continue;
                    if (count is int times)
                    {
                        for (int k = 0; k < times && list.SubgridNames.Count <= MaxTracks; k++) list.SubgridNames.AddRange(names);
                    }
                    else
                    {
                        list.AutoFillNames ??= (list.SubgridNames.Count, names);
                    }
                }
            }

            return list;
        }

        // auto-fill repeats as often as fits beside the other tracks, each counted at its
        // maximum when that is fixed, else its minimum (CSS Grid 1, 7.2.3.2).
        static double FixedSize(TrackSize t) => t.MaxKind == TrackKind.Fixed ? t.Max : t.MinKind == TrackKind.Fixed ? t.Min : 0;
        double others = 0;
        int otherCount = 0;
        foreach (CssValue v in items)
        {
            if (v.Function == "repeat")
            {
                if (Repeat(v) is (int count, List<CssValue> unit))
                {
                    List<TrackSize> sizes = ParseTracks(unit, basis, gap).Sizes;
                    others += count * sizes.Sum(FixedSize);
                    otherCount += count * sizes.Count;
                }
            }
            else if (ParseTrack(v, basis) is TrackSize track)
            {
                others += FixedSize(track);
                otherCount++;
            }
        }

        foreach (CssValue v in items)
        {
            if (v.Block == CssTokenType.LeftBracket)
            {
                list.Names[^1].AddRange(LineNames(v));
                continue;
            }

            if (v.IsIdent("none")) continue;
            if (v.Function == "repeat")
            {
                if (Repeat(v) is not (var count, var values)) continue;
                TrackList unit = ParseTracks(values, basis, gap);
                if (unit.Sizes.Count == 0) continue;
                int times = count ?? 1;
                if (count is null)
                {
                    double size = unit.Sizes.Sum(FixedSize) + (gap * unit.Sizes.Count);
                    double room = basis is double b ? b - others - (otherCount * gap) + gap : 0;
                    times = size > 0 && room > 0 ? (int)Math.Max(1, Math.Min(MaxTracks, Math.Floor(room / size))) : 1;
                }

                int first = list.Sizes.Count;
                for (int k = 0; k < times && list.Sizes.Count < MaxTracks; k++)
                {
                    list.Names[^1].AddRange(unit.Names[0]);
                    for (int j = 0; j < unit.Sizes.Count && list.Sizes.Count < MaxTracks; j++)
                    {
                        list.Sizes.Add(unit.Sizes[j]);
                        list.Names.Add([.. unit.Names[j + 1]]);
                    }
                }

                if (count is null && v.Children.FirstOrDefault(c => !c.IsWhitespace)?.IsIdent("auto-fit") == true)
                {
                    list.AutoFit ??= (first, list.Sizes.Count - first);
                }

                continue;
            }

            if (ParseTrack(v, basis) is TrackSize parsed && list.Sizes.Count < MaxTracks)
            {
                list.Sizes.Add(parsed);
                list.Names.Add([]);
            }
        }

        return list;
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

    /// <summary>A placement property: a line (an integer and maybe a name), span with a count or a name, a name alone, or auto.</summary>
    private static GridLine ParseLine(List<CssValue> value)
    {
        List<CssValue> items = value.Where(v => !v.IsWhitespace).ToList();
        bool span = items.Any(v => v.IsIdent("span"));
        CssValue? number = items.FirstOrDefault(v => v.IsToken && v.Token is { Type: CssTokenType.Number, IsInteger: true });
        string? name = items.FirstOrDefault(v => v.IsToken && v.Token.Type == CssTokenType.Ident && !v.IsIdent("span") && !v.IsIdent("auto"))?.Token.Value;
        int n = number is null ? 1 : (int)Math.Max(-MaxTracks, Math.Min(MaxTracks, number.Token.Number));
        if (span) return n > 0 ? new GridLine(LineKind.Span, n, name) : default;
        if (number is not null) return n != 0 ? new GridLine(LineKind.Line, n, name) : default;
        return name is null ? default : new GridLine(LineKind.Name, 1, name);
    }

    /// <summary>
    /// The line a value names, counted from 0 at the explicit grid's start; negative or past the end
    /// for implicit lines, which all count as carrying any name searched past the explicit ones.
    /// </summary>
    private static int? DefiniteLine(GridLine line, GridAxis axis, bool start)
    {
        if (line.Kind == LineKind.Name)
        {
            // A name alone is first an area edge, name-start or name-end, then the first line of that name.
            string edge = line.Name + (start ? "-start" : "-end");
            int found = axis.Names.FindIndex(n => n.Contains(edge));
            return found >= 0 ? found : NthLine(axis, line.Name, 1);
        }

        return line.Kind == LineKind.Line ? NthLine(axis, line.Name, line.Number) : null;
    }

    private static int NthLine(GridAxis axis, string? name, int n)
    {
        int count = axis.Count;
        if (name is null) return n > 0 ? n - 1 : count + 1 + n;
        int seen = 0;
        if (n > 0)
        {
            for (int i = 0; i <= count; i++)
            {
                if (axis.Names[i].Contains(name) && ++seen == n) return i;
            }

            return count + (n - seen);
        }

        for (int i = count; i >= 0; i--)
        {
            if (axis.Names[i].Contains(name) && ++seen == -n) return i;
        }

        return -(-n - seen);
    }

    /// <summary>The line <paramref name="n"/> lines of a name away from <paramref name="from"/>, for a named span.</summary>
    private static int SpanToName(GridAxis axis, string name, int n, int from, bool forward)
    {
        int seen = 0;
        if (forward)
        {
            for (int i = Math.Max(0, from + 1); i <= axis.Count; i++)
            {
                if (axis.Names[i].Contains(name) && ++seen == n) return i;
            }

            return Math.Max(from, axis.Count) + (n - seen);
        }

        for (int i = Math.Min(axis.Count, from - 1); i >= 0; i--)
        {
            if (axis.Names[i].Contains(name) && ++seen == n) return i;
        }

        return Math.Min(from, 0) - (n - seen);
    }

    /// <summary>
    /// An item's start line and span on one axis (CSS Grid 1, 8.3), with the conflict rules of 8.3.1:
    /// equal lines drop the end, two spans drop the end one, and a named span with no line to count
    /// from spans one track. The start is null when the item is placed automatically.
    /// </summary>
    private static (int? Start, int Span) ResolveLines(GridLine start, GridLine end, GridAxis axis, int autoSpan)
    {
        int? s = DefiniteLine(start, axis, true), e = DefiniteLine(end, axis, false);
        if (s is int a && e is int b) return a == b ? (a, autoSpan) : (Math.Min(a, b), Math.Abs(b - a));
        if (s is int only)
        {
            if (end.Kind != LineKind.Span) return (only, autoSpan);
            return (only, end.Name is null ? end.Number : SpanToName(axis, end.Name, end.Number, only, true) - only);
        }

        if (e is int endOnly)
        {
            int first = start.Kind != LineKind.Span ? endOnly - autoSpan
                : start.Name is null ? endOnly - start.Number
                : SpanToName(axis, start.Name, start.Number, endOnly, false);
            return (first, endOnly - first);
        }

        if (start.Kind == LineKind.Span) return (null, start.Name is null ? start.Number : 1);
        if (end.Kind == LineKind.Span) return (null, end.Name is null ? end.Number : 1);
        return (null, autoSpan);
    }

    /// <summary>True when a box is a grid container whose template on an axis is subgrid.</summary>
    private static bool IsSubgrid(Box box, string axis) =>
        box is GridBox && box.Style["grid-template-" + axis].FirstOrDefault(v => !v.IsWhitespace) is CssValue v && v.IsIdent("subgrid");

    /// <summary>A subgrid's span when its placement gives none: the tracks its line name list describes, at least one (CSS Grid 2, 9.1).</summary>
    private static int AutoSpan(Box box, string axis) =>
        IsSubgrid(box, axis) ? Math.Max(1, ParseTracks(box.Style["grid-template-" + axis], null, 0).SubgridNames.Count - 1) : 1;

    /// <summary>One dimension's explicit grid: from its template, or the parent's tracks when it is a subgrid.</summary>
    private static GridAxis Axis(ComputedStyle s, string name, double? basis, double gap, SubgridAxis? parent)
    {
        TrackList list = ParseTracks(s["grid-template-" + name], basis, gap);
        var axis = new GridAxis();
        if (list.IsSubgrid && parent is not null)
        {
            // The parent's line names, then the subgrid's own (CSS Grid 2, 9.3).
            axis.Parent = parent;
            axis.Names = [.. parent.Names.Select(n => new List<string>(n))];
            axis.Explicit = [.. Enumerable.Repeat(TrackSize.Auto, parent.Count)];
            List<List<string>> own = [.. list.SubgridNames];
            if (list.AutoFillNames is (int index, List<List<string>> pattern))
            {
                int times = Math.Max(0, (axis.Names.Count - own.Count) / pattern.Count);
                own.InsertRange(index, Enumerable.Range(0, times).SelectMany(_ => pattern));
            }

            for (int i = 0; i < own.Count && i < axis.Names.Count; i++) axis.Names[i].AddRange(own[i]);
        }
        else if (!list.IsSubgrid)
        {
            axis.Explicit = list.Sizes;
            axis.Names = list.Names;
            axis.AutoFit = list.AutoFit;
        }

        return axis;
    }

    /// <summary>Gives a line a name, growing the explicit grid to reach it unless the dimension is a subgrid's.</summary>
    private static void AddName(GridAxis axis, int line, string name)
    {
        if (line >= axis.Names.Count && axis.Parent is not null) return;
        while (axis.Names.Count <= line) axis.Names.Add([]);
        axis.Names[line].Add(name);
    }

    /// <summary>
    /// Places a grid's items (CSS Grid 1, 8.5): items with definite positions first, then those
    /// locked to a line of the flow direction's axis, then the rest along the flow, sparse or dense.
    /// Implicit tracks go after the explicit grid, or before it for lines placed there.
    /// </summary>
    /// <param name="frame">The parent's tracks when the grid is a subgrid.</param>
    private GridPlan PlaceGrid(GridBox box, double? width, double? height, double columnGap, double rowGap, SubgridFrame? frame)
    {
        ComputedStyle s = box.Style;
        GridAxis[] axes = [Axis(s, "rows", height, rowGap, frame?.Rows), Axis(s, "columns", width, columnGap, frame?.Columns)];
        foreach (KeyValuePair<string, (int Row, int Column, int RowEnd, int ColumnEnd)> area in ParseAreas(s["grid-template-areas"]))
        {
            // Areas name their edge lines name-start and name-end (CSS Grid 1, 7.3.2).
            AddName(axes[0], area.Value.Row, area.Key + "-start");
            AddName(axes[0], area.Value.RowEnd, area.Key + "-end");
            AddName(axes[1], area.Value.Column, area.Key + "-start");
            AddName(axes[1], area.Value.ColumnEnd, area.Key + "-end");
        }

        string flow = s.Keywords("grid-auto-flow");
        bool dense = flow.Contains("dense");
        int major = flow.Contains("column") ? 1 : 0, minor = 1 - major;
        int[] limit = new int[2];
        limit[major] = MaxTracks * 4;
        limit[minor] = MaxTracks;
        string[] names = ["row", "column"];
        string[] templates = ["rows", "columns"];

        var items = new List<GridItem>();
        var resolved = new List<int?[]>();
        foreach (Box child in box.Children.Where(c => !IsOutOfFlow(c)).OrderBy(c => (int)c.Style.Number("order")))
        {
            var item = new GridItem(child);
            var start = new int?[2];
            for (int a = 0; a < 2; a++)
            {
                (start[a], int span) = ResolveLines(ParseLine(child.Style["grid-" + names[a] + "-start"]), ParseLine(child.Style["grid-" + names[a] + "-end"]), axes[a], AutoSpan(child, templates[a]));
                item.Span[a] = Math.Max(1, Math.Min(limit[a], span));
            }

            items.Add(item);
            resolved.Add(start);
        }

        // Lines before the explicit grid add implicit tracks there; a subgrid has none and clamps instead.
        for (int a = 0; a < 2; a++)
        {
            int first = resolved.Select(r => r[a] ?? 0).DefaultIfEmpty(0).Min();
            axes[a].Offset = axes[a].Parent is null ? Math.Min(MaxTracks, Math.Max(0, -first)) : 0;
        }

        // Spans share one budget of grid slots, as table spans do: past it an item spans one cell.
        long slots = 0;
        for (int i = 0; i < items.Count; i++)
        {
            GridItem item = items[i];
            for (int a = 0; a < 2; a++)
            {
                if (resolved[i][a] is not int line) continue;
                int bound = axes[a].Parent is null ? limit[a] : axes[a].Count;
                item.Start[a] = Math.Max(0, Math.Min(bound - 1, line + axes[a].Offset));
                item.Span[a] = Math.Max(1, Math.Min(item.Span[a], bound - item.Start[a]));
            }

            if (slots + ((long)item.RowSpan * item.ColumnSpan) > MaxGridSlots) item.Span[0] = item.Span[1] = 1;
            slots += (long)item.RowSpan * item.ColumnSpan;
        }

        // Tracks across the flow: the explicit ones, those definite positions reach, and the widest auto span.
        int minorCount = Math.Min(limit[minor], Math.Max(1, Math.Max(
            axes[minor].Offset + axes[minor].Count,
            items.Count == 0 ? 0 : items.Max(i => i.Start[minor] < 0 ? i.Span[minor] : i.Start[minor] + i.Span[minor]))));
        foreach (GridItem item in items.Where(i => i.Start[minor] < 0)) item.Span[minor] = Math.Min(item.Span[minor], minorCount);

        var occupied = new HashSet<long>();
        void Occupy(GridItem item)
        {
            for (int r = item.Start[major]; r < item.Start[major] + item.Span[major]; r++)
            {
                for (int c = item.Start[minor]; c < item.Start[minor] + item.Span[minor] && occupied.Count < MaxGridSlots; c++) occupied.Add(Key(r, c));
            }
        }

        // Every cell any search looks at counts against one budget. Once it is spent, searches stop
        // where they are: hostile input gets overlapping items rather than unbounded work.
        long probes = 0;
        bool Free(int along, int across, GridItem item, int bound)
        {
            if (across + item.Span[minor] > bound) return false;
            for (int r = along; r < along + item.Span[major]; r++)
            {
                for (int c = across; c < across + item.Span[minor]; c++)
                {
                    if (++probes > MaxGridSlots) return true;
                    if (occupied.Contains(Key(r, c))) return false;
                }
            }

            return true;
        }

        foreach (GridItem item in items.Where(i => i.Start[0] >= 0 && i.Start[1] >= 0)) Occupy(item);

        // Items locked to a line of the flow axis search across it, from where the last one on that
        // line ended when sparse, from its start when dense; they may add tracks across.
        var lineCursor = new Dictionary<int, int>();
        foreach (GridItem item in items.Where(i => i.Start[major] >= 0 && i.Start[minor] < 0))
        {
            int c = dense ? 0 : lineCursor.TryGetValue(item.Start[major], out int last) ? last : 0;
            while (!Free(item.Start[major], c, item, limit[minor]) && c + item.Span[minor] < limit[minor]) c++;
            if (c + item.Span[minor] > limit[minor]) c = Math.Max(0, limit[minor] - item.Span[minor]);
            item.Start[minor] = c;
            lineCursor[item.Start[major]] = c + item.Span[minor];
            minorCount = Math.Max(minorCount, c + item.Span[minor]);
            Occupy(item);
        }

        int cursorAlong = 0, cursorAcross = 0;
        foreach (GridItem item in items.Where(i => i.Start[major] < 0))
        {
            if (item.Start[minor] >= 0)
            {
                // A definite position across: the first line along, from the cursor or the start, where it fits.
                if (dense) cursorAlong = 0;
                else if (item.Start[minor] < cursorAcross) cursorAlong++;
                cursorAcross = item.Start[minor];
                while (!Free(cursorAlong, item.Start[minor], item, int.MaxValue) && cursorAlong < limit[major] - 1) cursorAlong++;
            }
            else
            {
                if (dense) cursorAlong = cursorAcross = 0;
                while (!Free(cursorAlong, cursorAcross, item, minorCount) && cursorAlong < limit[major] - 1)
                {
                    cursorAcross++;
                    if (cursorAcross + item.Span[minor] > minorCount)
                    {
                        cursorAcross = 0;
                        cursorAlong++;
                    }
                }

                item.Start[minor] = cursorAcross;
                cursorAcross += item.Span[minor];
            }

            item.Start[major] = cursorAlong;
            item.Span[major] = Math.Min(item.Span[major], limit[major] - cursorAlong);
            Occupy(item);
        }

        int majorCount = Math.Min(limit[major], Math.Max(axes[major].Offset + axes[major].Count, items.Count == 0 ? 0 : items.Max(i => i.Start[major] + i.Span[major])));
        int[] total = new int[2];
        total[major] = majorCount;
        total[minor] = minorCount;
        for (int a = 0; a < 2; a++)
        {
            GridAxis axis = axes[a];
            if (axis.Parent is not null)
            {
                // A subgrid has no implicit tracks: areas past its last line are pulled back in (CSS Grid 2, 9).
                total[a] = axis.Count;
                foreach (GridItem item in items)
                {
                    item.Start[a] = Math.Min(item.Start[a], axis.Count - 1);
                    item.Span[a] = Math.Max(1, Math.Min(item.Span[a], axis.Count - item.Start[a]));
                }
            }

            total[a] = Math.Max(total[a], axis.Offset + axis.Count);
            axis.Tracks = Implicit(axis, total[a], s["grid-auto-" + templates[a]], a == 0 ? height : width);
            axis.AllNames = [.. Enumerable.Range(0, total[a] + 1).Select(line => line >= axis.Offset && line - axis.Offset < axis.Names.Count ? axis.Names[line - axis.Offset] : [])];
            if (axis.AutoFit is (int fitStart, int fitCount)) Collapse(axis, items, a, axis.Offset + fitStart, fitCount);
        }

        // A subgrid adopts the lines it spans; placement inside it needs only their names, so it
        // happens now when its columns are adopted, as the parent's column sizes depend on it.
        foreach (GridItem item in items)
        {
            bool columns = IsSubgrid(item.Box, "columns"), rows = IsSubgrid(item.Box, "rows");
            if (!columns && !rows) continue;
            var sub = new SubgridFrame
            {
                Rows = rows ? new SubgridAxis(axes[0].AllNames.GetRange(item.Row, item.RowSpan + 1), rowGap) : null,
                Columns = columns ? new SubgridAxis(axes[1].AllNames.GetRange(item.Column, item.ColumnSpan + 1), columnGap) : null,
            };
            item.Subgrid = sub;
            if (columns)
            {
                ComputedStyle cs = item.Box.Style;
                double? subHeight = rows ? null : SpecifiedHeight(cs, null, BoxMetrics.Borders(cs), BoxMetrics.Paddings(cs, 0));
                sub.Plan = PlaceGrid((GridBox)item.Box, null, subHeight, SubgridGap(cs, "column-gap", sub.Columns, 0), SubgridGap(cs, "row-gap", sub.Rows, subHeight ?? 0), sub);
            }
        }

        return new GridPlan(items, axes[0], axes[1]);
    }

    /// <summary>
    /// Collapses the empty tracks of a repeat(auto-fit) (CSS Grid 1, 7.2.3.2): a collapsed track and
    /// the gap beside it take no room, so it leaves the grid and its end line merges into its start.
    /// </summary>
    private static void Collapse(GridAxis axis, List<GridItem> items, int a, int first, int count)
    {
        var used = new bool[axis.Tracks.Count];
        foreach (GridItem item in items)
        {
            for (int k = item.Start[a]; k < item.Start[a] + item.Span[a] && k < used.Length; k++) used[k] = true;
        }

        var tracks = new List<TrackSize>(axis.Tracks.Count);
        var names = new List<List<string>> { axis.AllNames[0] };
        var shift = new int[axis.Tracks.Count + 1];
        for (int k = 0; k < axis.Tracks.Count; k++)
        {
            bool collapsed = k >= first && k < first + count && !used[k];
            shift[k + 1] = shift[k] + (collapsed ? 1 : 0);
            if (collapsed)
            {
                names[^1] = [.. names[^1], .. axis.AllNames[k + 1]];
                continue;
            }

            tracks.Add(axis.Tracks[k]);
            names.Add(axis.AllNames[k + 1]);
        }

        foreach (GridItem item in items) item.Start[a] -= shift[item.Start[a]];
        axis.Tracks = tracks;
        axis.AllNames = names;
    }

    /// <summary>A gap; a subgrid's normal gap is its parent's.</summary>
    private static double SubgridGap(ComputedStyle s, string property, SubgridAxis? parent, double basis) =>
        parent is not null && s.Keyword(property) == "normal" ? parent.Gap : Gap(s, property, basis);

    /// <summary>
    /// Every track of a dimension: the implicit ones repeat grid-auto-rows or grid-auto-columns, forward
    /// after the explicit grid and backward before it (CSS Grid 1, 7.6).
    /// </summary>
    private static List<TrackSize> Implicit(GridAxis axis, int count, List<CssValue> auto, double? basis)
    {
        List<TrackSize> pattern = ParseTracks(auto, basis, 0).Sizes;
        if (pattern.Count == 0) pattern = [TrackSize.Auto];
        int p = pattern.Count;
        var result = new List<TrackSize>(count);
        for (int j = axis.Offset; j > 0; j--) result.Add(pattern[((p - (j % p)) % p + p) % p]);
        result.AddRange(axis.Explicit.Take(count - result.Count));
        for (int k = 0; result.Count < count; k++) result.Add(pattern[k % p]);
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

    /// <summary>A subgrid's margin, border and padding at the start and end of an axis, and half the difference of its gap from its parent's.</summary>
    private static (double Start, double End, double Inset) SubgridEdges(Box box, SubgridAxis axis, bool rows, double basis)
    {
        ComputedStyle s = box.Style;
        Edges m = BoxMetrics.Margins(s, basis), b = BoxMetrics.Borders(s), p = BoxMetrics.Paddings(s, basis);
        double inset = (SubgridGap(s, rows ? "row-gap" : "column-gap", axis, basis) - axis.Gap) / 2;
        return rows
            ? (m.Top + b.Top + p.Top, m.Bottom + b.Bottom + p.Bottom, inset)
            : (m.Left + b.Left + p.Left, m.Right + b.Right + p.Right, inset);
    }

    /// <summary>
    /// A subgrid's items' sizes in its parent's tracks. Each takes the subgrid's margin, border and
    /// padding as extra margin where it touches the subgrid's edge, or the half gap difference
    /// where it touches an inner gap; an empty edge track still holds the subgrid's edge (CSS Grid 2, 9).
    /// </summary>
    private static IEnumerable<(int Start, int Span, double Min, double Max)> FromSubgrid(GridItem item, int axis, IEnumerable<(int Start, int Span, double Min, double Max)> inner, (double Start, double End, double Inset) edges)
    {
        int count = item.Span[axis];
        foreach ((int start, int span, double min, double max) in inner)
        {
            double extra = (start == 0 ? edges.Start : edges.Inset) + (start + span >= count ? edges.End : edges.Inset);
            yield return (item.Start[axis] + start, span, min + extra, max + extra);
        }

        if (count == 1)
        {
            yield return (item.Start[axis], 1, edges.Start + edges.End, edges.Start + edges.End);
        }
        else
        {
            yield return (item.Start[axis], 1, edges.Start, edges.Start);
            yield return (item.Start[axis] + count - 1, 1, edges.End, edges.End);
        }
    }

    /// <summary>The items' min-content and max-content contributions to the columns, a subgrid's through its own items.</summary>
    private List<(int Start, int Span, double Min, double Max)> ColumnContributions(GridPlan plan)
    {
        var result = new List<(int, int, double, double)>();
        foreach (GridItem item in plan.Items)
        {
            if (item.Subgrid is { Columns: SubgridAxis axis, Plan: GridPlan inner })
            {
                result.AddRange(FromSubgrid(item, 1, ColumnContributions(inner), SubgridEdges(item.Box, axis, rows: false, 0)));
                continue;
            }

            (double min, double max) = Intrinsic(item.Box);
            result.Add((item.Column, item.ColumnSpan, min, max));
        }

        return result;
    }

    private Placed LayoutGrid(GridBox box, double cbX, double cbWidth, double cursorY, MarginCollapse pending, double? cbHeight, double? forcedWidth)
    {
        ComputedStyle s = box.Style;
        _subgrids.TryGetValue(box, out SubgridFrame? frame);
        SubgridAxis? subColumns = frame?.Columns, subRows = frame?.Rows;
        Edges margin = BoxMetrics.Margins(s, cbWidth);
        Edges border = BoxMetrics.Borders(s);
        Edges padding = BoxMetrics.Paddings(s, cbWidth);
        double width = forcedWidth ?? ResolveWidth(box, cbWidth, ref margin, border, padding);
        double? height = SpecifiedHeight(s, cbHeight, border, padding);

        // A subgrid fills its area in the dimensions it adopts, whatever its own size says.
        bool measuring = subRows is not null && subRows.Starts is null;
        if (subRows is not null) height = measuring ? null : Math.Max(0, subRows.Extent - margin.Vertical - border.Vertical - padding.Vertical);
        double columnGap = SubgridGap(s, "column-gap", subColumns, width), rowGap = SubgridGap(s, "row-gap", subRows, height ?? 0);

        GridPlan plan = frame?.Plan ?? PlaceGrid(box, width, height, columnGap, rowGap, frame);
        if (frame is not null) _subgrids.Remove(box);
        List<GridItem> items = plan.Items;
        int rowCount = plan.Rows.Total;
        double y = cursorY + (pending + margin.Top).Value;
        double contentX = cbX + margin.Left + border.Left + padding.Left;
        double contentTop = y + border.Top + padding.Top;

        TrackLines columns = subColumns is { Starts: double[] cs, Ends: double[] ce }
            ? new TrackLines([.. cs.Select(x => x + cbX)], [.. ce.Select(x => x + cbX)], contentX, contentX + width, (columnGap - subColumns.Gap) / 2, true)
            : TrackLines.Sized(contentX, SizeTracks(plan.Columns.Tracks, ColumnContributions(plan), width, columnGap), columnGap);

        // A subgrid's rows are its parent's, known up front once the parent has sized them.
        double origin = y - margin.Top;
        TrackLines? rows = subRows is { Starts: double[] rs, Ends: double[] re }
            ? new TrackLines([.. rs.Select(v => v + origin)], [.. re.Select(v => v + origin)], contentTop, contentTop + height!.Value, (rowGap - subRows.Gap) / 2, true)
            : null;

        // Items laid out in their column areas give the rows their content heights. A subgrid
        // sharing rows still to be sized is first only measured: its items' heights join this grid's own.
        foreach (GridItem item in items)
        {
            (double areaX, double areaWidth) = columns.Area(item.Column, item.ColumnSpan);
            Hand(item, columns, rows, areaX, rows?.Area(item.Row, item.RowSpan).Start ?? 0);
            item.Fragment = LayoutGridItem(item, areaWidth, s);
            if (measuring && item.Subgrid?.Rows is null) _gridMeasured[item.Box] = (areaWidth, item.Fragment);
        }

        var fragment = new BoxFragment(box) { X = cbX + margin.Left, Y = y, Border = border, Padding = padding, Margin = margin };
        if (rows is null)
        {
            var rowContributions = new List<(int Start, int Span, double Min, double Max)>();
            foreach (GridItem item in items)
            {
                if (item.Subgrid?.Rows is SubgridAxis inner)
                {
                    double basis = columns.Area(item.Column, item.ColumnSpan).Size;
                    rowContributions.AddRange(FromSubgrid(item, 0, inner.Measured.Select(m => (m.Start, m.Span, m.Size, m.Size)), SubgridEdges(item.Box, inner, rows: true, basis)));
                    continue;
                }

                double size = item.Fragment.Height + item.Fragment.Margin.Vertical;
                rowContributions.Add((item.Row, item.RowSpan, size, size));
            }

            if (measuring)
            {
                // The parent sizes the rows: report the sizes in this subgrid's own tracks.
                subRows!.Measured.Clear();
                subRows.Measured.AddRange(rowContributions.Select(c => (c.Start, c.Span, c.Max)));
                fragment.Width = width + border.Horizontal + padding.Horizontal;
                return new Placed(fragment, MarginCollapse.None + margin.Bottom, false);
            }

            rows = TrackLines.Sized(contentTop, SizeTracks(plan.Rows.Tracks, rowContributions, height, rowGap), rowGap);

            // Subgrids sharing the rows are laid out again, now that their tracks are known.
            foreach (GridItem item in items.Where(i => i.Subgrid?.Rows is not null))
            {
                (double areaX, double areaWidth) = columns.Area(item.Column, item.ColumnSpan);
                Hand(item, columns, rows, areaX, rows.Area(item.Row, item.RowSpan).Start);
                item.Fragment = LayoutGridItem(item, areaWidth, s);
            }
        }

        double contentHeight = height ?? Math.Max(0, rowCount == 0 ? 0 : rows.Ends[rowCount - 1] - contentTop);

        foreach (GridItem item in items)
        {
            (double areaX, double areaWidth) = columns.Area(item.Column, item.ColumnSpan);
            (double areaY, double areaHeight) = rows.Area(item.Row, item.RowSpan);
            BoxFragment f = item.Fragment;
            Edges m = f.Margin;
            string justify = item.Subgrid?.Columns is null ? GridSelf(item.Box.Style, s, "justify") : "stretch";
            string align = item.Subgrid?.Rows is null ? GridSelf(item.Box.Style, s, "align") : "stretch";
            if (align == "stretch" && item.Subgrid?.Rows is null && BoxMetrics.IsAuto(item.Box.Style, "height")) f.Height = Math.Max(f.Height, areaHeight - m.Vertical);
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
            .Select(line => rows.Ends[line - 1])];

        contentHeight = ClampHeight(s, contentHeight, cbHeight, border, padding);
        fragment.Width = width + border.Horizontal + padding.Horizontal;
        fragment.Height = contentHeight + border.Vertical + padding.Vertical;
        fragment.FirstBaseline = items.OrderBy(i => i.Row).ThenBy(i => i.Column).Select(i => i.Fragment.FirstBaseline).FirstOrDefault(b => b is not null);
        fragment.LastBaseline = fragment.FirstBaseline;
        Offset(fragment, s, cbWidth, cbHeight);
        return new Placed(fragment, MarginCollapse.None + margin.Bottom, false);
    }

    /// <summary>
    /// Gives a subgrid item its parent's tracks over its area, relative to the area's start; rows
    /// are left unsized while the parent measures them.
    /// </summary>
    private void Hand(GridItem item, TrackLines columns, TrackLines? rows, double areaX, double areaY)
    {
        if (item.Subgrid is not SubgridFrame sub) return;
        if (sub.Columns is SubgridAxis c)
        {
            c.Starts = [.. columns.Starts.Skip(item.Column).Take(item.ColumnSpan).Select(x => x - areaX)];
            c.Ends = [.. columns.Ends.Skip(item.Column).Take(item.ColumnSpan).Select(x => x - areaX)];
            c.Extent = columns.Area(item.Column, item.ColumnSpan).Size;
        }

        if (sub.Rows is SubgridAxis r)
        {
            r.Starts = rows is null ? null : [.. rows.Starts.Skip(item.Row).Take(item.RowSpan).Select(v => v - areaY)];
            r.Ends = rows is null ? null : [.. rows.Ends.Skip(item.Row).Take(item.RowSpan).Select(v => v - areaY)];
            r.Extent = rows?.Area(item.Row, item.RowSpan).Size ?? 0;
        }

        _subgrids[(GridBox)item.Box] = sub;
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
        // An item a measuring subgrid already laid out at this width is not laid out again.
        if (_gridMeasured.TryGetValue(item.Box, out var measured))
        {
            _gridMeasured.Remove(item.Box);
            if (measured.Width == areaWidth) return measured.Fragment;
        }

        ComputedStyle cs = item.Box.Style;
        Edges m = BoxMetrics.Margins(cs, areaWidth);
        Edges border = BoxMetrics.Borders(cs);
        Edges padding = BoxMetrics.Paddings(cs, areaWidth);
        double frame = border.Horizontal + padding.Horizontal;
        bool subgrid = item.Subgrid?.Columns is not null;
        double? specified = subgrid ? null : DefiniteSize(cs, "width", areaWidth, border, padding, horizontal: true);
        double contentWidth;
        if (specified is double w) contentWidth = w;
        else if (subgrid || GridSelf(cs, container, "justify") == "stretch") contentWidth = areaWidth - m.Horizontal - frame;
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
        GridPlan plan = PlaceGrid(box, null, null, gap, Gap(s, "row-gap", 0), null);
        List<(int Start, int Span, double Min, double Max)> contributions = ColumnContributions(plan);
        double gaps = gap * Math.Max(0, plan.Columns.Total - 1);
        double max = SizeTracks(plan.Columns.Tracks, contributions, null, gap).Sum() + gaps;
        double min = SizeTracks(plan.Columns.Tracks, contributions.Select(c => (c.Start, c.Span, c.Min, c.Min)), null, gap).Sum() + gaps;
        return (min, Math.Max(min, max));
    }
}
