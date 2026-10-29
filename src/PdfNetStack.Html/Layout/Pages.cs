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
    private readonly Dictionary<int, PageGeometry> _cache = [];

    /// <param name="defaults">Declarations below every @page rule, such as the converter's size and margins.</param>
    public PageMaster(StyleResolver resolver, ComputedStyle root, string defaults)
    {
        _resolver = resolver;
        _root = root;
        _defaults = CssParser.ParseDeclarations(defaults);
    }

    private IEnumerable<(CssPageRule Rule, int Specificity, int Order)> Matching(int index)
    {
        // The first page is a right page in left to right documents.
        bool right = (index % 2 == 0) != (_root.Keyword("direction") == "rtl");
        for (int i = 0; i < _resolver.PageRules.Count; i++)
        {
            CssPageRule rule = _resolver.PageRules[i];
            if (rule.Name is not null) continue; // named pages need the page property, not yet laid out
            int specificity = rule.Pseudo switch
            {
                null => 0,
                "first" when index == 0 => 2,
                "left" when !right => 1,
                "right" when right => 1,
                _ => -1,
            };
            if (specificity >= 0) yield return (rule, specificity, i);
        }
    }

    public PageGeometry Geometry(int index)
    {
        if (_cache.TryGetValue(index, out PageGeometry? cached)) return cached;
        List<CssDeclaration> declarations = [.. _defaults];
        foreach (var match in Matching(index).OrderBy(m => m.Specificity).ThenBy(m => m.Order)) declarations.AddRange(match.Rule.Declarations);

        // Size first: percentages in margins are of the page width.
        List<CssValue>? size = declarations.LastOrDefault(d => d.Property == "size")?.Value;
        (double width, double height) = PageSize(size);
        ComputedStyle style = _resolver.ComputeDeclarations(declarations.Where(d => d.Property != "size"), _root);
        var geometry = new PageGeometry(width, height, BoxMetrics.Margins(style, width), style);
        _cache[index] = geometry;
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
        return (w, h);
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
    public List<(string Name, ComputedStyle Style, List<CssValue> Content)> MarginBoxes(int index)
    {
        PageGeometry page = Geometry(index);
        var boxes = new Dictionary<string, List<CssDeclaration>>(StringComparer.Ordinal);
        foreach (var match in Matching(index).OrderBy(m => m.Specificity).ThenBy(m => m.Order))
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

    /// <summary>The part of the canvas a page shows, from <see cref="Top"/> to <see cref="Bottom"/>.</summary>
    internal readonly record struct Slice(double Top, double Bottom);

    private sealed record Candidate(double Break, double Resume, bool Forced)
    {
        public bool Avoid { get; set; }
    }

    public static List<Slice> Paginate(BoxFragment root, Func<int, double> pageHeight)
    {
        var candidates = new List<Candidate>();
        var avoid = new List<(double Top, double Bottom)>();
        Collect(root, candidates, avoid);
        foreach (Candidate c in candidates)
        {
            if (avoid.Any(r => c.Break > r.Top + Epsilon && c.Break < r.Bottom - Epsilon)) c.Avoid = true;
        }

        candidates.Sort((x, y) => x.Break.CompareTo(y.Break));
        double end = ContentBottom(root);
        var slices = new List<Slice>();
        double top = root.Y;
        while (top < end - Epsilon || slices.Count == 0)
        {
            double limit = top + pageHeight(slices.Count);
            Candidate? forced = candidates.FirstOrDefault(c => c.Forced && c.Break > top + Epsilon && c.Break <= limit + Epsilon && c.Resume > top + Epsilon);
            if (forced is null && limit >= end - Epsilon)
            {
                slices.Add(new Slice(top, Math.Max(end, top)));
                break;
            }

            Candidate? chosen = forced
                ?? candidates.LastOrDefault(c => !c.Avoid && Fits(c, top, limit))
                ?? candidates.LastOrDefault(c => Fits(c, top, limit));
            if (chosen is null)
            {
                // Nothing breakable fits: cut through, so a tall image or line spans pages.
                slices.Add(new Slice(top, limit));
                top = limit;
                continue;
            }

            slices.Add(new Slice(top, chosen.Break));
            top = chosen.Resume;
        }

        return slices;
    }

    private static bool Fits(Candidate c, double top, double limit) =>
        !c.Forced && c.Break > top + Epsilon && c.Break <= limit + Epsilon && c.Resume > top + Epsilon;

    private static bool IsForced(string value) => value is "page" or "left" or "right" or "recto" or "verso";

    private static bool IsAvoid(string value) => value is "avoid" or "avoid-page";

    private static void Collect(BoxFragment box, List<Candidate> candidates, List<(double Top, double Bottom)> avoid)
    {
        if (IsAvoid(box.Style.Keyword("break-inside"))) avoid.Add((box.Y, box.Bottom));

        // Rows and flex lines are not split: their cells sit side by side.
        if (box.Box is TableRowBox) return;

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

        List<BoxFragment> blocks = box.Children.OfType<BoxFragment>().ToList();
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
                candidates.Add(new Candidate(previous.Bottom, Math.Max(previous.Bottom, child.Y), forced) { Avoid = !forced && (IsAvoid(after) || IsAvoid(before)) });
            }

            Collect(child, candidates, avoid);
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
}
