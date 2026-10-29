using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Table layout (CSS 2.1, 17; the column width algorithm of CSS Tables 3, which browsers share):
/// a grid from rows and spans, column widths from the cells' intrinsic widths, rows as tall as
/// their tallest cell, then vertical alignment inside cells. Both border models are supported.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Limits that keep hostile spans from claiming unbounded memory or time.
    private const int MaxColumns = 1000;
    private const int MaxGridSlots = 4_000_000;

    private sealed record CellSlot(TableCellBox Box, int Row, int Column, int RowSpan, int ColumnSpan);

    private sealed class TableGrid
    {
        public List<(TableRowBox Row, TableRowGroupBox Group)> Rows { get; } = [];

        public List<CellSlot> Cells { get; } = [];

        public List<(TableRowGroupBox Group, int First, int Count)> Groups { get; } = [];

        /// <summary>The col or colgroup box that sets each column's width, when there is one.</summary>
        public Dictionary<int, Box> ColumnBoxes { get; } = [];

        public int ColumnCount { get; set; }

        /// <summary>The cell covering each slot, for collapsed border neighbours.</summary>
        public Dictionary<long, CellSlot> Slots { get; } = [];
    }

    // A grid slot as one number. Not (row << 32 | column): a long hashes as its two halves XORed,
    // so that layout puts slots with equal row ^ column in one bucket and every lookup degrades to a
    // scan. Columns stay below 1024 (MaxColumns, MaxTracks), so row * 1024 + column is unique and
    // spreads well.
    private const int ColumnsPerRow = 1024;

    private static long Key(int row, int column) => ((long)row * ColumnsPerRow) + column;

    /// <summary>Places cells in the grid (CSS 2.1, 17.5): header group first, footer group last, spans reserving slots.</summary>
    private static TableGrid BuildGrid(TableBox table)
    {
        var grid = new TableGrid();
        List<TableRowGroupBox> groups = table.Children.OfType<TableRowGroupBox>().ToList();
        TableRowGroupBox? header = groups.FirstOrDefault(g => g.IsHeader);
        TableRowGroupBox? footer = groups.FirstOrDefault(g => g.IsFooter);
        var ordered = new List<TableRowGroupBox>();
        if (header is not null) ordered.Add(header);
        ordered.AddRange(groups.Where(g => g != header && g != footer));
        if (footer is not null) ordered.Add(footer);

        int slots = 0;
        foreach (TableRowGroupBox group in ordered)
        {
            List<TableRowBox> rows = group.Children.OfType<TableRowBox>().ToList();
            grid.Groups.Add((group, grid.Rows.Count, rows.Count));
            for (int r = 0; r < rows.Count; r++)
            {
                int rowIndex = grid.Rows.Count;
                grid.Rows.Add((rows[r], group));
                int column = 0;
                foreach (TableCellBox cell in rows[r].Children.OfType<TableCellBox>())
                {
                    while (grid.Slots.ContainsKey(Key(rowIndex, column))) column++;
                    if (column >= MaxColumns) break;

                    // A row span stops at the end of its row group.
                    int rowSpan = Math.Max(1, Math.Min(cell.RowSpan, rows.Count - r));
                    int columnSpan = Math.Max(1, Math.Min(cell.ColumnSpan, MaxColumns - column));
                    if (slots + ((long)rowSpan * columnSpan) > MaxGridSlots) rowSpan = columnSpan = 1;
                    slots += rowSpan * columnSpan;

                    var slot = new CellSlot(cell, rowIndex, column, rowSpan, columnSpan);
                    for (int dr = 0; dr < rowSpan; dr++)
                    {
                        for (int dc = 0; dc < columnSpan; dc++) grid.Slots[Key(rowIndex + dr, column + dc)] = slot;
                    }

                    grid.Cells.Add(slot);
                    column += columnSpan;
                    grid.ColumnCount = Math.Max(grid.ColumnCount, column);
                }
            }
        }

        // Columns from col and colgroup elements, which may also add empty columns.
        int index = 0;
        void Column(Box box, int span)
        {
            for (int k = 0; k < span && index < MaxColumns; k++) grid.ColumnBoxes[index++] = box;
        }

        foreach (Box child in table.Children)
        {
            if (child is TableColumnGroupBox group)
            {
                if (group.Children.Count == 0) Column(group, group.Span);
                foreach (TableColumnBox col in group.Children.OfType<TableColumnBox>()) Column(col, col.Span);
            }
            else if (child is TableColumnBox col)
            {
                Column(col, col.Span);
            }
        }

        grid.ColumnCount = Math.Max(grid.ColumnCount, index);
        return grid;
    }

    private static bool Collapsed(TableBox table) => table.Style.Keyword("border-collapse") == "collapse";

    /// <summary>border-spacing: one length for both directions, or horizontal then vertical. None when borders collapse.</summary>
    private static (double H, double V) Spacing(TableBox table)
    {
        if (Collapsed(table)) return (0, 0);
        List<double> lengths = table.Style["border-spacing"]
            .Where(v => v.IsToken && v.Token.Type is CssTokenType.Dimension or CssTokenType.Number)
            .Select(v => Math.Max(0, v.Token.Type == CssTokenType.Number ? 0 : v.Token.Number))
            .ToList();
        return lengths.Count switch
        {
            0 => (0, 0),
            1 => (lengths[0], lengths[0]),
            _ => (lengths[0], lengths[1]),
        };
    }

    /// <summary>The table's own border and padding: none when borders collapse, the outer cells draw them.</summary>
    private static (Edges Border, Edges Padding) TableFrame(TableBox table, double basis) =>
        Collapsed(table) ? (default, default) : (BoxMetrics.Borders(table.Style), BoxMetrics.Paddings(table.Style, basis));

    /// <summary>A specified width in px that does not depend on the containing block, with the box's frame included.</summary>
    private static double? FixedBorderBoxWidth(ComputedStyle s)
    {
        if (BoxMetrics.IsAuto(s, "width") || HasPercent(s, "width") || s.Length("width") is not double w) return null;
        if (s.Keyword("box-sizing") == "border-box") return w;
        return w + BoxMetrics.Borders(s).Horizontal + BoxMetrics.Paddings(s, 0).Horizontal;
    }

    private static double? PercentWidth(ComputedStyle s) =>
        s["width"].Count == 1 && s["width"][0].IsToken && s["width"][0].Token.Type == CssTokenType.Percentage ? s["width"][0].Token.Number : null;

    private sealed class ColumnWidths(int n)
    {
        public double[] Min { get; } = new double[n];

        public double[] Max { get; } = new double[n];

        public double?[] Percent { get; } = new double?[n];

        /// <summary>Columns with a specified width: they take extra space only when nothing else can.</summary>
        public bool[] Constrained { get; } = new bool[n];

        public double SumMin => Min.Sum();

        public double SumMax => Max.Sum();
    }

    /// <summary>The border edges of each cell when borders collapse: the widths it lays out with, and what it draws.</summary>
    private static Dictionary<CellSlot, (Edges Border, CollapsedEdge[] Edges)>? CollapsedCells(TableGrid grid, TableBox table)
    {
        if (!Collapsed(table)) return null;
        int n = grid.ColumnCount;
        var result = new Dictionary<CellSlot, (Edges, CollapsedEdge[])>();
        foreach (CellSlot cell in grid.Cells)
        {
            Side[] sides = CollapsedSides(grid, cell, table);

            // Inner edges are shared: each cell takes half. Outer edges belong to the cell whole.
            double Part(Side side, bool outer) => outer ? side.Width : side.Width / 2;
            var border = new Edges(
                Part(sides[0], cell.Row == 0),
                Part(sides[1], cell.Column + cell.ColumnSpan == n),
                Part(sides[2], cell.Row + cell.RowSpan == grid.Rows.Count),
                Part(sides[3], cell.Column == 0));
            result[cell] = (border, [.. sides.Select(e => new CollapsedEdge(e.Style, e.Color))]);
        }

        return result;
    }

    /// <summary>Minimum and maximum widths per column from the cells that span it (CSS Tables 3, 3.9).</summary>
    private ColumnWidths MeasureColumns(TableGrid grid, double hs, Dictionary<CellSlot, (Edges Border, CollapsedEdge[] Edges)>? collapsed)
    {
        (double Min, double Max) Contribution(CellSlot cell)
        {
            (double min, double max) = CellIntrinsic(cell.Box);
            if (collapsed is null) return (min, max);

            // Measured with the cell's own borders; it lays out with the resolved ones.
            double delta = collapsed[cell].Border.Horizontal - BoxMetrics.Borders(cell.Box.Style).Horizontal;
            return (Math.Max(0, min + delta), Math.Max(0, max + delta));
        }

        var c = new ColumnWidths(grid.ColumnCount);
        foreach (KeyValuePair<int, Box> col in grid.ColumnBoxes)
        {
            if (FixedBorderBoxWidth(col.Value.Style) is double w)
            {
                c.Max[col.Key] = Math.Max(c.Max[col.Key], w);
                c.Constrained[col.Key] = true;
            }

            if (PercentWidth(col.Value.Style) is double p) c.Percent[col.Key] = Math.Max(c.Percent[col.Key] ?? 0, p);
        }

        foreach (CellSlot cell in grid.Cells.Where(s => s.ColumnSpan == 1))
        {
            (double min, double max) = Contribution(cell);
            int k = cell.Column;
            c.Min[k] = Math.Max(c.Min[k], min);
            c.Max[k] = Math.Max(c.Max[k], max);
            if (FixedBorderBoxWidth(cell.Box.Style) is not null) c.Constrained[k] = true;
            if (PercentWidth(cell.Box.Style) is double p) c.Percent[k] = Math.Max(c.Percent[k] ?? 0, p);
        }

        for (int k = 0; k < c.Max.Length; k++) c.Max[k] = Math.Max(c.Max[k], c.Min[k]);

        // Spanning cells, narrowest span first, spread what the spanned columns lack in proportion to their maximums.
        foreach (CellSlot cell in grid.Cells.Where(s => s.ColumnSpan > 1).OrderBy(s => s.ColumnSpan))
        {
            (double min, double max) = Contribution(cell);
            double gaps = hs * (cell.ColumnSpan - 1);
            Spread(c.Min, cell.Column, cell.ColumnSpan, min - gaps, c.Max);
            Spread(c.Max, cell.Column, cell.ColumnSpan, max - gaps, c.Max);
            for (int k = cell.Column; k < cell.Column + cell.ColumnSpan; k++) c.Max[k] = Math.Max(c.Max[k], c.Min[k]);
        }

        return c;
    }

    private static void Spread(double[] widths, int first, int count, double needed, double[] weights)
    {
        double current = 0, weight = 0;
        for (int k = first; k < first + count; k++)
        {
            current += widths[k];
            weight += weights[k];
        }

        double extra = needed - current;
        if (extra <= 0) return;
        for (int k = first; k < first + count; k++) widths[k] += weight > 0 ? extra * weights[k] / weight : extra / count;
    }

    /// <summary>A cell's min and max content contributions: a min-width raises both, a fixed width sets the maximum.</summary>
    private (double Min, double Max) CellIntrinsic(TableCellBox cell)
    {
        (double min, double max) = Intrinsic(cell);
        if (cell.Style.Length("min-width") is double minWidth && !HasPercent(cell.Style, "min-width"))
        {
            min = Math.Max(min, minWidth);
            max = Math.Max(max, minWidth);
        }

        if (FixedBorderBoxWidth(cell.Style) is double fixedWidth) max = Math.Max(min, fixedWidth);
        return (min, max);
    }

    /// <summary>The intrinsic widths of a table's border box.</summary>
    private (double Min, double Max) TableIntrinsic(TableBox table)
    {
        TableGrid grid = BuildGrid(table);
        (double hs, _) = Spacing(table);
        ColumnWidths c = MeasureColumns(grid, hs, CollapsedCells(grid, table));
        (Edges border, Edges padding) = TableFrame(table, 0);
        double frame = border.Horizontal + padding.Horizontal + (hs * (grid.ColumnCount + 1));
        double min = c.SumMin + frame, max = c.SumMax + frame;
        if (FixedBorderBoxWidth(table.Style) is double w) (min, max) = (Math.Max(min, w), Math.Max(min, w));
        return (min, max);
    }

    /// <summary>Column widths for a target sum (CSS Tables 3, 3.9.3): percentages first, then the rest between min and max.</summary>
    private static double[] DistributeWidths(ColumnWidths c, double target, double tableWidth)
    {
        int n = c.Min.Length;
        var widths = new double[n];
        bool[] percent = [.. c.Percent.Select(p => p is not null)];
        double used = 0;
        for (int k = 0; k < n; k++)
        {
            if (!percent[k]) continue;
            widths[k] = Math.Max(c.Min[k], c.Percent[k]!.Value / 100 * tableWidth);
            used += widths[k];
        }

        double remaining = target - used;
        double sumMin = 0, sumMax = 0;
        for (int k = 0; k < n; k++)
        {
            if (percent[k]) continue;
            sumMin += c.Min[k];
            sumMax += c.Max[k];
        }

        if (remaining >= sumMax)
        {
            for (int k = 0; k < n; k++)
            {
                if (!percent[k]) widths[k] = c.Max[k];
            }

            // The extra goes to flexible columns by their maximums; failing those, to any column.
            double extra = remaining - sumMax;
            int[] receivers = Enumerable.Range(0, n).Where(k => !percent[k] && !c.Constrained[k]).ToArray();
            if (receivers.Length == 0) receivers = Enumerable.Range(0, n).Where(k => !percent[k]).ToArray();
            if (receivers.Length == 0) receivers = Enumerable.Range(0, n).ToArray();
            double weight = receivers.Sum(k => widths[k]);
            foreach (int k in receivers) widths[k] += weight > 0 ? extra * widths[k] / weight : extra / receivers.Length;
        }
        else
        {
            double t = remaining <= sumMin || sumMax <= sumMin ? 0 : (remaining - sumMin) / (sumMax - sumMin);
            for (int k = 0; k < n; k++)
            {
                if (!percent[k]) widths[k] = c.Min[k] + ((c.Max[k] - c.Min[k]) * t);
            }
        }

        return widths;
    }

    /// <summary>table-layout: fixed (CSS 2.1, 17.5.2.1): widths from columns and the first row only.</summary>
    private static double[] FixedWidths(TableGrid grid, double target)
    {
        int n = grid.ColumnCount;
        var widths = new double?[n];
        foreach (KeyValuePair<int, Box> col in grid.ColumnBoxes)
        {
            if (FixedBorderBoxWidth(col.Value.Style) is double w) widths[col.Key] = w;
            else if (PercentWidth(col.Value.Style) is double p) widths[col.Key] = p / 100 * target;
        }

        foreach (CellSlot cell in grid.Cells.Where(c => c.Row == 0))
        {
            double? w = FixedBorderBoxWidth(cell.Box.Style) ?? (PercentWidth(cell.Box.Style) is double p ? p / 100 * target : null);
            if (w is not double width) continue;
            for (int k = cell.Column; k < cell.Column + cell.ColumnSpan; k++) widths[k] ??= width / cell.ColumnSpan;
        }

        double assigned = widths.Sum(w => w ?? 0);
        int free = widths.Count(w => w is null);
        double share = free > 0 ? Math.Max(0, target - assigned) / free : 0;
        return [.. widths.Select(w => w ?? share)];
    }

    private readonly record struct Side(double Width, string Style, CssColor Color, int Source);

    private static int StyleRank(string style) => style switch
    {
        "double" => 8,
        "solid" => 7,
        "dashed" => 6,
        "dotted" => 5,
        "ridge" => 4,
        "outset" => 3,
        "groove" => 2,
        "inset" => 1,
        _ => 0,
    };

    private static Side SideOf(ComputedStyle s, string side, int source) =>
        new(BoxMetrics.Border(s, side), s.Keyword("border-" + side + "-style"), s.Color("border-" + side + "-color"), source);

    /// <summary>The winning border between candidates (CSS 2.1, 17.6.2.1): hidden, then width, style, and cell over row over group over table.</summary>
    private static Side Winner(IEnumerable<Side> candidates)
    {
        Side best = new(0, "none", CssColor.Transparent, -1);
        foreach (Side s in candidates)
        {
            if (s.Style == "hidden") return new Side(0, "hidden", CssColor.Transparent, s.Source);
            if (s.Width <= 0) continue;
            if (s.Width > best.Width
                || (s.Width == best.Width && StyleRank(s.Style) > StyleRank(best.Style))
                || (s.Width == best.Width && StyleRank(s.Style) == StyleRank(best.Style) && s.Source > best.Source))
            {
                best = s;
            }
        }

        return best;
    }

    /// <summary>The four resolved edges of a cell, full widths, top right bottom left.</summary>
    private static Side[] CollapsedSides(TableGrid grid, CellSlot cell, TableBox table)
    {
        const int Cell = 4, Row = 3, Group = 2, Table = 1;
        int lastRow = cell.Row + cell.RowSpan - 1, lastColumn = cell.Column + cell.ColumnSpan - 1;
        (TableRowBox firstRow, TableRowGroupBox firstGroup) = grid.Rows[cell.Row];
        (TableRowBox endRow, TableRowGroupBox endGroup) = grid.Rows[lastRow];

        var top = new List<Side> { SideOf(cell.Box.Style, "top", Cell), SideOf(firstRow.Style, "top", Row) };
        if (cell.Row == 0) top.Add(SideOf(table.Style, "top", Table));
        else
        {
            (TableRowBox above, TableRowGroupBox aboveGroup) = grid.Rows[cell.Row - 1];
            top.Add(SideOf(above.Style, "bottom", Row));
            if (aboveGroup != firstGroup) top.Add(SideOf(aboveGroup.Style, "bottom", Group));

            for (int k = cell.Column; k <= lastColumn; k++)
            {
                if (grid.Slots.TryGetValue(Key(cell.Row - 1, k), out CellSlot? n)) top.Add(SideOf(n.Box.Style, "bottom", Cell));
            }
        }

        if (cell.Row == 0 || grid.Rows[cell.Row - 1].Group != firstGroup) top.Add(SideOf(firstGroup.Style, "top", Group));

        var bottom = new List<Side> { SideOf(cell.Box.Style, "bottom", Cell), SideOf(endRow.Style, "bottom", Row) };
        if (lastRow == grid.Rows.Count - 1) bottom.Add(SideOf(table.Style, "bottom", Table));
        else
        {
            (TableRowBox below, TableRowGroupBox belowGroup) = grid.Rows[lastRow + 1];
            bottom.Add(SideOf(below.Style, "top", Row));
            if (belowGroup != endGroup) bottom.Add(SideOf(belowGroup.Style, "top", Group));
            for (int k = cell.Column; k <= lastColumn; k++)
            {
                if (grid.Slots.TryGetValue(Key(lastRow + 1, k), out CellSlot? n)) bottom.Add(SideOf(n.Box.Style, "top", Cell));
            }
        }

        if (lastRow == grid.Rows.Count - 1 || grid.Rows[lastRow + 1].Group != endGroup) bottom.Add(SideOf(endGroup.Style, "bottom", Group));

        var left = new List<Side> { SideOf(cell.Box.Style, "left", Cell) };
        var right = new List<Side> { SideOf(cell.Box.Style, "right", Cell) };
        for (int r = cell.Row; r <= lastRow; r++)
        {
            if (cell.Column == 0)
            {
                left.Add(SideOf(grid.Rows[r].Row.Style, "left", Row));
                left.Add(SideOf(grid.Rows[r].Group.Style, "left", Group));
            }
            else if (grid.Slots.TryGetValue(Key(r, cell.Column - 1), out CellSlot? n))
            {
                left.Add(SideOf(n.Box.Style, "right", Cell));
            }

            if (lastColumn == grid.ColumnCount - 1)
            {
                right.Add(SideOf(grid.Rows[r].Row.Style, "right", Row));
                right.Add(SideOf(grid.Rows[r].Group.Style, "right", Group));
            }
            else if (grid.Slots.TryGetValue(Key(r, lastColumn + 1), out CellSlot? n))
            {
                right.Add(SideOf(n.Box.Style, "left", Cell));
            }
        }

        if (cell.Column == 0) left.Add(SideOf(table.Style, "left", Table));
        if (lastColumn == grid.ColumnCount - 1) right.Add(SideOf(table.Style, "right", Table));
        return [Winner(top), Winner(right), Winner(bottom), Winner(left)];
    }

    /// <summary>Lays out a table and its captions in a wrapper box that holds the margins.</summary>
    private Placed LayoutTable(TableBox table, double cbX, double cbWidth, double cursorY, MarginCollapse pending, bool shrink)
    {
        ComputedStyle s = table.Style;
        TableGrid grid = BuildGrid(table);
        (double hs, double vs) = Spacing(table);
        Edges margin = BoxMetrics.Margins(s, cbWidth);
        (Edges border, Edges padding) = TableFrame(table, cbWidth);
        int n = grid.ColumnCount;
        double gaps = hs * (n + 1);
        double frame = border.Horizontal + padding.Horizontal;
        Dictionary<CellSlot, (Edges Border, CollapsedEdge[] Edges)>? collapsedCells = CollapsedCells(grid, table);
        ColumnWidths columns = MeasureColumns(grid, hs, collapsedCells);

        // The table's border box: a specified width (never below the minimum), else shrink to fit the available width.
        double? specified = BoxMetrics.IsAuto(s, "width") ? null : s.Length("width", cbWidth);
        if (specified is double sw && s.Keyword("box-sizing") != "border-box") specified = sw + frame;
        double available = cbWidth - margin.Horizontal;
        double minWidth = columns.SumMin + gaps + frame, maxWidth = columns.SumMax + gaps + frame;
        double tableWidth = specified is double w ? Math.Max(w, minWidth) : Math.Max(minWidth, Math.Min(maxWidth, available));
        double target = Math.Max(0, tableWidth - gaps - frame);
        double[] widths = s.Keyword("table-layout") == "fixed" && specified is not null && n > 0
            ? FixedWidths(grid, target)
            : DistributeWidths(columns, target, target);
        if (s.Keyword("table-layout") == "fixed" && specified is not null) tableWidth = Math.Max(tableWidth, widths.Sum() + gaps + frame);

        // Auto margins centre the table; otherwise the end margin gives, as for blocks.
        bool autoLeft = BoxMetrics.IsAuto(s, "margin-left"), autoRight = BoxMetrics.IsAuto(s, "margin-right");
        if (!shrink && autoLeft && autoRight) margin = margin with { Left = Math.Max(0, cbWidth - tableWidth) / 2, Right = Math.Max(0, cbWidth - tableWidth) / 2 };
        else if (!shrink && autoLeft) margin = margin with { Left = cbWidth - tableWidth - margin.Right };

        MarginCollapse top = pending + margin.Top;
        double wrapperX = cbX + margin.Left;
        double y = cursorY + top.Value;
        var wrapper = new BoxFragment(new TableWrapperBox(table)) { X = wrapperX, Y = y, Width = tableWidth, Margin = margin };

        // Captions above the table, as wide as the table.
        double cursor = y;
        foreach (TableCaptionBox caption in table.Children.OfType<TableCaptionBox>().Where(c => c.Style.Keyword("caption-side") != "bottom"))
        {
            Placed placed = LayoutBlockLevel(caption, wrapperX, tableWidth, cursor, MarginCollapse.None, null);
            wrapper.Children.Add(placed.Fragment);
            cursor = placed.Fragment.Bottom + placed.Bottom.Value;
        }

        var fragment = new BoxFragment(table) { X = wrapperX, Y = cursor, Width = tableWidth, Border = border, Padding = padding };
        double contentX = wrapperX + border.Left + padding.Left;
        double contentTop = cursor + border.Top + padding.Top;
        var columnX = new double[n + 1];
        columnX[0] = contentX + hs;
        for (int k = 0; k < n; k++) columnX[k + 1] = columnX[k] + widths[k] + hs;

        // Cells first at y = 0, to learn their heights.
        var cells = new BoxFragment[grid.Cells.Count];
        for (int i = 0; i < grid.Cells.Count; i++)
        {
            CellSlot cell = grid.Cells[i];
            double cellWidth = columnX[cell.Column + cell.ColumnSpan] - columnX[cell.Column] - hs;
            Edges? cellBorder = null;
            CollapsedEdge[]? edges = null;
            if (collapsedCells is not null) (cellBorder, edges) = collapsedCells[cell];

            Edges cellPadding = BoxMetrics.Paddings(cell.Box.Style, target);
            double content = Math.Max(0, cellWidth - (cellBorder ?? BoxMetrics.Borders(cell.Box.Style)).Horizontal - cellPadding.Horizontal);
            BoxFragment f = LayoutBlockLevel(cell.Box, columnX[cell.Column], cellWidth, 0, MarginCollapse.None, null, content, cellBorder).Fragment;
            f.CollapsedBorders = edges;
            cells[i] = f;
        }

        // Row heights: the tallest single-row cell or the row's own height; then spanning cells stretch their last row.
        int rows = grid.Rows.Count;
        var heights = new double[rows];
        for (int r = 0; r < rows; r++) heights[r] = Math.Max(0, grid.Rows[r].Row.Style.Length("height") ?? 0);
        for (int i = 0; i < cells.Length; i++)
        {
            if (grid.Cells[i].RowSpan == 1) heights[grid.Cells[i].Row] = Math.Max(heights[grid.Cells[i].Row], cells[i].Height);
        }

        foreach (int i in Enumerable.Range(0, cells.Length).Where(i => grid.Cells[i].RowSpan > 1).OrderBy(i => grid.Cells[i].RowSpan))
        {
            CellSlot cell = grid.Cells[i];
            double spanned = (vs * (cell.RowSpan - 1)) + Enumerable.Range(cell.Row, cell.RowSpan).Sum(r => heights[r]);
            if (cells[i].Height > spanned) heights[cell.Row + cell.RowSpan - 1] += cells[i].Height - spanned;
        }

        // A taller specified table height goes to the last row.
        double rowsHeight = heights.Sum() + (vs * (rows + 1));
        if (s.Length("height") is double tableHeight && !HasPercent(s, "height"))
        {
            double inner = tableHeight - (s.Keyword("box-sizing") == "border-box" ? border.Vertical + padding.Vertical : 0);
            if (inner > rowsHeight && rows > 0)
            {
                heights[rows - 1] += inner - rowsHeight;
                rowsHeight = inner;
            }
        }

        var rowY = new double[rows];
        double next = contentTop + vs;
        for (int r = 0; r < rows; r++)
        {
            rowY[r] = next;
            next += heights[r] + vs;
        }

        // Place cells: stretch each to its rows, then align its content (CSS 2.1, 17.5.3).
        var baselines = new Dictionary<int, double>();
        for (int i = 0; i < cells.Length; i++)
        {
            CellSlot cell = grid.Cells[i];
            BoxFragment f = cells[i];
            f.Translate(0, rowY[cell.Row] - f.Y);
            if (cell.RowSpan == 1 && cell.Box.Style.Keyword("vertical-align") == "baseline")
            {
                double offset = (f.FirstBaseline ?? f.ContentY + f.ContentHeight) - f.Y;
                baselines[cell.Row] = Math.Max(baselines.TryGetValue(cell.Row, out double b) ? b : 0, offset);
            }
        }

        for (int i = 0; i < cells.Length; i++)
        {
            CellSlot cell = grid.Cells[i];
            BoxFragment f = cells[i];
            double height = (vs * (cell.RowSpan - 1)) + Enumerable.Range(cell.Row, cell.RowSpan).Sum(r => heights[r]);
            double extra = Math.Max(0, height - f.Height);
            double shift = cell.Box.Style.Keyword("vertical-align") switch
            {
                "middle" => extra / 2,
                "bottom" => extra,
                "baseline" when cell.RowSpan == 1 => Math.Min(extra, baselines[cell.Row] - ((f.FirstBaseline ?? f.ContentY + f.ContentHeight) - f.Y)),
                _ => 0,
            };
            if (shift > 0) ShiftContent(f, shift);
            f.Height = height;
        }

        // Row groups and rows around the cells, for backgrounds, borders and page breaks.
        double rowWidth = Math.Max(0, columnX[n] - columnX[0] - hs);
        foreach ((TableRowGroupBox group, int first, int count) in grid.Groups)
        {
            if (count == 0) continue;
            var groupFragment = new BoxFragment(group)
            {
                X = columnX[0],
                Y = rowY[first],
                Width = rowWidth,
                Height = rowY[first + count - 1] + heights[first + count - 1] - rowY[first],
            };
            for (int r = first; r < first + count; r++)
            {
                var rowFragment = new BoxFragment(grid.Rows[r].Row) { X = columnX[0], Y = rowY[r], Width = rowWidth, Height = heights[r] };
                for (int i = 0; i < cells.Length; i++)
                {
                    if (grid.Cells[i].Row == r) rowFragment.Children.Add(cells[i]);
                }

                rowFragment.FirstBaseline = rowFragment.Children.OfType<BoxFragment>().Select(c => c.FirstBaseline).FirstOrDefault(b => b is not null);
                groupFragment.Children.Add(rowFragment);
            }

            fragment.Children.Add(groupFragment);
        }

        fragment.Height = rowsHeight + border.Vertical + padding.Vertical;
        fragment.FirstBaseline = fragment.Children.OfType<BoxFragment>().Select(g => g.Children.OfType<BoxFragment>().FirstOrDefault()?.FirstBaseline).FirstOrDefault(b => b is not null);
        fragment.LastBaseline = fragment.FirstBaseline;
        wrapper.Children.Add(fragment);
        cursor = fragment.Bottom;

        foreach (TableCaptionBox caption in table.Children.OfType<TableCaptionBox>().Where(c => c.Style.Keyword("caption-side") == "bottom"))
        {
            Placed placed = LayoutBlockLevel(caption, wrapperX, tableWidth, cursor, MarginCollapse.None, null);
            wrapper.Children.Add(placed.Fragment);
            cursor = placed.Fragment.Bottom + placed.Bottom.Value;
        }

        wrapper.Height = cursor - y;
        wrapper.FirstBaseline = fragment.FirstBaseline;
        wrapper.LastBaseline = fragment.LastBaseline;
        return new Placed(wrapper, MarginCollapse.None + margin.Bottom, false);
    }

    /// <summary>Moves a box's content without moving the box, for vertical alignment in cells.</summary>
    private static void ShiftContent(BoxFragment f, double dy)
    {
        foreach (Fragment child in f.Children) child.Translate(0, dy);
        f.Marker?.Translate(0, dy);
        if (f.FirstBaseline is double first) f.FirstBaseline = first + dy;
        if (f.LastBaseline is double last) f.LastBaseline = last + dy;
    }
}
