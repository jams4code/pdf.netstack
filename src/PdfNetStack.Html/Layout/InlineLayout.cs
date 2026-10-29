using System.Globalization;
using System.Text;
using PdfNetStack.Fonts;
using PdfNetStack.Html.Css;
using PdfNetStack.Text;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Inline formatting (CSS 2.1, 9.4.2 and 10.8; CSS Text 3; CSS Inline 3). The inline content of
/// a block container becomes one logical string, atomic inlines as U+FFFC, so line breaking
/// (UAX #14), bidi (UAX #9) and shaping see the whole paragraph. Lines are filled greedily, then
/// reordered, aligned and given heights from the strut, line-height and vertical-align.
/// </summary>
internal sealed class InlineLayout
{
    private const char ObjectReplacement = (char)0xFFFC;
    private const char SoftHyphen = (char)0x00AD;
    private const double Epsilon = 1e-6;

    private readonly LayoutEngine _engine;
    private readonly ComputedStyle _container;
    private readonly Box? _containerBox;
    private readonly double _width;
    private readonly bool _intrinsic;
    private readonly bool _rtl;

    private readonly StringBuilder _sb = new();
    private readonly List<ComputedStyle> _styleList = [];
    private readonly List<int> _spanList = [];
    private readonly List<Span> _spans = [];
    private readonly Dictionary<int, Atomic> _atomics = [];

    private string _text = "";
    private char[] _shaping = [];
    private ComputedStyle[] _style = [];
    private int[] _span = [];
    private PdfFont[] _font = [];
    private byte[] _level = [];
    private double[] _advance = [];
    private double[] _startEdge = [];
    private double[] _endEdge = [];
    private double[] _prefix = [];
    private readonly List<Item> _items = [];
    private int[] _itemAt = [];
    private List<LineBreak> _breaks = [];

    private sealed class Span(InlineBox box, int parent, int start, int depth)
    {
        public InlineBox Box { get; } = box;

        public int Parent { get; } = parent;

        public int Start { get; } = start;

        public int End { get; set; }

        public int Depth { get; } = depth;

        // Logical edges: start is the left side in a left to right paragraph.
        public double StartMargin { get; set; }

        public double StartBorder { get; set; }

        public double StartPadding { get; set; }

        public double EndMargin { get; set; }

        public double EndBorder { get; set; }

        public double EndPadding { get; set; }

        public Edges Border { get; set; }

        public Edges Padding { get; set; }

        public double Leading => StartMargin + StartBorder + StartPadding;

        public double Trailing => EndMargin + EndBorder + EndPadding;
    }

    private sealed class Atomic(Box box, BoxFragment? fragment, double width, double above, double below, double min)
    {
        public Box Box { get; } = box;

        public BoxFragment? Fragment { get; } = fragment;

        /// <summary>The margin box width.</summary>
        public double Width { get; } = width;

        /// <summary>From the baseline up to the top margin edge, and down to the bottom margin edge.</summary>
        public double Above { get; } = above;

        public double Below { get; } = below;

        /// <summary>The min-content contribution, for intrinsic sizing.</summary>
        public double Min { get; } = min;
    }

    private sealed class Item(int start, int end, ComputedStyle style, PdfFont font, byte level)
    {
        public int Start { get; } = start;

        public int End { get; } = end;

        public ComputedStyle Style { get; } = style;

        public PdfFont Font { get; } = font;

        public byte Level { get; } = level;

        public List<ShapedGlyph> Glyphs { get; set; } = [];
    }

    private sealed class Segment(Item item, int start, int end)
    {
        public Item Item { get; } = item;

        public int Start { get; } = start;

        public int End { get; } = end;

        public List<ShapedGlyph> Glyphs { get; set; } = [];

        public double Width { get; set; }
    }

    /// <param name="engine">Lays out atomic inlines and resolves fonts.</param>
    /// <param name="container">The block container's style: the strut, alignment and direction.</param>
    /// <param name="containerBox">The container, for text decorations of ancestors; null for markers.</param>
    /// <param name="children">The inline-level boxes.</param>
    /// <param name="width">The available width.</param>
    /// <param name="intrinsic">Measure only: atomic inlines report their intrinsic widths instead of being laid out.</param>
    public InlineLayout(LayoutEngine engine, ComputedStyle container, Box? containerBox, IReadOnlyList<Box> children, double width, bool intrinsic = false)
    {
        _engine = engine;
        _container = container;
        _containerBox = containerBox;
        _width = width;
        _intrinsic = intrinsic;
        _rtl = container.Keyword("direction") == "rtl";

        Walk(children, -1, 0);
        _text = _sb.ToString();
        int n = _text.Length;
        _style = [.. _styleList];
        _span = [.. _spanList];
        _startEdge = new double[n + 1];
        _endEdge = new double[n + 1];
        if (n == 0) return;

        // Edges count toward the width at the first and last character of a box; an empty box puts both at one place.
        foreach (Span s in _spans)
        {
            if (s.End > s.Start)
            {
                _startEdge[s.Start] += s.Leading;
                _endEdge[s.End - 1] += s.Trailing;
            }
            else
            {
                _startEdge[Math.Min(s.Start, n - 1)] += s.Leading + s.Trailing;
            }
        }

        AssignFonts();
        ResolveBidi();
        Itemize();
        Measure();
        FindBreaks();
    }

