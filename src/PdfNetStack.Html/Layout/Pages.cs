using PdfNetStack.Html.Css;

namespace PdfNetStack.Html.Layout;

/// <summary>A page box in CSS px: its size and margins, and its style for margin boxes and backgrounds.</summary>
internal sealed record PageGeometry(double Width, double Height, Edges Margin, ComputedStyle Style)
{
    public double ContentWidth => Math.Max(1, Width - Margin.Horizontal);

    public double ContentHeight => Math.Max(1, Height - Margin.Vertical);
}

/// <summary>
/// Page boxes from @page rules (CSS Paged Media 3): size, margins and margin boxes, with
/// :first, :left, :right and :blank matched by page number and the rules cascaded by
/// specificity, then order.
/// </summary>
internal sealed class PageMaster
{
    // 14400 PDF units, the largest page side, in CSS px.
    private const double MaxPageSide = 19200;

    // Named sizes in mm, or inches for the North American ones (CSS Paged Media 3, 7.2.1).
    private static readonly Dictionary<string, (double W, double H, bool Inches)> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a5"] = (148, 210, false), ["a4"] = (210, 297, false), ["a3"] = (297, 420, false),
        ["b5"] = (176, 250, false), ["b4"] = (250, 353, false), ["jis-b5"] = (182, 257, false), ["jis-b4"] = (257, 364, false),
        ["letter"] = (8.5, 11, true), ["legal"] = (8.5, 14, true), ["ledger"] = (11, 17, true),
    };

    private readonly StyleResolver _resolver;
    private readonly ComputedStyle _root;
    private readonly List<CssDeclaration> _defaults;
    private readonly Dictionary<(int, string), PageGeometry> _cache = [];

    /// <param name="defaults">Declarations below every @page rule, such as the converter's size and margins.</param>
    public PageMaster(StyleResolver resolver, ComputedStyle root, string defaults)
    {
        _resolver = resolver;
        _root = root;
        _defaults = CssParser.ParseDeclarations(defaults);
    }

    /// <summary>The @page rules for a page: unnamed rules always, named ones for pages of that name; a name outweighs a pseudo-class.</summary>
    private IEnumerable<(CssPageRule Rule, int Specificity, int Order)> Matching(int index, string? name)
    {
        // The first page is a right page in left to right documents.
        bool right = (index % 2 == 0) != (_root.Keyword("direction") == "rtl");
        for (int i = 0; i < _resolver.PageRules.Count; i++)
        {
            CssPageRule rule = _resolver.PageRules[i];
            if (rule.Name is not null && !string.Equals(rule.Name, name, StringComparison.Ordinal)) continue;
            int specificity = rule.Pseudo switch
            {
                null => 0,
                "first" when index == 0 => 2,
                "left" when !right => 1,
                "right" when right => 1,
                _ => -1,
            };
            if (specificity >= 0) yield return (rule, specificity + (rule.Name is null ? 0 : 4), i);
        }
    }

    /// <param name="index">The page's position in the document, from 0.</param>
    /// <param name="name">The page name from the page property of its content, or null.</param>
    public PageGeometry Geometry(int index, string? name = null)
    {
        if (_cache.TryGetValue((index, name ?? ""), out PageGeometry? cached)) return cached;
        List<CssDeclaration> declarations = [.. _defaults];
        foreach (var match in Matching(index, name).OrderBy(m => m.Specificity).ThenBy(m => m.Order)) declarations.AddRange(match.Rule.Declarations);

        // Size first: percentages in margins are of the page width.
        List<CssValue>? size = declarations.LastOrDefault(d => d.Property == "size")?.Value;
        (double width, double height) = PageSize(size);
        ComputedStyle style = _resolver.ComputeDeclarations(declarations.Where(d => d.Property != "size"), _root);
        var geometry = new PageGeometry(width, height, BoxMetrics.Margins(style, width), style);
        _cache[(index, name ?? "")] = geometry;
        return geometry;
    }

    internal (double Width, double Height) PageSize(List<CssValue>? value) => PageSize(value, _root.FontSize);

    /// <summary>The page size in px: a named size, one or two lengths, and an orientation.</summary>
    internal static (double Width, double Height) PageSize(List<CssValue>? value, double fontSize)
    {
        (double w, double h) = (793.7007874, 1122.519685); // A4
        if (value is null) return (w, h);
        var lengths = new List<double>();
        bool? landscape = null;
        foreach (CssValue v in value)
        {
            if (v.IsWhitespace) continue;
            if (v.IsToken && v.Token.Type == CssTokenType.Ident)
            {
                string k = v.Token.Value.ToLowerInvariant();
                if (k == "landscape") landscape = true;
                else if (k == "portrait") landscape = false;
                else if (Sizes.TryGetValue(k, out var named))
                {
                    double scale = named.Inches ? 96 : 96 / 25.4;
                    (w, h) = (named.W * scale, named.H * scale);
                }
            }
            else if (v.IsToken && v.Token.Type == CssTokenType.Dimension && ToPx(v.Token, fontSize) is double px && px > 0)
            {
                lengths.Add(px);
            }
        }

        if (lengths.Count == 1) (w, h) = (lengths[0], lengths[0]);
        else if (lengths.Count >= 2) (w, h) = (lengths[0], lengths[1]);
        if (landscape == true && h > w) (w, h) = (h, w);
        if (landscape == false && w > h) (w, h) = (h, w);

        // PDF pages are 3 to 14400 units on a side (ISO 32000-1, Annex C).
        return (Math.Max(4, Math.Min(MaxPageSide, w)), Math.Max(4, Math.Min(MaxPageSide, h)));
    }

    private static double? ToPx(CssToken t, double fontSize) => t.Unit switch
    {
        "px" => t.Number,
        "cm" => t.Number * 96 / 2.54,
        "mm" => t.Number * 96 / 25.4,
        "q" => t.Number * 96 / 101.6,
        "in" => t.Number * 96,
        "pt" => t.Number * 96 / 72,
        "pc" => t.Number * 16,
        "em" or "rem" => t.Number * fontSize,
        _ => null,
    };

    /// <summary>The margin boxes of a page that have content: name, style and content value.</summary>
    public List<(string Name, ComputedStyle Style, List<CssValue> Content)> MarginBoxes(int index, string? pageName = null)
    {
        PageGeometry page = Geometry(index, pageName);
        var boxes = new Dictionary<string, List<CssDeclaration>>(StringComparer.Ordinal);
        foreach (var match in Matching(index, pageName).OrderBy(m => m.Specificity).ThenBy(m => m.Order))
        {
            foreach (KeyValuePair<string, List<CssDeclaration>> box in match.Rule.MarginBoxes)
            {
                if (!boxes.TryGetValue(box.Key, out List<CssDeclaration>? list)) boxes[box.Key] = list = [];
                list.AddRange(box.Value);
            }
        }

        var result = new List<(string, ComputedStyle, List<CssValue>)>();
        foreach (KeyValuePair<string, List<CssDeclaration>> box in boxes)
        {
            // Boxes align toward their side unless their rules say otherwise (CSS Paged Media 3, 5.3.2).
            string align = box.Key.EndsWith("-left", StringComparison.Ordinal) || box.Key.StartsWith("left-", StringComparison.Ordinal) ? "left"
                : box.Key.EndsWith("-right", StringComparison.Ordinal) || box.Key.StartsWith("right-", StringComparison.Ordinal) ? "right"
                : "center";
            List<CssDeclaration> declarations = [.. CssParser.ParseDeclarations("text-align: " + align), .. box.Value];
            ComputedStyle style = _resolver.ComputeDeclarations(declarations, page.Style);
            List<CssValue> content = style["content"];
            if (content.Count == 1 && (content[0].IsIdent("normal") || content[0].IsIdent("none"))) continue;
            result.Add((box.Key, style, content));
        }

        return result;
    }
}

