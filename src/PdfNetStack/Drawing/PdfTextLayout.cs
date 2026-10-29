using System.Globalization;
using PdfNetStack.Fonts;
using PdfNetStack.Text;

namespace PdfNetStack.Drawing;

/// <summary>One line of a <see cref="PdfTextLayout"/>. Distances are points from the layout's top left corner.</summary>
public sealed class PdfTextLine
{
    internal PdfTextLine(int start, int end, double top, double ascent, double descent, double left, double width, List<LineRun> runs)
    {
        Start = start;
        End = end;
        Top = top;
        Baseline = top + ascent;
        Height = ascent + descent;
        Left = left;
        Width = width;
        Runs = runs;
    }

    /// <summary>Index in <see cref="PdfParagraph.Text"/> of the line's first character.</summary>
    public int Start { get; }

    /// <summary>Index just past the line, including the spaces and line feed that end it.</summary>
    public int End { get; }

    /// <summary>Top of the line box.</summary>
    public double Top { get; }

    /// <summary>The baseline.</summary>
    public double Baseline { get; }

    /// <summary>Height of the line box.</summary>
    public double Height { get; }

    /// <summary>Left edge of the text after alignment.</summary>
    public double Left { get; }

    /// <summary>Width of the text, without trailing spaces.</summary>
    public double Width { get; }

    internal List<LineRun> Runs { get; }
}

/// <summary>A piece of a line in one style and direction, in visual order, ready to draw.</summary>
internal sealed record LineRun(PdfTextStyle Style, PdfFont Font, List<ShapedGlyph> Glyphs, double X, double Width, string Text);

/// <summary>
/// A paragraph broken into lines for a given width: the Unicode line breaking algorithm finds
/// break opportunities, the bidirectional algorithm orders each line, and an OpenType shaper
/// forms the glyphs. Measure with it, then draw it with <see cref="PdfGraphics.DrawText(PdfTextLayout, double, double)"/>.
/// </summary>
/// <remarks>
/// Lines are filled greedily. A word wider than the line is broken between characters. Trailing
/// spaces do not count toward a line's width. Line height follows the CSS model: each span's
/// font contributes its ascent and descent, spread by the paragraph's line height.
/// </remarks>
public sealed class PdfTextLayout
{
    private const double Epsilon = 1e-6;
    private const int TabSpaces = 4;

    private PdfTextLayout(PdfParagraph paragraph, double maxWidth, List<PdfTextLine> lines)
    {
        Paragraph = paragraph;
        MaxWidth = maxWidth;
        Lines = lines;
        Height = lines.Count == 0 ? 0 : lines[^1].Top + lines[^1].Height;
        Width = lines.Count == 0 ? 0 : lines.Max(l => l.Width);
    }

    /// <summary>The laid out paragraph.</summary>
    public PdfParagraph Paragraph { get; }

    /// <summary>The width lines were broken for.</summary>
    public double MaxWidth { get; }

    /// <summary>Width of the widest line.</summary>
    public double Width { get; }

    /// <summary>Total height of all lines.</summary>
    public double Height { get; }

    /// <summary>The lines, top to bottom.</summary>
    public IReadOnlyList<PdfTextLine> Lines { get; }

    /// <summary>Breaks <paramref name="paragraph"/> into lines no wider than <paramref name="width"/> points.</summary>
    /// <exception cref="ArgumentException">A character has no glyph in its style's font or fallbacks.</exception>
    public static PdfTextLayout Create(PdfParagraph paragraph, double width)
    {
        Compat.ThrowIfNull(paragraph, nameof(paragraph));
        if (!(width > 0) || double.IsInfinity(width)) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        string text = paragraph.Text;
        if (text.Length == 0) return new PdfTextLayout(paragraph, width, []);
        return new Builder(paragraph, text, width).Build();
    }

    /// <summary>The layout state, kept per call so layouts can be built in parallel.</summary>
    private sealed class Builder
    {
        private readonly PdfParagraph _p;
        private readonly string _text;
        private readonly double _width;
        private readonly PdfTextStyle[] _style;
        private readonly PdfFont[] _font;
        private readonly byte[] _level;
        private readonly double[] _prefix;
        private readonly List<Item> _items = [];
        private readonly List<(int Start, int End, Bidi Bidi, int[] CpStart)> _paragraphs = [];

        public Builder(PdfParagraph p, string text, double width)
        {
            _p = p;
            _text = text;
            _width = width;
            int n = text.Length;
            _style = new PdfTextStyle[n];
            _font = new PdfFont[n];
            _level = new byte[n];
            _prefix = new double[n + 1];
        }

        public PdfTextLayout Build()
        {
            AssignStylesAndFonts();
            ResolveBidi();
            Itemize();
            Measure();
            return new PdfTextLayout(_p, _width, BreakLines());
        }