    public bool IsEmpty => _text.Length == 0;

    private void Walk(IReadOnlyList<Box> boxes, int parent, int depth)
    {
        foreach (Box b in boxes)
        {
            switch (b)
            {
                case TextBox t:
                    Append(t.Text, t.Style, parent);
                    break;
                case LineBreakBox br:
                    Append("\n", br.Style, parent);
                    break;
                case InlineBox ib:
                    var span = new Span(ib, parent, _sb.Length, depth);
                    Edges margin = BoxMetrics.Margins(ib.Style, _width);
                    Edges border = BoxMetrics.Borders(ib.Style);
                    Edges padding = BoxMetrics.Paddings(ib.Style, _width);
                    span.Border = border;
                    span.Padding = padding;
                    if (ib.HasStartEdge)
                    {
                        span.StartMargin = _rtl ? margin.Right : margin.Left;
                        span.StartBorder = _rtl ? border.Right : border.Left;
                        span.StartPadding = _rtl ? padding.Right : padding.Left;
                    }

                    if (ib.HasEndEdge)
                    {
                        span.EndMargin = _rtl ? margin.Left : margin.Right;
                        span.EndBorder = _rtl ? border.Left : border.Right;
                        span.EndPadding = _rtl ? padding.Left : padding.Right;
                    }

                    int index = _spans.Count;
                    _spans.Add(span);
                    Walk(ib.Children, index, depth + 1);
                    span.End = _sb.Length;
                    break;
                default:
                    _atomics[_sb.Length] = LayoutAtomic(b);
                    Append(ObjectReplacement.ToString(), b.Style, parent);
                    break;
            }
        }
    }

    private Atomic LayoutAtomic(Box b)
    {
        if (_intrinsic)
        {
            (double min, double max) = _engine.Intrinsic(b);
            return new Atomic(b, null, max, 0, 0, min);
        }

        BoxFragment f = _engine.LayoutAtomic(b, _width);
        double marginHeight = f.Height + f.Margin.Vertical;
        double width = f.Width + f.Margin.Horizontal;

        // Inline blocks sit on their last line; images and blocks without lines on their bottom margin edge.
        double above = marginHeight;
        if (b is not ReplacedBox && f.LastBaseline is double baseline && b.Style.Keyword("overflow-x") == "visible")
        {
            above = f.Margin.Top + (baseline - f.Y);
        }

        return new Atomic(b, f, width, above, marginHeight - above, width);
    }

    private void Append(string text, ComputedStyle style, int span)
    {
        _sb.Append(text);
        for (int i = 0; i < text.Length; i++)
        {
            _styleList.Add(style);
            _spanList.Add(span);
        }
    }

    private bool IsAtomic(int i) => _text[i] == ObjectReplacement && _atomics.ContainsKey(i);

    private void AssignFonts()
    {
        int n = _text.Length;
        _font = new PdfFont[n];
        _shaping = _text.ToCharArray();
        for (int i = 0; i < n; i++)
        {
            ComputedStyle style = _style[i];
            FontSet fonts = _engine.FontsFor(style);
            if (char.IsLowSurrogate(_text[i]) && i > 0 && char.IsHighSurrogate(_text[i - 1]))
            {
                _font[i] = _font[i - 1];
                continue;
            }

            int cp = char.IsSurrogatePair(_text, i) ? char.ConvertToUtf32(_text, i) : _text[i];
            if (IsAtomic(i) || cp == '\n' || PdfFont.IsInvisible(_text, i))
            {
                _font[i] = fonts.Primary;
                if (cp == '\t') _shaping[i] = ' ';
                continue;
            }

            // A combining mark stays with its base's font when that font has it.
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(_text, i);
            PdfFont font = i > 0 && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                && ReferenceEquals(_style[i - 1], style) && _font[i - 1].HasGlyph(cp)
                ? _font[i - 1]
                : fonts.For(cp);
            _font[i] = font;

            // No font has it: a question mark keeps the text visible, as browsers show a box.
            if (!font.HasGlyph(cp))
            {
                bool pair = char.IsSurrogatePair(_text, i);
                _shaping[i] = font.HasGlyph('?') ? '?' : (char)0x200B;
                if (pair) _shaping[i + 1] = (char)0x200B;
            }
        }
    }