/// <summary>
/// Splits the laid out canvas into pages (CSS Fragmentation 3): breaks go between lines and
/// between blocks, forced breaks first, then the lowest allowed break that fits; break-inside,
/// break-before, break-after, orphans and widows mark breaks to avoid, which are used only when
/// nothing else fits. Margins at a break are truncated.
/// </summary>
internal static class Paginator
{
    private const double Epsilon = 0.01;

    /// <summary>
    /// The part of the canvas a page shows, from <see cref="Top"/> to <see cref="Bottom"/>, below
    /// the table headers repeated at the top of the page.
    /// </summary>
    internal sealed record Slice(double Top, double Bottom, IReadOnlyList<RepeatedHeader> Headers)
    {
        /// <summary>The room the repeated headers take above the content.</summary>
        public double Inset => Headers.Sum(h => h.Advance);
    }

    /// <summary>A table header group to repeat, and how far the content below it moves down.</summary>
    internal sealed record RepeatedHeader(BoxFragment Header, double Advance);

    private sealed record Candidate(double Break, double Resume, bool Forced)
    {
        public bool Avoid { get; set; }
    }

    // A table's header group repeats on pages that start between its end and the end of the body rows.
    private sealed record Repeat(BoxFragment Header, double Start, double End, double Advance);

    /// <param name="root">The laid out canvas.</param>
    /// <param name="pageHeight">The page area height of each page, from the first page of the canvas.</param>
    /// <param name="maxPages">The most pages allowed.</param>
    /// <param name="start">Where the pages start, when the canvas holds content before them that other pages show.</param>
    /// <param name="last">The box whose end is the end of the pages, when content after it shows on other pages.</param>
    /// <exception cref="HtmlConversionException">The canvas needs more than <paramref name="maxPages"/> pages.</exception>
    public static List<Slice> Paginate(BoxFragment root, Func<int, double> pageHeight, int maxPages, double? start = null, BoxFragment? last = null)
    {
        var candidates = new List<Candidate>();
        var avoid = new List<(double Top, double Bottom)>();
        var repeats = new List<Repeat>();
        Collect(root, candidates, avoid, repeats);
        foreach (Candidate c in candidates)
        {
            if (avoid.Any(r => c.Break > r.Top + Epsilon && c.Break < r.Bottom - Epsilon)) c.Avoid = true;
        }

        candidates.Sort((x, y) => x.Break.CompareTo(y.Break));
        double end = ContentBottom(root);
        if (last is not null)
        {
            // Content past the last box shows on the next run's pages, and so do its breaks.
            end = Math.Max(last.Bottom, ContentBottom(last));
            candidates.RemoveAll(c => c.Break > end + Epsilon);
        }

        // A forced break before the first content or after the last would leave a page empty:
        // the pages start and end there anyway.
        double top = start ?? root.Y;
        (double first, double final) = Content(root, top);
        candidates.RemoveAll(c => c.Forced && (c.Break <= first + Epsilon || c.Break >= final - Epsilon));
        var slices = new List<Slice>();
        while (top < end - Epsilon || slices.Count == 0)
        {
            // Every page takes another slice, so the limit also ends a canvas whose positions are
            // too large for a page height to move them.
            if (slices.Count >= maxPages) throw new HtmlConversionException($"The document needs more than {maxPages} pages (HtmlConverterOptions.MaxPages).");
            double height = pageHeight(slices.Count);
            List<RepeatedHeader> headers = repeats
                .Where(r => top > r.Start + Epsilon && top < r.End - Epsilon)
                .OrderBy(r => r.Start)
                .Select(r => new RepeatedHeader(r.Header, r.Advance))
                .ToList();

            // Headers that would fill most of the page are not repeated, so the page still makes progress.
            if (headers.Sum(h => h.Advance) > height / 2) headers.Clear();
            double limit = top + height - headers.Sum(h => h.Advance);

            Candidate? forced = candidates.FirstOrDefault(c => c.Forced && c.Break > top + Epsilon && c.Break <= limit + Epsilon && c.Resume > top + Epsilon);
            if (forced is null && limit >= end - Epsilon)
            {
                slices.Add(new Slice(top, Math.Max(end, top), headers));
                break;
            }

            Candidate? chosen = forced
                ?? candidates.LastOrDefault(c => !c.Avoid && Fits(c, top, limit))
                ?? candidates.LastOrDefault(c => Fits(c, top, limit));
            if (chosen is null)
            {
                // Nothing breakable fits: cut through, so a tall image or line spans pages.
                slices.Add(new Slice(top, limit, headers));
                top = limit;
                continue;
            }

            slices.Add(new Slice(top, chosen.Break, headers));
            top = chosen.Resume;
        }

        return slices;
    }