        private void AssignStylesAndFonts()
        {
            for (int i = 0; i < _text.Length; i++)
            {
                PdfTextStyle style = _p.StyleAt(i);
                _style[i] = style;
                if (char.IsLowSurrogate(_text[i]) && i > 0 && char.IsHighSurrogate(_text[i - 1]))
                {
                    _font[i] = _font[i - 1];
                    continue;
                }

                int cp = char.IsSurrogatePair(_text, i) ? char.ConvertToUtf32(_text, i) : _text[i];
                if (PdfFont.IsInvisible(_text, i) || cp == '\t' || style.Font.HasGlyph(cp))
                {
                    _font[i] = style.Font;
                    continue;
                }

                // A combining mark stays with its base's font when that font has it.
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(_text, i);
                if (i > 0 && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                    && ReferenceEquals(_style[i - 1], style) && _font[i - 1].HasGlyph(cp))
                {
                    _font[i] = _font[i - 1];
                    continue;
                }

                PdfFont? fallback = style.Fallbacks.FirstOrDefault(f => f.HasGlyph(cp));
                _font[i] = fallback ?? throw style.Font.MissingGlyph(cp);
            }
        }

        /// <summary>UAX #9 per paragraph: the text splits at paragraph separators (line feeds and U+2029).</summary>
        private void ResolveBidi()
        {
            int direction = _p.Direction switch
            {
                PdfTextDirection.LeftToRight => 0,
                PdfTextDirection.RightToLeft => 1,
                _ => 2,
            };
            int start = 0;
            for (int i = 0; i <= _text.Length; i++)
            {
                bool end = i == _text.Length || IsParagraphSeparator(_text[i]);
                if (!end) continue;
                int stop = i < _text.Length ? (_text[i] == '\r' && i + 1 < _text.Length && _text[i + 1] == '\n' ? i + 2 : i + 1) : i;
                AddParagraph(start, stop, direction);
                if (i == _text.Length) break;
                start = stop;
                i = stop - 1;
            }
        }

        private void AddParagraph(int start, int end, int direction)
        {
            if (end <= start) return;
            var cps = new List<int>();
            var cpStart = new List<int>();
            for (int i = start; i < end; i += char.IsSurrogatePair(_text, i) ? 2 : 1)
            {
                cps.Add(char.IsSurrogatePair(_text, i) ? char.ConvertToUtf32(_text, i) : _text[i]);
                cpStart.Add(i);
            }

            cpStart.Add(end);
            var bidi = Bidi.Analyze([.. cps], direction);
            for (int k = 0; k < cps.Count; k++)
            {
                for (int i = cpStart[k]; i < cpStart[k + 1]; i++) _level[i] = bidi.Levels[k];
            }

            _paragraphs.Add((start, end, bidi, [.. cpStart]));
        }