    private void ResolveBidi()
    {
        int n = _text.Length;
        _level = new byte[n];
        int direction = _container.Keyword("unicode-bidi") == "plaintext" ? 2 : _rtl ? 1 : 0;
        int start = 0;
        for (int i = 0; i <= n; i++)
        {
            if (i < n && _text[i] != '\n') continue;
            int end = i < n ? i + 1 : n;
            if (end > start)
            {
                var cps = new List<int>();
                var cpStart = new List<int>();
                for (int k = start; k < end; k += char.IsSurrogatePair(_text, k) ? 2 : 1)
                {
                    cps.Add(char.IsSurrogatePair(_text, k) ? char.ConvertToUtf32(_text, k) : _text[k]);
                    cpStart.Add(k);
                }

                cpStart.Add(end);
                byte[] levels = Bidi.Analyze([.. cps], direction).Levels;
                for (int k = 0; k < cps.Count; k++)
                {
                    for (int j = cpStart[k]; j < cpStart[k + 1]; j++) _level[j] = levels[k];
                }
            }

            start = end;
        }
    }

    private void Itemize()
    {
        int n = _text.Length;
        int[] script = ResolveScripts();
        _itemAt = new int[n];
        int start = 0;
        for (int i = 1; i <= n; i++)
        {
            if (i < n && char.IsLowSurrogate(_text[i])) continue;
            bool split = i == n || !ReferenceEquals(_style[i], _style[start]) || !ReferenceEquals(_font[i], _font[start])
                || _level[i] != _level[start] || script[i] != script[start]
                || IsAtomic(i) || IsAtomic(i - 1) || _text[i] == '\n' || _text[i - 1] == '\n'
                || _startEdge[i] != 0 || _endEdge[i - 1] != 0 || _span[i] != _span[start];
            if (!split) continue;
            var item = new Item(start, i, _style[start], _font[start], _level[start]);
            if (!IsAtomic(start) && _text[start] != '\n') item.Glyphs = Shape(item, script[start]);
            for (int k = start; k < i; k++) _itemAt[k] = _items.Count;
            _items.Add(item);
            start = i;
        }
    }

    private int[] ResolveScripts()
    {
        var script = new int[_text.Length];
        int current = -1;
        for (int i = 0; i < _text.Length; i++)
        {
            int s = char.IsLowSurrogate(_text[i]) && i > 0 ? script[i - 1] : UnicodeData.Script(char.IsSurrogatePair(_text, i) ? char.ConvertToUtf32(_text, i) : _text[i]);
            if (s > 2) current = s;
            script[i] = s > 2 ? s : current;
        }

        int first = Array.Find(script, s => s > 2);
        for (int i = 0; i < script.Length && script[i] < 0; i++) script[i] = first > 2 ? first : UnicodeData.CommonScript;
        return script;
    }

    private List<ShapedGlyph> Shape(Item item, int script)
    {
        double size = item.Style.FontSize;
        List<ShapedGlyph> glyphs = item.Font.ShapeRun(new string(_shaping), item.Start, item.End, item.Level % 2 == 1, UnicodeData.ScriptTags[script]);
        double toUnits = size > 0 ? 1000 / size : 0;
        double letter = Spacing(item.Style, "letter-spacing") * toUnits;
        double word = Spacing(item.Style, "word-spacing") * toUnits;
        double tabSize = item.Style.Length("tab-size") is double tabLength && item.Style["tab-size"][0].Token.Type == CssTokenType.Dimension
            ? tabLength * toUnits
            : item.Style.Number("tab-size") * (item.Font.HasGlyph(' ') ? item.Font.MeasureString(" ", 1000) : 250);
        for (int g = 0; g < glyphs.Count; g++)
        {
            ShapedGlyph glyph = glyphs[g];
            char c = _text[glyph.Cluster];
            double advance = c == '\t' ? tabSize : glyph.Advance;
            if (c is ' ' or (char)0x00A0) advance += word;
            bool lastOfCluster = g + 1 == glyphs.Count || glyphs[g + 1].Cluster != glyph.Cluster;
            if (lastOfCluster && letter != 0) advance += letter;
            if (advance != glyph.Advance) glyphs[g] = glyph with { Advance = advance, Text = c == '\t' ? "\t" : glyph.Text };
        }

        return glyphs;
    }