    private static bool Fits(Candidate c, double top, double limit) =>
        !c.Forced && c.Break > top + Epsilon && c.Break <= limit + Epsilon && c.Resume > top + Epsilon;

    private static bool IsForced(string value) => value is "page" or "left" or "right" or "recto" or "verso";

    private static bool IsAvoid(string value) => value is "avoid" or "avoid-page";

    private static void Collect(BoxFragment box, List<Candidate> candidates, List<(double Top, double Bottom)> avoid, List<Repeat> repeats)
    {
        if (IsAvoid(box.Style.Keyword("break-inside"))) avoid.Add((box.Y, box.Bottom));

        if (box.Box is TableBox)
        {
            List<BoxFragment> groups = box.Children.OfType<BoxFragment>().ToList();
            if (groups.Count > 1 && groups[0].Box is TableRowGroupBox { IsHeader: true })
            {
                BoxFragment header = groups[0];
                BoxFragment lastBody = groups.LastOrDefault(g => g.Box is TableRowGroupBox { IsFooter: false, IsHeader: false }) ?? header;
                repeats.Add(new Repeat(header, header.Bottom, lastBody.Bottom, groups[1].Y - header.Y));
            }
        }

        // A grid breaks only between rows that no item spans.
        if (box.Box is GridBox)
        {
            foreach (double line in box.BreakLines ?? []) candidates.Add(new Candidate(line, line, false));
            return;
        }

        // A flex row is not split: its items sit side by side.
        if (box.Box is FlexBox && box.Style.Keyword("flex-direction") is "row" or "row-reverse") return;

        // Rows are not split: their cells sit side by side. A cell spanning rows keeps them together.
        if (box.Box is TableRowBox)
        {
            foreach (Fragment cell in box.Children)
            {
                if (cell.Bottom > box.Bottom + Epsilon) avoid.Add((box.Y, cell.Bottom));
            }

            return;
        }

        List<LineFragment> lines = box.Children.OfType<LineFragment>().ToList();
        if (lines.Count > 1)
        {
            int orphans = Math.Max(1, (int)box.Style.Number("orphans"));
            int widows = Math.Max(1, (int)box.Style.Number("widows"));
            for (int k = 1; k < lines.Count; k++)
            {
                candidates.Add(new Candidate(lines[k - 1].Bottom, lines[k].Y, false) { Avoid = k < orphans || lines.Count - k < widows });
            }
        }

        // Floats and positioned boxes are not in the flow: breaks go between in-flow siblings and never through them.
        var blocks = new List<BoxFragment>();
        foreach (BoxFragment child in box.Children.OfType<BoxFragment>())
        {
            bool outOfFlow = child.Style.Keyword("float") is not ("none" or "") || child.Style.Keyword("position") is "absolute" or "fixed";
            if (outOfFlow) avoid.Add((child.Y, child.Bottom));
            else blocks.Add(child);
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            BoxFragment child = blocks[i];
            string before = child.Style.Keyword("break-before");
            if (i == 0)
            {
                if (IsForced(before)) candidates.Add(new Candidate(child.Y, child.Y, true));
            }
            else
            {
                BoxFragment previous = blocks[i - 1];
                string after = previous.Style.Keyword("break-after");
                bool forced = IsForced(after) || IsForced(before);
                bool header = previous.Box is TableRowGroupBox { IsHeader: true };
                candidates.Add(new Candidate(previous.Bottom, Math.Max(previous.Bottom, child.Y), forced) { Avoid = !forced && (header || IsAvoid(after) || IsAvoid(before)) });
            }

            Collect(child, candidates, avoid, repeats);
        }

        if (blocks.Count > 0 && IsForced(blocks[^1].Style.Keyword("break-after")))
        {
            candidates.Add(new Candidate(blocks[^1].Bottom, blocks[^1].Bottom, true));
        }
    }