        /// <summary>Items share style, font, level and script, so each is shaped in one call.</summary>
        private void Itemize()
        {
            int[] script = ResolveScripts();
            int start = 0;
            for (int i = 1; i <= _text.Length; i++)
            {
                if (i < _text.Length && char.IsLowSurrogate(_text[i])) continue;
                bool split = i == _text.Length || !ReferenceEquals(_style[i], _style[start]) || !ReferenceEquals(_font[i], _font[start])
                    || _level[i] != _level[start] || script[i] != script[start] || IsParagraphSeparator(_text[i - 1]);
                if (!split) continue;
                _items.Add(Shape(start, i, script[start]));
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

        private Item Shape(int start, int end, int script)
        {
            PdfTextStyle style = _style[start];
            PdfFont font = _font[start];
            bool rtl = _level[start] % 2 == 1;

            // Tabs shape as spaces and take the room of four.
            string shapingText = _text.IndexOf('\t', start, end - start) >= 0 ? _text.Replace('\t', ' ') : _text;
            List<ShapedGlyph> glyphs = font.ShapeRun(shapingText, start, end, rtl, UnicodeData.ScriptTags[script]);
            double toUnits = 1000 / style.Size;
            for (int g = 0; g < glyphs.Count; g++)
            {
                ShapedGlyph glyph = glyphs[g];
                char c = _text[glyph.Cluster];
                double advance = glyph.Advance;
                if (c == '\t') advance *= TabSpaces;
                if (c is ' ' or '\u00A0' or '\t') advance += style.WordSpacing * toUnits;

                // Letter spacing goes after each cluster, on its last glyph in logical order.
                bool lastOfCluster = g + 1 == glyphs.Count || glyphs[g + 1].Cluster != glyph.Cluster;
                if (lastOfCluster && style.LetterSpacing != 0) advance += style.LetterSpacing * toUnits;
                if (advance != glyph.Advance) glyphs[g] = glyph with { Advance = advance, Text = c == '\t' ? "\t" : glyph.Text };
            }

            return new Item(start, end, style, font, rtl, glyphs);
        }

        /// <summary>Prefix sums of cluster widths in points, so any range measures in constant time.</summary>
        private void Measure()
        {
            var width = new double[_text.Length];
            foreach (Item item in _items)
            {
                foreach (ShapedGlyph g in item.Glyphs) width[g.Cluster] += g.Advance * item.Style.Size / 1000;
            }

            for (int i = 0; i < _text.Length; i++) _prefix[i + 1] = _prefix[i] + width[i];
        }

        private double Width(int start, int end) => end > start ? _prefix[end] - _prefix[start] : 0;

        private int TrimEnd(int start, int end)
        {
            while (end > start && _text[end - 1] is ' ' or '\t' or '\n' or '\r' or '\u2029' or '\u0085') end--;
            return end;
        }

        private double HyphenWidth(int index)
        {
            PdfFont font = _font[index];
            int hyphen = font.HasGlyph(0x2010) ? 0x2010 : '-';
            return font.HasGlyph(hyphen) ? font.MeasureString(char.ConvertFromUtf32(hyphen), _style[index].Size) : 0;
        }

        private bool EndsWithSoftHyphen(int start, int end) => end > start && _text[end - 1] == '\u00AD';

        private double LineWidth(int start, int end)
        {
            int visible = TrimEnd(start, end);
            return Width(start, visible) + (EndsWithSoftHyphen(start, visible) ? HyphenWidth(visible - 1) : 0);
        }

        private List<PdfTextLine> BreakLines()
        {
            List<LineBreak> breaks = LineBreaker.Breaks(_text);
            var lines = new List<PdfTextLine>();
            int lineStart = 0, next = 0;
            double top = 0;
            while (lineStart < _text.Length)
            {
                double available = _width - (lines.Count == 0 ? _p.FirstLineIndent : 0);
                int end = -1;
                bool mandatory = false;
                while (next < breaks.Count && breaks[next].Position <= lineStart) next++;
                for (int b = next; b < breaks.Count; b++)
                {
                    if (LineWidth(lineStart, breaks[b].Position) > available + Epsilon) break;
                    end = breaks[b].Position;
                    mandatory = breaks[b].Mandatory;
                    if (mandatory) break;
                }

                if (end < 0)
                {
                    // Nothing fits: break the first word between clusters, at least one per line.
                    int first = next < breaks.Count ? breaks[next].Position : _text.Length;
                    end = EmergencyBreak(lineStart, first, available);
                    mandatory = end == first && next < breaks.Count && breaks[next].Mandatory;
                }

                bool last = end >= _text.Length;
                PdfTextLine line = BuildLine(lineStart, end, top, available, lines.Count == 0, justify: !last && !mandatory);
                lines.Add(line);
                top += line.Height;
                lineStart = end;
            }

            // A paragraph that ends with a line feed has an empty last line.
            if (_text.Length > 0 && IsParagraphSeparator(_text[^1]))
            {
                lines.Add(BuildLine(_text.Length, _text.Length, top, _width, false, justify: false));
            }

            return lines;
        }

        private int EmergencyBreak(int start, int limit, double available)
        {
            var clusterStarts = new SortedSet<int>();
            foreach (Item item in _items)
            {
                if (item.End <= start || item.Start >= limit) continue;
                foreach (ShapedGlyph g in item.Glyphs)
                {
                    if (g.Cluster > start && g.Cluster < limit) clusterStarts.Add(g.Cluster);
                }
            }

            // The longest run of whole clusters that fits, and never less than one cluster.
            int end = clusterStarts.Count > 0 ? clusterStarts.Min : limit;
            foreach (int c in clusterStarts)
            {
                if (Width(start, c) > available + Epsilon) break;
                end = c;
            }

            return end;
        }

        private PdfTextLine BuildLine(int start, int end, double top, double available, bool first, bool justify)
        {
            int visible = TrimEnd(start, end);
            var pieces = new List<(Item Item, int Start, int End, byte Level)>();
            (int pStart, int pEnd, Bidi bidi, int[] cpStart) = _paragraphs.FirstOrDefault(p => start >= p.Start && start < p.End);
            byte paragraphLevel = bidi?.ParagraphLevel ?? 0;
            byte[] levels = LineLevels(start, visible, pStart, bidi, cpStart);

            foreach (Item item in _items)
            {
                int s = Math.Max(item.Start, start), e = Math.Min(item.End, visible);
                for (int i = s; i < e;)
                {
                    int j = i + 1;
                    while (j < e && levels[j - start] == levels[i - start]) j++;
                    pieces.Add((item, i, j, levels[i - start]));
                    i = j;
                }
            }

            int[] order = Bidi.Reorder([.. pieces.Select(p => p.Level)]);
            var runs = new List<LineRun>();
            double x = 0;
            foreach (int k in order)
            {
                (Item item, int s, int e, byte level) = pieces[k];
                var glyphs = item.Glyphs.Where(g => g.Cluster >= s && g.Cluster < e).ToList();
                if (e == visible && EndsWithSoftHyphen(start, visible)) glyphs.AddRange(Hyphen(item));
                if (level % 2 == 1) glyphs.Reverse();
                double w = glyphs.Sum(g => g.Advance) * item.Style.Size / 1000;
                runs.Add(new LineRun(item.Style, item.Font, glyphs, x, w, _text.Substring(s, e - s)));
                x += w;
            }

            double lineWidth = x;
            if (justify && _p.Alignment == PdfTextAlignment.Justify) lineWidth = Justify(runs, available, lineWidth);

            bool rtl = paragraphLevel % 2 == 1;
            double indent = first ? _p.FirstLineIndent : 0;
            double slack = available - lineWidth;
            double left = (_p.Alignment, rtl) switch
            {
                (PdfTextAlignment.Left, _) => 0,
                (PdfTextAlignment.Right, _) => slack,
                (PdfTextAlignment.Center, _) => slack / 2,
                (PdfTextAlignment.End, false) => slack,
                (PdfTextAlignment.End, true) => 0,
                (_, true) => slack,
                _ => 0,
            };
            if (!rtl) left += indent;

            (double ascent, double descent) = Metrics(runs, start);
            return new PdfTextLine(start, end, top, ascent, descent, left, lineWidth, runs);
        }

        private byte[] LineLevels(int start, int end, int paragraphStart, Bidi? bidi, int[] cpStart)
        {
            var levels = new byte[Math.Max(0, end - start)];
            if (bidi is null || end <= start) return levels;
            int first = Array.BinarySearch(cpStart, start);
            int last = Array.BinarySearch(cpStart, end);
            if (first < 0 || last < 0) return levels;
            byte[] line = bidi.LineLevels(first, last);
            for (int k = 0; k < line.Length; k++)
            {
                for (int i = cpStart[first + k]; i < cpStart[first + k + 1]; i++) levels[i - start] = line[k];
            }

            return levels;
        }

        private List<ShapedGlyph> Hyphen(Item item)
        {
            PdfFont font = item.Font;
            int hyphen = font.HasGlyph(0x2010) ? 0x2010 : '-';
            if (!font.HasGlyph(hyphen)) return [];
            return [.. font.ShapeRun(char.ConvertFromUtf32(hyphen), 0, hyphen > 0xFFFF ? 2 : 1, false, "DFLT").Select(g => g with { Cluster = item.End - 1, Text = "\u00AD" })];
        }

        /// <summary>Spreads the slack over the spaces between words; returns the new line width.</summary>
        private double Justify(List<LineRun> runs, double available, double width)
        {
            int spaces = runs.Sum(r => r.Glyphs.Count(g => _text[g.Cluster] is ' ' or '\u00A0'));
            if (spaces == 0 || available <= width) return width;
            double extra = (available - width) / spaces;
            double x = 0;
            for (int r = 0; r < runs.Count; r++)
            {
                LineRun run = runs[r];
                double toUnits = 1000 / run.Style.Size;
                var glyphs = run.Glyphs.Select(g => _text[g.Cluster] is ' ' or '\u00A0' ? g with { Advance = g.Advance + extra * toUnits } : g).ToList();
                double w = glyphs.Sum(g => g.Advance) * run.Style.Size / 1000;
                runs[r] = run with { Glyphs = glyphs, X = x, Width = w };
                x += w;
            }

            return x;
        }

        /// <summary>CSS line box: each span's ascent and descent, each spread by half the leading.</summary>
        private (double Ascent, double Descent) Metrics(List<LineRun> runs, int start)
        {
            double ascent = 0, descent = 0;
            IEnumerable<PdfTextStyle> styles = runs.Count > 0
                ? runs.Select(r => r.Style)
                : [_style[Math.Min(Math.Max(0, start - (start == _text.Length ? 1 : 0)), _text.Length - 1)]];
            foreach (PdfTextStyle s in styles)
            {
                PdfFont f = s.Font;
                double a = f.Ascent * s.Size, d = f.Descent * s.Size;
                double lineHeight = _p.LineHeight is double h ? h * s.Size : a + d + f.LineGap * s.Size;
                double half = (lineHeight - (a + d)) / 2;
                ascent = Math.Max(ascent, a + half);
                descent = Math.Max(descent, d + half);
            }

            return (ascent, descent);
        }

        private static bool IsParagraphSeparator(char c) => c is '\n' or '\r' or '\u2029' or '\u0085' or '\u001C' or '\u001D' or '\u001E';
    }

    private sealed record Item(int Start, int End, PdfTextStyle Style, PdfFont Font, bool Rtl, List<ShapedGlyph> Glyphs);
}