    private static double Spacing(ComputedStyle style, string property) =>
        style[property].Count == 1 && style[property][0].IsIdent("normal") ? 0 : style.Length(property, style.FontSize) ?? 0;

    private void Measure()
    {
        int n = _text.Length;
        _advance = new double[n];
        foreach (Item item in _items)
        {
            if (IsAtomic(item.Start)) _advance[item.Start] = _atomics[item.Start].Width;
            foreach (ShapedGlyph g in item.Glyphs) _advance[g.Cluster] += g.Advance * item.Style.FontSize / 1000;
        }

        _prefix = new double[n + 1];
        for (int i = 0; i < n; i++) _prefix[i + 1] = _prefix[i] + _advance[i] + _startEdge[i] + _endEdge[i];
    }

    private static bool NoWrap(ComputedStyle s) => s.Keyword("white-space") is "nowrap" or "pre";

    private static bool Collapsible(ComputedStyle s) => s.Keyword("white-space") is "normal" or "nowrap" or "pre-line";

    private void FindBreaks()
    {
        int n = _text.Length;
        var positions = new SortedDictionary<int, bool>();
        foreach (LineBreak b in LineBreaker.Breaks(_text))
        {
            int p = b.Position;
            if (b.Mandatory || p >= n)
            {
                positions[p] = true;
                continue;
            }

            if (p <= 0 || NoWrap(_style[p - 1])) continue;
            if (_text[p - 1] == SoftHyphen && _style[p - 1].Keyword("hyphens") == "none") continue;
            positions[p] = false;
        }

        // word-break: break-all allows a break between any two letters.
        for (int p = 1; p < n; p++)
        {
            if (_style[p - 1].Keyword("word-break") != "break-all" || NoWrap(_style[p - 1]) || positions.ContainsKey(p)) continue;
            if (char.IsLowSurrogate(_text[p]) || IsMark(p)) continue;
            if (char.IsLetterOrDigit(_text[p - 1]) || char.IsLetterOrDigit(_text[p])) positions[p] = false;
        }

        _breaks = [.. positions.Select(p => new LineBreak(p.Key, p.Value))];
        if (_breaks.Count == 0 || _breaks[^1].Position != n) _breaks.Add(new LineBreak(n, true));
    }

    private bool IsMark(int i) =>
        CharUnicodeInfo.GetUnicodeCategory(_text, i) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private double Width(int start, int end) => end > start ? _prefix[end] - _prefix[start] : 0;

    /// <summary>The end of the visible part of a line: trailing collapsible spaces hang, forced breaks take no room.</summary>
    private int TrimEnd(int start, int end)
    {
        while (end > start)
        {
            char c = _text[end - 1];
            if (c == '\n' || (c is ' ' or '\t' && _style[end - 1].Keyword("white-space") is not ("pre" or "break-spaces"))) end--;
            else break;
        }

        return end;
    }

    private double HyphenWidth(int index)
    {
        PdfFont font = _font[index];
        return font.HasGlyph('-') ? font.MeasureString("-", _style[index].FontSize) : 0;
    }

    private double LineWidth(int start, int end)
    {
        int visible = TrimEnd(start, end);
        return Width(start, visible) + (visible > start && _text[visible - 1] == SoftHyphen ? HyphenWidth(visible - 1) : 0);
    }

    /// <summary>The min-content and max-content widths (CSS Sizing 3, 5.1).</summary>
    public (double Min, double Max) IntrinsicWidths()
    {
        if (_text.Length == 0) return (0, 0);
        double indent = _container.Length("text-indent", _width) ?? 0;
        double min = 0, max = 0;
        int previous = 0, lineStart = 0;
        bool anywhere = _style.Any(s => s.Keyword("overflow-wrap") == "anywhere");
        foreach (LineBreak b in _breaks)
        {
            min = Math.Max(min, LineWidth(previous, b.Position) - AtomicSlack(previous, b.Position));
            previous = b.Position;
            if (b.Mandatory)
            {
                max = Math.Max(max, LineWidth(lineStart, b.Position) + (lineStart == 0 ? indent : 0));
                lineStart = b.Position;
            }
        }

        if (anywhere) min = _advance.DefaultIfEmpty(0).Max();
        return (min + Math.Max(0, indent), Math.Max(max, min));
    }

    // In min-content measurement an atomic inline counts with its own min-content width.
    private double AtomicSlack(int start, int end)
    {
        double slack = 0;
        foreach (KeyValuePair<int, Atomic> a in _atomics)
        {
            if (a.Key >= start && a.Key < end) slack += a.Value.Width - a.Value.Min;
        }

        return slack;
    }