    /// <summary>The lowest visible content: lines, leaves, and boxes with a background or border.</summary>
    private static double ContentBottom(BoxFragment box)
    {
        double bottom = double.MinValue;
        if (box.Children.Count == 0 || box.Border.Vertical > 0 || box.Style.Color("background-color").A > 0 || box.Style.Keyword("background-image") != "none")
        {
            bottom = box.Bottom;
        }

        foreach (Fragment child in box.Children)
        {
            bottom = Math.Max(bottom, child is BoxFragment b ? ContentBottom(b) : child.Bottom);
        }

        return bottom == double.MinValue ? box.Y : bottom;
    }

    /// <summary>
    /// The top of the highest and the bottom of the lowest line or leaf box at or below
    /// <paramref name="from"/>. A container's own border or background does not count: a forced
    /// break before its first child or after its last is a break before or after it.
    /// </summary>
    private static (double Top, double Bottom) Content(BoxFragment box, double from)
    {
        double top = double.MaxValue, bottom = double.MinValue;
        if (box.Children.Count == 0 && box.Height > Epsilon && box.Y >= from - Epsilon) (top, bottom) = (box.Y, box.Bottom);
        foreach (Fragment child in box.Children)
        {
            (double t, double b) = child is BoxFragment f ? Content(f, from) : child.Y >= from - Epsilon ? (child.Y, child.Bottom) : (double.MaxValue, double.MinValue);
            top = Math.Min(top, t);
            bottom = Math.Max(bottom, b);
        }

        return (top, bottom);
    }
}

