using System.Globalization;
using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Builds the box tree from the document and its computed styles (CSS 2.1, 9.2 and 17.2.1; CSS
/// Display 3): boxes by display type, generated content and list markers with counters, then
/// the fix-ups layout relies on (anonymous blocks, inlines split around blocks, table wrappers,
/// collapsed white space).
/// </summary>
internal sealed class BoxBuilder
{
    private const int MaxSpan = 1000;

    private readonly StyleResolver _resolver;
    private readonly Func<string, HtmlImage?>? _loadImage;
    private readonly CounterScopes _counters = new();
    private int _quoteDepth;

    public BoxBuilder(StyleResolver resolver, Func<string, HtmlImage?>? loadImage = null)
    {
        _resolver = resolver;
        _loadImage = loadImage;
    }

    /// <summary>Builds the tree for a document; the root box belongs to the html element.</summary>
    public BlockBox Build(HtmlDocument document)
    {
        List<Box> top = Element(document.Html, null);
        BlockBox root;
        if (top.Count == 1 && top[0] is BlockBox block)
        {
            root = block;
        }
        else
        {
            root = new BlockBox(_resolver.Compute(document.Html, null), document.Html) { IsAnonymous = true };
            root.SetChildren(top);
        }

        Fix(root);
        Collapse(root);
        return root;
    }

    private List<Box> Element(HtmlElement e, ComputedStyle? parentStyle)
    {
        ComputedStyle style = _resolver.Compute(e, parentStyle);
        string display = style.Keywords("display");
        if (display == "none") return [];

        _counters.Enter();
        try
        {
            ApplyCounters(e, style, display);
            if (e.Name == "br") return [new LineBreakBox(style, e)];
            if (e.Name == "img") return Image(e, style);
            if (e.Name == "svg") return InlineSvg(e, style);

            Box? marker = display.Contains("list-item") ? Marker(e, style) : null;
            var children = new List<Box>();
            if (Generated(e, style, "before") is Box before) children.Add(before);
            if (FormControlText(e) is string control)
            {
                children.Add(new TextBox(style, control));
            }
            else
            {
                foreach (HtmlNode node in e.Children)
                {
                    if (node is HtmlText t && t.Data.Length > 0) children.Add(new TextBox(style, t.Data));
                    else if (node is HtmlElement c) children.AddRange(Element(c, style));
                }
            }

            if (Generated(e, style, "after") is Box after) children.Add(after);
            if (display == "contents") return children;

            Box box = Create(e, style, display);
            box.SetChildren(children);
            if (marker is not null && box is BlockBox list)
            {
                if (style.Keyword("list-style-position") == "inside") list.Insert(0, marker);
                else list.Marker = marker;
            }

            return [box];
        }
        finally
        {
            _counters.Leave();
        }
    }

    private static Box Create(HtmlElement? e, ComputedStyle style, string display)
    {
        return display switch
        {
            "inline-block" or "inline flow-root" => new InlineBlockBox(style, e),
            "grid" or "block grid" => new GridBox(style, e),
            "inline-grid" or "inline grid" => new GridBox(style, e) { IsInline = true },
            "flex" or "block flex" => new FlexBox(style, e),
            "inline-flex" or "inline flex" => new FlexBox(style, e) { IsInline = true },
            "table" or "block table" => new TableBox(style, e),
            "inline-table" or "inline table" => new TableBox(style, e) { IsInline = true },
            "table-row-group" or "table-header-group" or "table-footer-group" => new TableRowGroupBox(style, e),
            "table-row" => new TableRowBox(style, e),
            "table-cell" => new TableCellBox(style, e)
            {
                ColumnSpan = Span(e?.GetAttribute("colspan"), 1),
                RowSpan = Span(e?.GetAttribute("rowspan"), 1),
            },
            "table-column" => new TableColumnBox(style, e) { Span = Span(e?.GetAttribute("span"), 1) },
            "table-column-group" => new TableColumnGroupBox(style, e) { Span = Span(e?.GetAttribute("span"), 1) },
            "table-caption" => new TableCaptionBox(style, e),
            "inline" or "inline flow" or "ruby" => new InlineBox(style, e),
            _ => new BlockBox(style, e), // block, list-item, flow-root
        };
    }

    private static int Span(string? value, int fallback) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? Math.Min(n, MaxSpan) : fallback;

    private List<Box> Image(HtmlElement e, ComputedStyle style)
    {
        if (e.GetAttribute("src") is string src && _loadImage?.Invoke(src) is HtmlImage image) return [new ReplacedBox(style, e, image)];

        // A missing image shows its alt text.
        string? alt = e.GetAttribute("alt");
        if (string.IsNullOrEmpty(alt)) return [];
        var box = new InlineBox(style, e);
        box.Add(new TextBox(style, alt!));
        return [box];
    }

    /// <summary>Inline SVG: its subtree, written back out as markup, becomes a vector image.</summary>
    private static List<Box> InlineSvg(HtmlElement e, ComputedStyle style)
    {
        var markup = new StringBuilder();
        Serialize(e, markup);
        try
        {
            return [new ReplacedBox(style, e, new HtmlImage(PdfSvg.Parse(markup.ToString())))];
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static void Serialize(HtmlNode node, StringBuilder sb)
    {
        if (node is HtmlText text)
        {
            Escape(text.Data, sb, attribute: false);
            return;
        }

        var element = (HtmlElement)node;
        sb.Append('<').Append(element.Name);
        foreach (KeyValuePair<string, string> a in element.Attributes)
        {
            sb.Append(' ').Append(a.Key).Append("=\"");
            Escape(a.Value, sb, attribute: true);
            sb.Append('"');
        }

        sb.Append('>');
        foreach (HtmlNode child in element.Children) Serialize(child, sb);
        sb.Append("</").Append(element.Name).Append('>');
    }

    private static void Escape(string s, StringBuilder sb, bool attribute)
    {
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' when attribute => "&quot;",
                _ => c.ToString(),
            });
        }
    }

    /// <summary>The text a form control shows, or null for other elements.</summary>
    private static string? FormControlText(HtmlElement e)
    {
        switch (e.Name)
        {
            case "input":
                string type = (e.GetAttribute("type") ?? "text").ToLowerInvariant();
                bool on = e.GetAttribute("checked") is not null;
                return type switch
                {
                    "checkbox" => ((char)(on ? 0x2611 : 0x2610)).ToString(),
                    "radio" => ((char)(on ? 0x25C9 : 0x25CB)).ToString(),
                    "password" => new string((char)0x2022, (e.GetAttribute("value") ?? "").Length),
                    _ => e.GetAttribute("value") ?? "",
                };
            case "select":
                HtmlElement? selected = e.Descendants().FirstOrDefault(o => o.Name == "option" && o.GetAttribute("selected") is not null)
                    ?? e.Descendants().FirstOrDefault(o => o.Name == "option");
                return selected?.TextContent.Trim() ?? "";
            default:
                return null;
        }
    }

    private void ApplyCounters(HtmlElement? e, ComputedStyle style, string display)
    {
        List<(string Name, int Value)> resets = CounterScopes.Parse(style["counter-reset"], 0);
        List<(string Name, int Value)> increments = CounterScopes.Parse(style["counter-increment"], 1);
        List<(string Name, int Value)> sets = CounterScopes.Parse(style["counter-set"], 0);

        // HTML lists (CSS Lists 3, 4.5): ol, ul and menu reset list-item; ol honours start and reversed.
        if (e is not null && e.Name is "ol" or "ul" or "menu" && !resets.Any(r => r.Name == "list-item"))
        {
            int value = 0;
            if (e.Name == "ol")
            {
                int? start = Integer(e.GetAttribute("start"));
                value = e.GetAttribute("reversed") is not null ? (start ?? e.ChildElements.Count(c => c.Name == "li")) + 1 : (start ?? 1) - 1;
            }

            resets.Add(("list-item", value));
        }

        if (display.Contains("list-item") && !increments.Any(i => i.Name == "list-item"))
        {
            bool reversed = e?.ParentElement is { Name: "ol" } list && list.GetAttribute("reversed") is not null;
            increments.Add(("list-item", reversed ? -1 : 1));
        }

        if (e is { Name: "li" } && Integer(e.GetAttribute("value")) is int itemValue && !sets.Any(s => s.Name == "list-item")) sets.Add(("list-item", itemValue));

        foreach ((string name, int value) in resets) _counters.Reset(name, value);
        foreach ((string name, int value) in increments) _counters.Increment(name, value);
        foreach ((string name, int value) in sets) _counters.Set(name, value);
    }

    private static int? Integer(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static bool IsNone(List<CssValue> content) => content.Count == 1 && (content[0].IsIdent("none") || content[0].IsIdent("normal"));

    private Box? Generated(HtmlElement e, ComputedStyle style, string pseudo)
    {
        // content does not inherit and starts as none: no rule, no box.
        if (!_resolver.HasRulesFor(e, pseudo)) return null;
        ComputedStyle ps = _resolver.Compute(e, style, pseudo);
        List<CssValue> content = ps["content"];
        string display = ps.Keywords("display");
        if (IsNone(content) || display == "none") return null;

        _counters.Enter();
        try
        {
            ApplyCounters(null, ps, display);
            Box box = display is "inline" or "contents" ? new InlineBox(ps, null) : Create(null, ps, display);
            box.SetChildren(Content(e, ps, content));
            return box;
        }
        finally
        {
            _counters.Leave();
        }
    }

    private Box? Marker(HtmlElement e, ComputedStyle style)
    {
        ComputedStyle ms = _resolver.Compute(e, style, "marker");
        List<Box> children;
        if (!IsNone(ms["content"]))
        {
            children = Content(e, ms, ms["content"]);
        }
        else if (ImageUrl(style["list-style-image"]) is string url && _loadImage?.Invoke(url) is HtmlImage image)
        {
            children = [new ReplacedBox(_resolver.Anonymous(ms, "inline"), null, image)];
        }
        else
        {
            List<CssValue> type = style["list-style-type"];
            if (type.Count != 1 || !type[0].IsToken) return null;
            string text = type[0].Token.Type == CssTokenType.String
                ? type[0].Token.Value
                : CounterStyle.Format(_counters.Value("list-item"), type[0].Token.Value) + CounterStyle.Suffix(type[0].Token.Value);
            if (text.Length == 0) return null;
            children = [new TextBox(ms, text)];
        }

        var marker = new InlineBox(ms, null);
        marker.SetChildren(children);
        return marker;
    }

    private static string? ImageUrl(List<CssValue> value)
    {
        if (value.Count != 1) return null;
        CssValue v = value[0];
        if (v.IsToken && v.Token.Type == CssTokenType.Url) return v.Token.Value;
        return v.Function == "url" ? v.Children.FirstOrDefault(c => c.IsToken && c.Token.Type == CssTokenType.String)?.Token.Value : null;
    }

    /// <summary>The boxes for a content value (CSS Generated Content 3): strings, images, counters, attributes, quotes.</summary>
    private List<Box> Content(HtmlElement e, ComputedStyle style, List<CssValue> content)
    {
        var boxes = new List<Box>();
        var text = new StringBuilder();
        void FlushText()
        {
            if (text.Length == 0) return;
            boxes.Add(new TextBox(style, text.ToString()));
            text.Clear();
        }

        foreach (CssValue v in content)
        {
            if (v.IsWhitespace) continue;
            if (v.IsToken && v.Token.Type == CssTokenType.String)
            {
                text.Append(v.Token.Value);
            }
            else if (ImageUrl([v]) is string url)
            {
                if (_loadImage?.Invoke(url) is HtmlImage image)
                {
                    FlushText();
                    boxes.Add(new ReplacedBox(_resolver.Anonymous(style, "inline"), null, image));
                }
            }
            else if (v.IsToken && v.Token.Type == CssTokenType.Ident)
            {
                text.Append(Quote(style, v.Token.Value.ToLowerInvariant()));
            }
            else if (v.Function is "counter" or "counters")
            {
                List<CssValue> args = v.Children.Where(c => !c.IsWhitespace && !(c.IsToken && c.Token.Type == CssTokenType.Comma)).ToList();
                if (args.Count == 0 || !args[0].IsToken) continue;
                string name = args[0].Token.Value;
                if (v.Function == "counter")
                {
                    string counterStyle = args.Count > 1 && args[1].IsToken ? args[1].Token.Value : "decimal";
                    text.Append(CounterStyle.Format(_counters.Value(name), counterStyle));
                }
                else
                {
                    string separator = args.Count > 1 && args[1].IsToken ? args[1].Token.Value : "";
                    string counterStyle = args.Count > 2 && args[2].IsToken ? args[2].Token.Value : "decimal";
                    List<int> values = _counters.Values(name).DefaultIfEmpty(0).ToList();
                    text.Append(string.Join(separator, values.Select(n => CounterStyle.Format(n, counterStyle))));
                }
            }
            else if (v.Function == "attr")
            {
                CssValue? name = v.Children.FirstOrDefault(c => !c.IsWhitespace);
                if (name is { IsToken: true }) text.Append(e.GetAttribute(name.Token.Value.ToLowerInvariant()) ?? "");
            }

            // string(), leader(), target-counter() and content() belong to paged media and are resolved there.
        }

        FlushText();
        return boxes;
    }

    private string Quote(ComputedStyle style, string keyword)
    {
        if (keyword is not ("open-quote" or "close-quote" or "no-open-quote" or "no-close-quote")) return "";
        bool open = keyword is "open-quote" or "no-open-quote";
        if (!open) _quoteDepth = Math.Max(0, _quoteDepth - 1);
        int depth = _quoteDepth;
        if (open) _quoteDepth++;
        if (keyword.StartsWith("no-", StringComparison.Ordinal)) return "";

        List<CssValue> quotes = style["quotes"];
        if (quotes.Count == 1 && quotes[0].IsIdent("none")) return "";
        List<string> pairs = quotes.Where(q => q.IsToken && q.Token.Type == CssTokenType.String).Select(q => q.Token.Value).ToList();
        if (pairs.Count < 2)
        {
            // auto: English quotation marks, double outside, single inside.
            pairs = [((char)0x201C).ToString(), ((char)0x201D).ToString(), ((char)0x2018).ToString(), ((char)0x2019).ToString()];
        }

        int pair = Math.Min(depth, (pairs.Count / 2) - 1);
        return pairs[(pair * 2) + (open ? 0 : 1)];
    }

    // Fix-ups, from the root down.
    private void Fix(Box box)
    {
        switch (box)
        {
            case TableBox table:
                FixTable(table);
                break;
            case TableRowGroupBox group:
                WrapRuns(group, b => b is TableRowBox, parent => new TableRowBox(_resolver.Anonymous(parent.Style, "table-row"), null) { IsAnonymous = true });
                break;
            case TableRowBox row:
                WrapRuns(row, b => b is TableCellBox, parent => new TableCellBox(_resolver.Anonymous(parent.Style, "table-cell"), null) { IsAnonymous = true });
                break;
            case TableColumnGroupBox columns:
                columns.SetChildren(columns.Children.Where(c => c is TableColumnBox));
                break;
            case FlexBox or GridBox:
                WrapInlineRuns((BlockBox)box, always: true);
                break;
            case BlockBox block:
                WrapTableParts(block);
                SplitInlines(block);
                WrapInlineRuns(block, always: false);
                break;
        }

        foreach (Box child in box.Children.ToList()) Fix(child);
        if (box is BlockBox { Marker: Box marker }) Fix(marker);
    }

    private static bool IsTablePart(Box b) =>
        b is TableRowGroupBox or TableRowBox or TableCellBox or TableColumnBox or TableColumnGroupBox or TableCaptionBox;

    private static bool IsCollapsibleWhiteSpace(Box b) =>
        b is TextBox t && t.Style.Keyword("white-space") is "normal" or "nowrap" or "" && t.Text.All(c => c is ' ' or '\t' or '\n' or '\r' or '\f');

    private void FixTable(TableBox table)
    {
        // Loose content becomes rows, loose rows become a row group (CSS 2.1, 17.2.1).
        table.SetChildren(table.Children.Where(c => !IsCollapsibleWhiteSpace(c)));
        WrapRuns(
            table,
            b => b is TableRowGroupBox or TableRowBox or TableCaptionBox or TableColumnBox or TableColumnGroupBox,
            parent => new TableRowBox(_resolver.Anonymous(parent.Style, "table-row"), null) { IsAnonymous = true });
        WrapRuns(
            table,
            b => b is not TableRowBox,
            parent => new TableRowGroupBox(_resolver.Anonymous(parent.Style, "table-row-group"), null) { IsAnonymous = true });
    }

    /// <summary>Wraps each run of children that do not satisfy <paramref name="keep"/> in a new box; collapsible white space alone is dropped.</summary>
    private static void WrapRuns(Box parent, Func<Box, bool> keep, Func<Box, Box> wrapper)
    {
        if (parent.Children.All(keep)) return;
        var result = new List<Box>();
        var run = new List<Box>();
        void Flush()
        {
            if (run.Count > 0 && !run.All(IsCollapsibleWhiteSpace))
            {
                Box w = wrapper(parent);
                w.SetChildren(run);
                result.Add(w);
            }

            run = [];
        }

        foreach (Box c in parent.Children)
        {
            if (keep(c))
            {
                Flush();
                result.Add(c);
            }
            else
            {
                run.Add(c);
            }
        }

        Flush();
        parent.SetChildren(result);
    }

    /// <summary>Table parts outside a table get an anonymous table around them.</summary>
    private void WrapTableParts(BlockBox block)
    {
        if (!block.Children.Any(IsTablePart)) return;
        var result = new List<Box>();
        var run = new List<Box>();
        void Flush()
        {
            if (run.Count == 0) return;
            var table = new TableBox(_resolver.Anonymous(block.Style, "table"), null) { IsAnonymous = true };
            table.SetChildren(run);
            result.Add(table);
            run = [];
        }

        foreach (Box c in block.Children)
        {
            if (IsTablePart(c) || (run.Count > 0 && IsCollapsibleWhiteSpace(c))) run.Add(c);
            else
            {
                Flush();
                result.Add(c);
            }
        }

        Flush();
        block.SetChildren(result);
    }

    /// <summary>An inline that contains a block is split into inline parts around it (CSS 2.1, 9.2.1.1).</summary>
    private static void SplitInlines(BlockBox block)
    {
        if (!block.Children.Any(c => c is InlineBox i && ContainsBlock(i))) return;
        block.SetChildren(block.Children.SelectMany(c => c is InlineBox i && ContainsBlock(i) ? Split(i) : [c]));
    }

    private static bool ContainsBlock(InlineBox box) => box.Children.Any(c => c.IsBlockLevel || (c is InlineBox i && ContainsBlock(i)));

    private static List<Box> Split(InlineBox box)
    {
        var pieces = new List<Box>();
        var current = new InlineBox(box.Style, box.Element) { HasStartEdge = box.HasStartEdge, HasEndEdge = false };
        void Block(Box b)
        {
            pieces.Add(current);
            pieces.Add(b);
            current = new InlineBox(box.Style, box.Element) { HasStartEdge = false, HasEndEdge = false };
        }

        foreach (Box child in box.Children.ToList())
        {
            if (child.IsBlockLevel)
            {
                Block(child);
            }
            else if (child is InlineBox inner && ContainsBlock(inner))
            {
                foreach (Box piece in Split(inner))
                {
                    if (piece.IsBlockLevel) Block(piece);
                    else current.Add(piece);
                }
            }
            else
            {
                current.Add(child);
            }
        }

        current.HasEndEdge = box.HasEndEdge;
        pieces.Add(current);
        return pieces;
    }

    /// <summary>
    /// In a block container with block children, or always in a flex container, each run of
    /// inline-level children goes into an anonymous block.
    /// </summary>
    private void WrapInlineRuns(BlockBox block, bool always)
    {
        if (!always && !block.Children.Any(c => c.IsBlockLevel)) return;
        if (!block.Children.Any(c => c.IsInlineLevel)) return;
        WrapRuns(block, b => b.IsBlockLevel, parent => new BlockBox(_resolver.Anonymous(parent.Style, "block"), null) { IsAnonymous = true });
    }

    // White space processing (CSS Text 3, 4.1.1): collapse spaces across the inline boxes of each
    // block container, so "a <b> b</b>" keeps one space. Spaces at line ends go during line layout.
    private static void Collapse(Box box)
    {
        if (box is BlockBox and not TableBox and not FlexBox && box.Children.Count > 0 && box.Children.All(c => c.IsInlineLevel))
        {
            bool previousSpace = true;
            CollapseInline(box, ref previousSpace);
        }

        foreach (Box child in box.Children) Collapse(child);
        if (box is BlockBox { Marker: Box marker })
        {
            bool previousSpace = false;
            CollapseInline(marker, ref previousSpace);
        }
    }

    private static void CollapseInline(Box box, ref bool previousSpace)
    {
        for (int i = 0; i < box.Children.Count; i++)
        {
            switch (box.Children[i])
            {
                case TextBox t:
                    t.Text = CollapseText(t.Text, t.Style, ref previousSpace);
                    if (t.Text.Length == 0) box.Children.RemoveAt(i--);
                    break;
                case LineBreakBox:
                    previousSpace = true;
                    break;
                case InlineBox inline:
                    CollapseInline(inline, ref previousSpace);
                    break;
                default:
                    previousSpace = false;
                    break;
            }
        }
    }

    internal static string CollapseText(string text, ComputedStyle style, ref bool previousSpace)
    {
        text = Transform(text, style.Keyword("text-transform"));
        string ws = style.Keyword("white-space");
        if (ws is "pre" or "pre-wrap" or "break-spaces")
        {
            if (text.Length > 0) previousSpace = false;
            return text;
        }

        bool keepNewlines = ws == "pre-line";
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            char c = ch is '\r' or '\f' or '\t' ? ' ' : ch;
            if (c == '\n')
            {
                if (keepNewlines)
                {
                    // Spaces around a preserved line break are removed.
                    while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                    sb.Append('\n');
                    previousSpace = true;
                    continue;
                }

                c = ' ';
            }

            if (c == ' ')
            {
                if (previousSpace) continue;
                previousSpace = true;
            }
            else
            {
                previousSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Transform(string text, string transform)
    {
        switch (transform)
        {
            case "uppercase":
                return text.ToUpperInvariant();
            case "lowercase":
                return text.ToLowerInvariant();
            case "capitalize":
                var sb = new StringBuilder(text);
                for (int i = 0; i < sb.Length; i++)
                {
                    if (char.IsLetter(sb[i]) && (i == 0 || char.IsWhiteSpace(sb[i - 1]) || sb[i - 1] is '-' or '(' or '"')) sb[i] = char.ToUpperInvariant(sb[i]);
                }

                return sb.ToString();
            default:
                return text;
        }
    }
}