    /// <summary>Lays out the lines from (<paramref name="x"/>, <paramref name="y"/>); <paramref name="height"/> is their total height.</summary>
    /// <param name="x">The left content edge.</param>
    /// <param name="y">The top of the first line.</param>
    /// <param name="height">The height of all lines.</param>
    /// <param name="exclusions">Floats beside the lines: for a band given by its top and height, the room they take on each side and where that changes; null when there are none.</param>
    public List<LineFragment> Layout(double x, double y, out double height, Func<double, double, Exclusion>? exclusions = null)
    {
        var lines = new List<LineFragment>();
        int n = _text.Length;
        double top = y;
        int lineStart = 0, next = 0;
        double indent = _container.Length("text-indent", _width) ?? 0;
        double strut = Metrics(_container).LineHeight;
        while (lineStart < n)
        {
            bool first = lines.Count == 0;
            Exclusion band = exclusions?.Invoke(top, strut) ?? default;
            double available = _width - band.Left - band.Right - (first ? indent : 0);
            int end = -1;
            bool mandatory = false;
            while (next < _breaks.Count && _breaks[next].Position <= lineStart) next++;
            for (int b = next; b < _breaks.Count; b++)
            {
                if (LineWidth(lineStart, _breaks[b].Position) > available + Epsilon) break;
                end = _breaks[b].Position;
                mandatory = _breaks[b].Mandatory;
                if (mandatory) break;
            }

            // Nothing fits beside a float: the line moves down to where the float ends (CSS 2.1, 9.5).
            if (end < 0 && band.Left + band.Right > 0 && band.Next > top && !double.IsInfinity(band.Next))
            {
                top = band.Next;
                continue;
            }

            if (end < 0)
            {
                // Nothing fits. overflow-wrap may break the word; otherwise the line overflows.
                int firstBreak = next < _breaks.Count ? _breaks[next].Position : n;
                bool wrapAnywhere = _style[lineStart].Keyword("overflow-wrap") is "anywhere" or "break-word" || _style[lineStart].Keyword("word-break") == "break-word";
                end = wrapAnywhere ? EmergencyBreak(lineStart, firstBreak, available) : firstBreak;
                mandatory = end == firstBreak && next < _breaks.Count && _breaks[next].Mandatory;
            }

            bool last = end >= n;
            LineFragment line = BuildLine(lineStart, end, x + band.Left, top, available, first ? indent : 0, last || mandatory);
            lines.Add(line);
            top += line.Height;
            lineStart = end;
        }

        height = top - y;
        return lines;
    }

    private int EmergencyBreak(int start, int limit, double available)
    {
        int end = start + (char.IsSurrogatePair(_text, start) ? 2 : 1);
        while (end < limit && (char.IsLowSurrogate(_text[end]) || IsMark(end))) end++;
        int best = end;
        while (end < limit)
        {
            int next = end + (char.IsSurrogatePair(_text, end) ? 2 : 1);
            while (next < limit && (char.IsLowSurrogate(_text[next]) || IsMark(next))) next++;
            if (Width(start, next) > available + Epsilon) break;
            best = end = next;
        }

        return best;
    }

    private (double Ascent, double Descent, double LineHeight, double XHeight) Metrics(ComputedStyle s)
    {
        PdfFont f = _engine.FontsFor(s).Primary;
        double size = s.FontSize;
        double a = f.Ascent * size, d = f.Descent * size;
        double lineHeight = LineHeight(s, a + d + (f.LineGap * size));
        return (a, d, lineHeight, f.XHeight > 0 ? f.XHeight * size : size / 2);
    }

    /// <summary>The used line-height: normal is the font's own spacing.</summary>
    internal static double LineHeight(ComputedStyle s, double normal)
    {
        List<CssValue> v = s["line-height"];
        if (v.Count == 1 && v[0].IsToken && v[0].Token.Type == CssTokenType.Number) return v[0].Token.Number * s.FontSize;
        if (v.Count == 1 && v[0].IsIdent("normal")) return normal;
        return s.Length("line-height", s.FontSize) ?? normal;
    }