/// <summary>
/// Named pages (CSS Paged Media 3, 8.1): splits the box tree into runs of one used page name. The
/// page property of any block-level box in the block flow counts; auto takes its parent's. Where
/// the page name changes between two siblings, the page breaks (CSS Fragmentation 3, 4.1) and the
/// next run is laid out at the width of its own page type. A run holds the boxes on its side of the
/// breaks; a box with content on both sides goes in each, sliced at the break, so its start edge
/// shows only in the first run and its end edge only in the last.
/// </summary>
internal sealed class NamedPages
{
    private readonly Dictionary<Box, (int First, int Last)> _range = [];
    private readonly List<string?> _names = [];
    private readonly List<(Box Before, Box After)> _breaks = [];
    private readonly List<List<(Box Box, List<Box> Children)>> _split = [];
    private Box? _previous;

    public NamedPages(BlockBox root)
    {
        Walk(root, null);

        // For each run, the boxes it shares with others and their children on its side of the breaks.
        for (int r = 0; r < _names.Count; r++) _split.Add([]);
        foreach (KeyValuePair<Box, (int First, int Last)> box in _range.Where(b => b.Value.First < b.Value.Last).ToList())
        {
            for (int r = box.Value.First; r <= box.Value.Last; r++) _split[r].Add((box.Key, []));
            foreach (Box child in box.Key.Children)
            {
                if (!_range.TryGetValue(child, out (int First, int Last) range)) continue;
                for (int r = range.First; r <= range.Last; r++) _split[r][^1].Children.Add(child);
            }
        }
    }

    /// <summary>The number of runs: at least one.</summary>
    public int Count => Math.Max(1, _names.Count);

    /// <summary>The page name of a run, null for the unnamed page.</summary>
    public string? Name(int run) => run < _names.Count ? _names[run] : null;

    /// <summary>The box right after the break that starts a run, or null for the first run.</summary>
    public Box? First(int run) => run > 0 ? _breaks[run - 1].After : null;

    /// <summary>The box right before the break that ends a run, or null for the last run.</summary>
    public Box? Last(int run) => run < _breaks.Count ? _breaks[run].Before : null;

    /// <summary>The page property as a name; auto takes the parent's.</summary>
    private static string? PageName(Box box)
    {
        List<CssValue> v = box.Style["page"];
        return v.Count == 1 && v[0].IsToken && v[0].Token.Type == CssTokenType.Ident && !v[0].IsIdent("auto") ? v[0].Token.Value : null;
    }

    /// <summary>Floats, absolutely positioned and inline-level children are outside the block flow and force no break.</summary>
    private static bool InFlow(Box box) =>
        box.IsBlockLevel && box.Style.Keyword("float") is "none" or "" && box.Style.Keyword("position") is not ("absolute" or "fixed");

    /// <summary>A block container whose children are in the block flow; other boxes are leaves, kept whole in one run.</summary>
    private static bool Descends(Box box) =>
        box.GetType() == typeof(BlockBox) && box.Children.Any(InFlow) && !box.Children.All(c => c.IsInlineLevel);

    private void Walk(Box box, string? inherited)
    {
        string? name = PageName(box) ?? inherited;
        if (!Descends(box))
        {
            // A leaf joins the current run, or starts the next one when its page name differs.
            if (_names.Count == 0 || _names[^1] != name)
            {
                if (_previous is not null) _breaks.Add(Siblings(_previous, box));
                _names.Add(name);
            }

            _previous = box;
            _range[box] = (_names.Count - 1, _names.Count - 1);
            return;
        }

        foreach (Box child in box.Children.Where(InFlow)) Walk(child, name);
        List<(int First, int Last)> ranges = [.. box.Children.Where(InFlow).Select(c => _range[c])];
        _range[box] = (ranges[0].First, ranges[^1].Last);

        // A child outside the block flow stays with the in-flow sibling before it, else the one after.
        int? before = null;
        var pending = new List<Box>();
        foreach (Box child in box.Children)
        {
            if (!InFlow(child))
            {
                if (before is int run) _range[child] = (run, run);
                else pending.Add(child);
                continue;
            }

            foreach (Box waiting in pending) _range[waiting] = (_range[child].First, _range[child].First);
            pending.Clear();
            before = _range[child].Last;
        }
    }

    /// <summary>The siblings a break between two leaves falls between: their ancestors under the nearest common one.</summary>
    private static (Box Before, Box After) Siblings(Box before, Box after)
    {
        var chain = new List<Box>();
        for (Box? b = before; b is not null; b = b.Parent) chain.Add(b);
        var ancestors = new HashSet<Box>(chain);
        Box next = after;
        while (next.Parent is not null && !ancestors.Contains(next.Parent)) next = next.Parent;
        int common = next.Parent is null ? chain.Count : chain.IndexOf(next.Parent);
        return (chain[Math.Max(0, common - 1)], next);
    }

    /// <summary>
    /// Cuts the tree down to one run's boxes until the result is disposed. A list item continued
    /// from an earlier run shows no marker.
    /// </summary>
    public IDisposable Keep(int run)
    {
        var restore = new List<(Box Box, List<Box> Children, Box? Marker)>();
        foreach ((Box box, List<Box> children) in run < _split.Count ? _split[run] : [])
        {
            Box? marker = box is BlockBox block ? block.Marker : null;
            restore.Add((box, [.. box.Children], marker));
            box.SetChildren(children);
            if (run > _range[box].First && box is BlockBox continued) continued.Marker = null;
        }

        return new Restore(restore);
    }

    /// <summary>
    /// Where a run starts on its canvas: above the box after the break by its top margin, and those
    /// of first children collapsing with it, as margins after a forced break are kept (CSS
    /// Fragmentation 3, 5.2). Edges of boxes continued from the run before stay above, cut off.
    /// </summary>
    public static double Start(BoxFragment first)
    {
        double top = first.Y - Math.Max(0, first.Margin.Top);
        for (BoxFragment? f = first; f is not null && f.Border.Top == 0 && f.Padding.Top == 0;)
        {
            f = f.Children.FirstOrDefault() as BoxFragment;
            if (f is not null) top = Math.Min(top, f.Y - Math.Max(0, f.Margin.Top));
        }

        return top;
    }

    /// <summary>The outermost fragment of a box in a laid out run: a table's wrapper rather than the table.</summary>
    public static BoxFragment? Find(BoxFragment root, Box box)
    {
        var stack = new Stack<BoxFragment>([root]);
        while (stack.Count > 0)
        {
            BoxFragment f = stack.Pop();
            if (f.Box == box || (f.Box is TableWrapperBox wrapper && wrapper.Table == box)) return f;
            for (int i = f.Children.Count - 1; i >= 0; i--)
            {
                if (f.Children[i] is BoxFragment child) stack.Push(child);
            }
        }

        return null;
    }

    private sealed class Restore(List<(Box Box, List<Box> Children, Box? Marker)> boxes) : IDisposable
    {
        public void Dispose()
        {
            foreach ((Box box, List<Box> children, Box? marker) in boxes)
            {
                box.SetChildren(children);
                if (box is BlockBox block) block.Marker = marker;
            }
        }
    }
}