    /// <summary>The baseline shift of a box from vertical-align, down positive, relative to its parent's baseline.</summary>
    private static double Align(ComputedStyle s, (double A, double D, double L, double X) parent, double parentSize, double above, double below, double lineHeight)
    {
        List<CssValue> v = s["vertical-align"];
        if (v.Count == 1 && v[0].IsToken && v[0].Token.Type == CssTokenType.Ident)
        {
            return v[0].Token.Value.ToLowerInvariant() switch
            {
                "sub" => parentSize * 0.2,
                "super" => -parentSize * 0.34,
                "text-top" => above - parent.A,
                "text-bottom" => parent.D - below,
                "middle" => (-parent.X / 2) + ((above - below) / 2),
                _ => 0,
            };
        }

        return -(s.Length("vertical-align", lineHeight) ?? 0);
    }

    private LineFragment BuildLine(int a, int b, double x, double top, double available, double indent, bool lastOrForced)
    {
        int visibleEnd = TrimEnd(a, b);
        int start = a;
        while (start < visibleEnd && _text[start] == ' ' && Collapsible(_style[start])) start++;

        // Segments: the items cut to the line, then put in visual order (UAX #9, L2).
        var segments = new List<Segment>();
        for (int i = start; i < visibleEnd;)
        {
            Item item = _items[_itemAt[i]];
            int s = Math.Max(item.Start, start), e = Math.Min(item.End, visibleEnd);
            var seg = new Segment(item, s, e) { Glyphs = item.Glyphs.Where(g => g.Cluster >= s && g.Cluster < e).ToList() };
            seg.Width = Width(s, e);
            segments.Add(seg);
            i = e;
        }

        // A soft hyphen at the break shows a hyphen.
        if (segments.Count > 0 && _text[visibleEnd - 1] == SoftHyphen)
        {
            Segment last = segments[^1];
            List<ShapedGlyph> hyphen = last.Item.Font.ShapeRun("-", 0, 1, false, "latn");
            if (hyphen.Count > 0)
            {
                ShapedGlyph h = hyphen[0] with { Cluster = visibleEnd - 1, Text = "-" };
                last.Glyphs.Add(h);
                last.Width += h.Advance * last.Item.Style.FontSize / 1000;
            }
        }

        // Shaping returns logical order; right to left runs are drawn reversed, as the core layout does.
        foreach (Segment seg in segments)
        {
            if (seg.Item.Level % 2 == 1) seg.Glyphs.Reverse();
        }

        // The first and last segment of each inline box on this line carry its edges, even when
        // the box's trailing space was trimmed at the break.
        var startSegment = new Dictionary<int, Segment>();
        var endSegment = new Dictionary<int, Segment>();
        foreach (Segment seg in segments)
        {
            foreach (int s in Chain(_span[seg.Start]))
            {
                if (_spans[s].Start >= a && !startSegment.ContainsKey(s)) startSegment[s] = seg;
                if (_spans[s].End <= b) endSegment[s] = seg;
            }
        }

        Reorder(segments);

        // Horizontal alignment and justification.
        double lineWidth = segments.Sum(s => s.Width);
        string align = TextAlign(lastOrForced);
        double extra = available - lineWidth;
        double perSpace = 0;
        if (align == "justify" && extra > 0)
        {
            int spaces = 0;
            for (int i = start; i < visibleEnd; i++) spaces += _text[i] is ' ' or (char)0x00A0 ? 1 : 0;
            if (spaces > 0) perSpace = extra / spaces;
        }

        double offset = align switch
        {
            "right" => Math.Max(0, extra),
            "center" => Math.Max(0, extra / 2),
            _ => 0,
        };

        // Vertical metrics: the strut, then each inline box and atomic inline on the line.
        var rootMetrics = Metrics(_container);
        double rootHalf = (rootMetrics.LineHeight - (rootMetrics.Ascent + rootMetrics.Descent)) / 2;
        double minTop = -(rootMetrics.Ascent + rootHalf), maxBottom = rootMetrics.Descent + rootHalf;
        var spanShift = new Dictionary<int, double>();
        var spanMetrics = new Dictionary<int, (double A, double D, double L, double X)>();

        (double A, double D, double L, double X) MetricsOf(int span)
        {
            if (span < 0) return rootMetrics;
            if (!spanMetrics.TryGetValue(span, out var m)) spanMetrics[span] = m = Metrics(_spans[span].Box.Style);
            return m;
        }

        double ShiftOf(int span)
        {
            if (span < 0) return 0;
            if (spanShift.TryGetValue(span, out double shift)) return shift;
            Span s = _spans[span];
            var m = MetricsOf(span);
            double half = (m.L - (m.A + m.D)) / 2;
            ComputedStyle parentStyle = s.Parent < 0 ? _container : _spans[s.Parent].Box.Style;
            shift = ShiftOf(s.Parent) + Align(s.Box.Style, MetricsOf(s.Parent), parentStyle.FontSize, m.A, m.D, m.L);
            spanShift[span] = shift;
            minTop = Math.Min(minTop, shift - m.A - half);
            maxBottom = Math.Max(maxBottom, shift + m.D + half);
            return shift;
        }

        var atomicShift = new Dictionary<int, double>();
        var deferred = new List<(int Position, Atomic Atomic, string Align)>();
        foreach (Segment seg in segments)
        {
            for (int sp = _span[seg.Start]; sp >= 0; sp = _spans[sp].Parent) ShiftOf(sp);
            if (!IsAtomic(seg.Start)) continue;
            Atomic atomic = _atomics[seg.Start];
            string valign = atomic.Box.Style.Keyword("vertical-align");
            int parent = _span[seg.Start];
            ComputedStyle parentStyle = parent < 0 ? _container : _spans[parent].Box.Style;
            if (valign is "top" or "bottom")
            {
                deferred.Add((seg.Start, atomic, valign));
                continue;
            }

            double shift = ShiftOf(parent) + Align(atomic.Box.Style, MetricsOf(parent), parentStyle.FontSize, atomic.Above, atomic.Below, atomic.Above + atomic.Below);
            atomicShift[seg.Start] = shift;
            minTop = Math.Min(minTop, shift - atomic.Above);
            maxBottom = Math.Max(maxBottom, shift + atomic.Below);
        }

        // top and bottom align to the line box once the rest is placed, growing it if needed.
        foreach ((int position, Atomic atomic, string valign) in deferred)
        {
            double h = atomic.Above + atomic.Below;
            if (h > maxBottom - minTop)
            {
                if (valign == "top") maxBottom = minTop + h;
                else minTop = maxBottom - h;
            }

            atomicShift[position] = valign == "top" ? minTop + atomic.Above : maxBottom - atomic.Below;
        }

        double baseline = top - minTop;
        var line = new LineFragment { X = x, Y = top, Width = available + indent, Height = maxBottom - minTop, Baseline = baseline };

        // Place segments left to right with their inline box edges.
        double cursor = x + (_rtl ? 0 : indent) + offset;
        var spanLeft = new Dictionary<int, double>();
        var spanRight = new Dictionary<int, double>();
        var content = new List<Fragment>();
        foreach (Segment seg in segments)
        {
            bool rtlSeg = seg.Item.Level % 2 == 1;
            List<int> chain = Chain(_span[seg.Start]);
            List<int> starting = chain.Where(s => startSegment.TryGetValue(s, out Segment? first) && first == seg).ToList();
            List<int> ending = chain.Where(s => endSegment.TryGetValue(s, out Segment? last) && last == seg).ToList();
            double outerLeft = cursor;

            // Edges on the left, outer box first: start edges in a left to right run, end edges in a right to left run.
            foreach (int s in rtlSeg ? ending : starting)
            {
                Span sp = _spans[s];
                cursor += rtlSeg ? sp.EndMargin : sp.StartMargin;
                spanLeft[s] = Math.Min(spanLeft.TryGetValue(s, out double l) ? l : double.MaxValue, cursor);
                cursor += rtlSeg ? sp.EndBorder + sp.EndPadding : sp.StartBorder + sp.StartPadding;
            }

            if (IsAtomic(seg.Start))
            {
                Atomic atomic = _atomics[seg.Start];
                BoxFragment f = atomic.Fragment!;
                double marginTop = baseline + atomicShift[seg.Start] - atomic.Above;
                f.Translate(cursor + f.Margin.Left - f.X, marginTop + f.Margin.Top - f.Y);
                content.Add(f);
                cursor += atomic.Width;
            }
            else if (seg.Glyphs.Count > 0)
            {
                double size = seg.Item.Style.FontSize;
                double width = seg.Glyphs.Sum(g => g.Advance) * size / 1000;
                int spaces = perSpace > 0 ? seg.Glyphs.Count(g => _text[g.Cluster] is ' ' or (char)0x00A0) : 0;
                double shift = ShiftOf(_span[seg.Start]);
                var m = Metrics(seg.Item.Style);
                var text = new TextFragment(seg.Item.Style, seg.Item.Font, size, seg.Glyphs, _text.Substring(seg.Start, seg.End - seg.Start))
                {
                    X = cursor,
                    Y = baseline + shift - m.Ascent,
                    Width = width + (spaces * perSpace),
                    Height = m.Ascent + m.Descent,
                    Baseline = baseline + shift,
                    JustifySpace = perSpace,
                    Decorations = Decorations(_span[seg.Start]),
                };
                content.Add(text);
                cursor += text.Width;
            }

            // Edges on the right, inner box first.
            foreach (int s in Enumerable.Reverse(rtlSeg ? starting : ending))
            {
                Span sp = _spans[s];
                cursor += rtlSeg ? sp.StartBorder + sp.StartPadding : sp.EndBorder + sp.EndPadding;
                spanRight[s] = Math.Max(spanRight.TryGetValue(s, out double r) ? r : double.MinValue, cursor);
                cursor += rtlSeg ? sp.StartMargin : sp.EndMargin;
            }

            // Boxes that continue past this segment cover it from edge to edge.
            foreach (int s in chain)
            {
                bool leftEdge = rtlSeg ? ending.Contains(s) : starting.Contains(s);
                bool rightEdge = rtlSeg ? starting.Contains(s) : ending.Contains(s);
                double left = leftEdge ? spanLeft[s] : outerLeft;
                double right = rightEdge ? spanRight[s] : cursor;
                spanLeft[s] = Math.Min(spanLeft.TryGetValue(s, out double l) ? l : double.MaxValue, left);
                spanRight[s] = Math.Max(spanRight.TryGetValue(s, out double r) ? r : double.MinValue, right);
            }
        }

        // Inline box backgrounds and borders go under the text, outer boxes first.
        foreach (int s in spanLeft.Keys.OrderBy(s => _spans[s].Depth))
        {
            Span sp = _spans[s];
            var m = MetricsOf(s);
            double baselineY = baseline + ShiftOf(s);
            line.Items.Add(new InlineFragment(sp.Box)
            {
                X = spanLeft[s],
                Y = baselineY - m.A - sp.Padding.Top - sp.Border.Top,
                Width = spanRight[s] - spanLeft[s],
                Height = m.A + m.D + sp.Padding.Vertical + sp.Border.Vertical,
                Border = sp.Border,
                Padding = sp.Padding,
                StartEdge = startSegment.ContainsKey(s) && sp.Box.HasStartEdge,
                EndEdge = endSegment.ContainsKey(s) && sp.Box.HasEndEdge,
            });
        }

        line.Items.AddRange(content);
        return line;
    }

    private List<int> Chain(int span)
    {
        var chain = new List<int>();
        for (int s = span; s >= 0; s = _spans[s].Parent) chain.Add(s);
        chain.Reverse();
        return chain;
    }

    /// <summary>The boxes whose text-decoration draws through this text: inline ancestors and the block containers above them.</summary>
    private List<ComputedStyle> Decorations(int span)
    {
        var styles = new List<ComputedStyle>();
        for (int s = span; s >= 0; s = _spans[s].Parent)
        {
            if (_spans[s].Box.Style.Keyword("text-decoration-line") is not ("none" or "")) styles.Add(_spans[s].Box.Style);
        }

        for (Box? b = _containerBox; b is not null; b = b.Parent)
        {
            if (b.Style.Keyword("text-decoration-line") is not ("none" or "")) styles.Add(b.Style);

            // Decorations do not propagate into atomic inlines, floats or positioned boxes.
            if (b is InlineBlockBox || b.Style.Keyword("float") != "none" || b.Style.Keyword("position") is "absolute" or "fixed") break;
        }

        return styles;
    }

    private string TextAlign(bool lastOrForced)
    {
        string align = _container.Keyword("text-align");
        if (lastOrForced && align is "justify")
        {
            string last = _container.Keyword("text-align-last");
            align = last is "auto" or "" ? "start" : last;
        }
        else if (align == "justify-all")
        {
            align = "justify";
        }

        return align switch
        {
            "start" or "match-parent" or "-webkit-left" => _rtl ? "right" : "left",
            "end" => _rtl ? "left" : "right",
            "-webkit-center" => "center",
            "-webkit-right" => "right",
            _ => align,
        };
    }

    /// <summary>UAX #9, L2: from the highest level down to the lowest odd one, reverse each run at or above it.</summary>
    private static void Reorder(List<Segment> segments)
    {
        if (segments.Count < 2) return;
        byte max = segments.Max(s => s.Item.Level);
        byte minOdd = segments.Where(s => s.Item.Level % 2 == 1).Select(s => s.Item.Level).DefaultIfEmpty((byte)255).Min();
        if (minOdd == 255) return;
        for (int level = max; level >= minOdd; level--)
        {
            for (int i = 0; i < segments.Count;)
            {
                if (segments[i].Item.Level < level)
                {
                    i++;
                    continue;
                }

                int j = i;
                while (j < segments.Count && segments[j].Item.Level >= level) j++;
                segments.Reverse(i, j - i);
                i = j;
            }
        }
    }
}
