using System.Text;
using PdfNetStack.Drawing;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Puts letters in reading order: groups them by writing direction, then into lines across that
/// direction, top to bottom; orders each line along the direction and splits it into words at
/// spaces and gaps. Right to left runs are put back in logical order.
/// </summary>
/// <remarks>
/// Every letter is measured in the frame of its own baseline, so rotated text and vertical writing
/// read like ordinary lines. Columns of prose are read one after the other (see Blocks).
/// </remarks>
internal static class TextLayout
{
    /// <summary>Directions within about 5 degrees are one direction.</summary>
    private const double SameDirection = 0.996;

    private const int MaxDirections = 32;

    /// <summary>Lines a letter compares itself with per bucket.</summary>
    private const int MaxCandidates = 64;

    /// <summary>A gap wider than this many font sizes ends a word.</summary>
    private const double WordGap = 0.15;

    /// <summary>Lines of words in reading order.</summary>
    public static List<List<PdfWord>> Lines(IReadOnlyList<PdfLetter> letters)
    {
        var lines = new List<List<PdfWord>>();
        foreach (Item[] line in VisualLines(letters))
        {
            var words = new List<PdfWord>();
            Words(line, words, null);
            if (words.Count > 0) lines.Add(words);
        }

        return lines;
    }

    /// <summary>The text of a page: lines top to bottom, words separated by a space, lines by a line feed.</summary>
    public static string Text(IReadOnlyList<PdfLetter> letters)
    {
        // The words of Lines, written straight into the text without a PdfWord for each.
        var sb = new StringBuilder();
        foreach (Item[] line in VisualLines(letters))
        {
            int mark = sb.Length;
            if (mark > 0) sb.Append('\n');
            int start = sb.Length;
            Words(line, null, sb);
            if (sb.Length == start) sb.Length = mark;
        }

        return sb.ToString();
    }

    /// <summary>The lines of every direction and block in reading order, each in visual order.</summary>
    private static IEnumerable<Item[]> VisualLines(IReadOnlyList<PdfLetter> letters)
    {
        // The direction of each letter's text, needed by both the blocks and the words.
        var kinds = new Kind[letters.Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = Classify(letters[i].Text);

        // Directions in the order they first appear, each with its letters in content order.
        var groups = new List<(PdfPoint Direction, List<int> Letters)>();
        for (int i = 0; i < letters.Count; i++)
        {
            PdfPoint d = letters[i].Direction;
            int best = -1;
            double closest = double.MinValue;
            for (int g = 0; g < groups.Count; g++)
            {
                double dot = Dot(d, groups[g].Direction);
                if (dot > closest) (best, closest) = (g, dot);
            }

            if (best < 0 || (closest < SameDirection && groups.Count < MaxDirections))
            {
                groups.Add((d, []));
                best = groups.Count - 1;
            }

            groups[best].Letters.Add(i);
        }

        foreach ((PdfPoint direction, List<int> members) in groups)
        {
            foreach (List<int> block in Blocks(letters, kinds, direction, members))
            {
                foreach (Item[] line in GroupLines(letters, kinds, direction, block)) yield return line;
            }
        }
    }

    /// <summary>Recursion depth of the block segmentation.</summary>
    private const int MaxCutDepth = 24;

    /// <summary>Lines and letters per line a side of a gutter needs to read as a column of prose.</summary>
    private const int MinColumnLines = 3;

    private const int MinLettersPerLine = 12;

    /// <summary>
    /// Splits one direction's letters into blocks read one after the other, by recursive XY cuts
    /// (Nagy and Seth, 1984): a gutter running the whole height of a region parts columns, a gap
    /// running its whole width parts bands, top first. A gutter only counts between two columns of
    /// prose, at least three lines of a dozen letters each, so tables and label and value pairs
    /// keep reading row by row. Columns read left to right, right to left where the region's
    /// text mostly is.
    /// </summary>
    private static List<List<int>> Blocks(IReadOnlyList<PdfLetter> letters, Kind[] kinds, PdfPoint d, List<int> members)
    {
        // Letters that follow each other in content order on one baseline, at one size and with
        // no gap between them (a word, typically) make one box. A gap that small never reads as a
        // gutter, and the boxes count lines, characters and sizes as their letters would, so the
        // cut is the same as one over the letters, with far fewer boxes to sort.
        var n = new PdfPoint(-d.Y, d.X);
        var boxes = new List<Box>();
        for (int k = 0; k < members.Count; k++)
        {
            int i = members[k];
            PdfLetter l = letters[i];
            double a = Dot(l.BaselineStart, d), b = Dot(l.BaselineEnd, d), v = Dot(l.BaselineStart, n), size = Math.Max(l.FontSize, 0.01);
            double u0 = Math.Min(a, b), u1 = Math.Max(a, b), v0 = v - (0.25 * size), v1 = v + (0.75 * size);
            bool rightToLeft = kinds[i] == Kind.Right;
            int chars = Math.Max(1, l.Text.Length);
            if (boxes.Count > 0)
            {
                Box run = boxes[^1];
                if (run.V0 == v0 && run.V1 == v1 && run.Size == size && run.RightToLeft == rightToLeft && u0 >= run.U0 && u0 <= run.U1 + Touching)
                {
                    boxes[^1] = run with { Count = run.Count + 1, U1 = Math.Max(run.U1, u1), Chars = run.Chars + chars };
                    continue;
                }
            }

            boxes.Add(new Box(k, 1, u0, u1, v0, v1, size, rightToLeft, chars));
        }

        var result = new List<List<int>>();
        new Regions([.. boxes], members).Cut(0, boxes.Count, 0, result);
        return result;
    }

    /// <summary>
    /// The widest hole a run of letters may have along the baseline: half the smallest em a
    /// region can have, so never a gutter.
    /// </summary>
    private const double Touching = 0.005;

    /// <summary>
    /// The XY cut over one array of boxes. A region is a range of it: sorting a region puts each
    /// column or band it splits into in a range of its own, so nothing is copied.
    /// </summary>
    /// <remarks>
    /// The order of boxes within a region never matters: a gap is measured between boxes of
    /// distinct coordinates, and the lines of a block are sorted again. So sorts need not keep
    /// the previous order among equal keys.
    /// </remarks>
    private sealed class Regions(Box[] boxes, List<int> members)
    {
        // Sort scratch: keys and positions are sorted together, then the boxes, which are large
        // to move, are moved once.
        private readonly double[] _keys = new double[boxes.Length];
        private readonly int[] _positions = new int[boxes.Length];
        private readonly Box[] _moved = new Box[boxes.Length];

        public void Cut(int from, int to, int depth, List<List<int>> result)
        {
            if (depth < MaxCutDepth && Chars(from, to) >= 2 * MinColumnLines * MinLettersPerLine)
            {
                double em = Median(from, to);
                if (Columns(from, to, em) is (int split, bool rightToLeft))
                {
                    if (rightToLeft)
                    {
                        Cut(split, to, depth + 1, result);
                        Cut(from, split, depth + 1, result);
                    }
                    else
                    {
                        Cut(from, split, depth + 1, result);
                        Cut(split, to, depth + 1, result);
                    }

                    return;
                }

                List<int> bands = Bands(from, to, em);
                if (bands.Count > 2)
                {
                    for (int i = 0; i + 1 < bands.Count; i++) Cut(bands[i], bands[i + 1], depth + 1, result);
                    return;
                }
            }

            // The block's letters in content order, which lines mostly keep along the baseline.
            var block = new List<int>();
            for (int i = from; i < to; i++)
            {
                for (int k = boxes[i].Start; k < boxes[i].Start + boxes[i].Count; k++) block.Add(members[k]);
            }

            block.Sort();
            result.Add(block);
        }

        private int Chars(int from, int to)
        {
            int chars = 0;
            for (int i = from; i < to; i++) chars += boxes[i].Chars;
            return chars;
        }

        /// <summary>
        /// The widest gutter of at least an em between two columns of prose, as the start of the
        /// right column after the region is sorted left to right, and whether the right column
        /// reads first; or null.
        /// </summary>
        private (int Split, bool RightToLeft)? Columns(int from, int to, double em)
        {
            for (int i = from; i < to; i++) _keys[i] = boxes[i].U0;
            Sort(from, to);
            double reach = double.MinValue, widest = 0;
            int split = -1;
            for (int i = from; i < to; i++)
            {
                double gap = boxes[i].U0 - reach;
                if (i > from && gap >= em && gap > widest && Prose(from, i, em) && Prose(i, to, em)) (widest, split) = (gap, i);
                reach = Math.Max(reach, boxes[i].U1);
            }

            if (split < 0) return null;
            int rightToLeftChars = 0;
            for (int i = from; i < to; i++)
            {
                if (boxes[i].RightToLeft) rightToLeftChars += boxes[i].Chars;
            }

            return (split, rightToLeftChars * 2 > Chars(from, to));
        }

        /// <summary>
        /// Whether letters [from, to) form a column of prose: enough lines, each long enough. Counted
        /// in characters, since an ActualText span is one letter for a whole run.
        /// </summary>
        private bool Prose(int from, int to, double em)
        {
            var lines = new HashSet<long>();
            int chars = 0;
            for (int i = from; i < to; i++)
            {
                lines.Add(Key(boxes[i].V0, em));
                chars += boxes[i].Chars;
            }

            return lines.Count >= MinColumnLines && chars / lines.Count >= MinLettersPerLine;
        }

        /// <summary>
        /// Bands parted by gaps across the whole region of at least 0.8 em, from the top down, as
        /// the boundaries of their ranges after the region is sorted top down: from first to to last.
        /// </summary>
        private List<int> Bands(int from, int to, double em)
        {
            // By V1 negated, which orders exactly as V1 descending.
            for (int i = from; i < to; i++) _keys[i] = -boxes[i].V1;
            Sort(from, to);
            var bounds = new List<int> { from };
            double floor = double.MaxValue;
            for (int i = from; i < to; i++)
            {
                if (i > bounds[^1] && floor - boxes[i].V1 >= 0.8 * em)
                {
                    bounds.Add(i);
                    floor = double.MaxValue;
                }

                floor = Math.Min(floor, boxes[i].V0);
            }

            bounds.Add(to);
            return bounds;
        }

        /// <summary>The median size of the region's letters, each box counting once per letter.</summary>
        private double Median(int from, int to)
        {
            if (to == from) return 1;
            int letters = 0;
            for (int i = from; i < to; i++)
            {
                _keys[i] = boxes[i].Size;
                _positions[i] = boxes[i].Count;
                letters += boxes[i].Count;
            }

            Array.Sort(_keys, _positions, from, to - from);
            for (int i = from, seen = 0; ; i++)
            {
                seen += _positions[i];
                if (seen > letters / 2) return _keys[i];
            }
        }

        /// <summary>Sorts boxes [from, to) by the keys in the same range.</summary>
        private void Sort(int from, int to)
        {
            for (int i = from; i < to; i++) _positions[i] = i;
            Array.Sort(_keys, _positions, from, to - from);
            for (int i = from; i < to; i++) _moved[i] = boxes[_positions[i]];
            Array.Copy(_moved, from, boxes, from, to - from);
        }
    }

    /// <summary>
    /// The extent of a run of letters in their direction's frame: along the baseline, and across
    /// it from descent to ascent. The letters are members [Start, Start + Count) of a direction.
    /// </summary>
    private readonly record struct Box(int Start, int Count, double U0, double U1, double V0, double V1, double Size, bool RightToLeft, int Chars);

    /// <summary>
    /// Lines across one direction. Each letter has a band across the baseline, from a quarter of
    /// its size below to three quarters above. The largest letters found the lines; each smaller
    /// letter joins the line whose band its own overlaps most, by at least half its size, so a
    /// superscript joins its line rather than a heading just above it. Lines read from the top
    /// of the frame down, letters along the baseline.
    /// </summary>
    private static List<Item[]> GroupLines(IReadOnlyList<PdfLetter> letters, Kind[] kinds, PdfPoint d, List<int> members)
    {
        var n = new PdfPoint(-d.Y, d.X);
        var items = new Item[members.Count];
        for (int k = 0; k < items.Length; k++)
        {
            int i = members[k];
            PdfLetter l = letters[i];
            double a = Dot(l.BaselineStart, d), b = Dot(l.BaselineEnd, d);
            items[k] = new Item(i, l, Math.Min(a, b), Math.Max(a, b), Dot(l.BaselineStart, n), Math.Max(l.FontSize, 0.01), kinds[i]);
        }

        // Letters of one size on one baseline always join the same line, whatever their order, so
        // lines are found once for each distinct size and position: largest first, then from the
        // top down.
        var lineOf = new Dictionary<(double Size, double V), Line?>();
        foreach (Item item in items) lineOf[(item.Size, item.V)] = null;
        var keys = new List<(double Size, double V)>(lineOf.Keys);
        keys.Sort((x, y) => x.Size != y.Size ? y.Size.CompareTo(x.Size) : y.V.CompareTo(x.V));

        // Lines are found through buckets as tall as the largest letter, so a letter only looks at
        // lines near it; a bucket is searched from its newest line, at most MaxCandidates of them.
        double bucket = keys.Count > 0 ? keys[0].Size : 1;
        var buckets = new Dictionary<long, List<Line>>();
        var lines = new List<Line>();
        foreach ((double size, double v) in keys)
        {
            long key = Key(v, bucket);
            Line? best = null;
            double bestScore = 0;
            for (long k = key - 1; k <= key + 1; k++)
            {
                if (!buckets.TryGetValue(k, out List<Line>? near)) continue;
                for (int j = near.Count - 1; j >= 0 && j >= near.Count - MaxCandidates; j--)
                {
                    Line line = near[j];
                    double score = Overlap(v, size, line.V, line.Size) / size;
                    if (score >= 0.5 && (score > bestScore || (score == bestScore && Math.Abs(v - line.V) < Math.Abs(v - best!.V))))
                    {
                        (best, bestScore) = (line, score);
                    }
                }
            }

            if (best is null)
            {
                best = new Line(v, size, lines.Count);
                lines.Add(best);
                if (!buckets.TryGetValue(key, out List<Line>? list)) buckets[key] = list = [];
                list.Add(best);
            }

            lineOf[(size, v)] = best;
        }

        // Each line's letters in arrays of their exact size, in content order.
        foreach (Item item in items) lineOf[(item.Size, item.V)]!.Count++;
        foreach (Line line in lines)
        {
            line.Items = new Item[line.Count];
            line.Count = 0;
        }

        foreach (Item item in items)
        {
            Line line = lineOf[(item.Size, item.V)]!;
            line.Items[line.Count++] = item;
        }

        lines.Sort((x, y) => x.V != y.V ? y.V.CompareTo(x.V) : x.Id.CompareTo(y.Id));
        var result = new List<Item[]>(lines.Count);
        foreach (Line line in lines)
        {
            AlongBaseline(line.Items);
            result.Add(line.Items);
        }

        return result;
    }

    /// <summary>Sorts a line's letters along the baseline, equal positions in content order.</summary>
    private static void AlongBaseline(Item[] line)
    {
        // Letters mostly come in that order already.
        bool sorted = true;
        for (int k = 1; k < line.Length && sorted; k++) sorted = Before(line[k - 1], line[k]);
        if (sorted) return;

        // Positions are sorted rather than the letters, which are large to move.
        var keys = new double[line.Length];
        var positions = new int[line.Length];
        for (int k = 0; k < line.Length; k++) (keys[k], positions[k]) = (line[k].U0, k);
        Array.Sort(keys, positions);

        // Letters at one position, as for fake bold, are rare and few: put each run in content order.
        for (int start = 0; start < line.Length;)
        {
            int end = start + 1;
            while (end < line.Length && keys[end] == keys[start]) end++;
            if (end - start > 1)
            {
                int[] run = positions[start..end];
                var indices = new int[run.Length];
                for (int k = 0; k < run.Length; k++) indices[k] = line[run[k]].Index;
                Array.Sort(indices, run);
                Array.Copy(run, 0, positions, start, run.Length);
            }

            start = end;
        }

        Item[] copy = [.. line];
        for (int k = 0; k < line.Length; k++) line[k] = copy[positions[k]];

        static bool Before(in Item x, in Item y) => x.U0 < y.U0 || (x.U0 == y.U0 && x.Index < y.Index);
    }

    private static long Key(double v, double bucket) => (long)Math.Floor(Math.Max(-1e15, Math.Min(1e15, v / bucket)));

    /// <summary>How far the bands of two letters, at their baseline position and size, overlap across the baseline.</summary>
    private static double Overlap(double v, double size, double otherV, double otherSize)
    {
        double top = Math.Min(v + (0.75 * size), otherV + (0.75 * otherSize));
        double bottom = Math.Max(v - (0.25 * size), otherV - (0.25 * otherSize));
        return top - bottom;
    }

    /// <summary>
    /// Splits a line, in visual order, into words in reading order: into <paramref name="words"/>,
    /// or as text into <paramref name="output"/>, the words separated by a space.
    /// </summary>
    private static void Words(Item[] visual, List<PdfWord>? words, StringBuilder? output)
    {
        // A letter drawn again over itself, as for fake bold, counts once. The line is the caller's
        // to change: kept letters move to its front.
        int count = 0;
        foreach (Item item in visual)
        {
            if (count > 0)
            {
                ref readonly Item last = ref visual[count - 1];
                if (last.Letter.Text == item.Letter.Text && Math.Abs(last.U0 - item.U0) < 0.1 * item.Size && Math.Abs(last.V - item.V) < 0.1 * item.Size) continue;
            }

            visual[count++] = item;
        }

        // A gap wider than a fraction of the font size separates words, as a space glyph does.
        var gapBefore = new bool[count];
        for (int k = 1; k < count; k++)
        {
            double size = (visual[k - 1].Size + visual[k].Size) / 2;
            gapBefore[k] = visual[k].U0 - visual[k - 1].U1 > WordGap * size;
        }

        int[]? order = LogicalOrder(visual, count);
        StringBuilder text = output ?? new StringBuilder();
        int lineStart = text.Length, wordStart = -1;
        List<PdfLetter>? members = words is null ? null : [];
        for (int j = 0; j < count; j++)
        {
            ref readonly Item item = ref visual[order is null ? j : order[j]];
            if (j > 0)
            {
                // Letters next to each other in the visual order and without a gap stay in one word.
                int a = order is null ? j - 1 : order[j - 1], b = order is null ? j : order[j];
                if (Math.Abs(a - b) != 1 || gapBefore[Math.Max(a, b)]) Flush();
            }

            foreach (char c in item.Letter.Text)
            {
                if (char.IsWhiteSpace(c))
                {
                    Flush();
                    continue;
                }

                if (wordStart < 0)
                {
                    if (output is not null && text.Length > lineStart) text.Append(' ');
                    wordStart = text.Length;
                }

                text.Append(c);
                if (members is not null && (members.Count == 0 || members[^1] != item.Letter)) members.Add(item.Letter);
            }
        }

        Flush();

        void Flush()
        {
            if (wordStart >= 0 && words is not null && members is not null)
            {
                double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
                foreach (PdfLetter l in members)
                {
                    left = Math.Min(left, l.Bounds.Left);
                    bottom = Math.Min(bottom, l.Bounds.Bottom);
                    right = Math.Max(right, l.Bounds.Right);
                    top = Math.Max(top, l.Bounds.Top);
                }

                words.Add(new PdfWord(text.ToString(), new PdfRectangle(left, bottom, right, top), [.. members]));
                text.Clear();
                members.Clear();
            }

            wordStart = -1;
        }
    }

    /// <summary>
    /// The logical order of a line's letters from their visual order (Unicode Standard Annex #9,
    /// rule L2, run backwards). In a right to left paragraph left to right letters sit at level 2, right to left letters at level 1, numbers take
    /// the left to right level, and neutrals take the level around them or the paragraph's. Since
    /// reversing runs is its own inverse, applying L2 to the visual order gives the logical order.
    /// Null when that is the visual order: a line without right to left letters.
    /// </summary>
    private static int[]? LogicalOrder(Item[] visual, int n)
    {
        bool any = false;
        for (int i = 0; i < n && !any; i++) any = visual[i].Kind == Kind.Right;
        if (!any) return null;

        int[] order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var types = new Kind[n];
        for (int i = 0; i < n; i++) types[i] = visual[i].Kind;

        // A left to right paragraph starts with a left to right character (rule P2), at the left
        // end; so a line whose leftmost strong character is right to left is a right to left one.
        int paragraph = Strong(types, -1, 1) == Kind.Right ? 1 : 0;
        int[] levels = new int[n];
        for (int i = 0; i < n; i++)
        {
            levels[i] = types[i] switch
            {
                Kind.Right => 1,
                Kind.Left => paragraph == 1 ? 2 : 0,
                Kind.Number when paragraph == 1 => 2,
                Kind.Number => Strong(types, i, -1) == Kind.Right && Strong(types, i, 1) == Kind.Right ? 2 : 0,
                _ => -1,
            };
        }

        for (int i = 0; i < n; i++)
        {
            if (levels[i] >= 0) continue;
            int before = Neighbor(levels, i, -1, paragraph), after = Neighbor(levels, i, 1, paragraph);
            levels[i] = before == after ? before : paragraph;
        }

        int max = levels.Max();
        for (int level = max; level >= 1; level--)
        {
            for (int i = 0; i < n;)
            {
                if (levels[order[i]] < level)
                {
                    i++;
                    continue;
                }

                int end = i;
                while (end < n && levels[order[end]] >= level) end++;
                Array.Reverse(order, i, end - i);
                i = end;
            }
        }

        return order;
    }

    private static Kind Strong(Kind[] types, int at, int step)
    {
        for (int i = at + step; i >= 0 && i < types.Length; i += step)
        {
            if (types[i] is Kind.Left or Kind.Right) return types[i];
        }

        return Kind.Neutral;
    }

    private static int Neighbor(int[] levels, int at, int step, int paragraph)
    {
        for (int i = at + step; i >= 0 && i < levels.Length; i += step)
        {
            if (levels[i] >= 0) return levels[i];
        }

        return paragraph;
    }

    /// <summary>The direction of a letter's text: its first strong character, else a digit, else neutral.</summary>
    private static Kind Classify(string text)
    {
        bool number = false;
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int cp = char.IsSurrogate(text[i]) && !char.IsSurrogatePair(text, i) ? 0xFFFD : char.ConvertToUtf32(text, i);
            switch (UnicodeData.Bidi(cp))
            {
                case BidiClass.L:
                    return Kind.Left;
                case BidiClass.R or BidiClass.AL:
                    return Kind.Right;
                case BidiClass.EN or BidiClass.AN:
                    number = true;
                    break;
            }
        }

        return number ? Kind.Number : Kind.Neutral;
    }

    private static double Dot(PdfPoint a, PdfPoint b) => (a.X * b.X) + (a.Y * b.Y);

    private enum Kind
    {
        Neutral,
        Left,
        Right,
        Number,
    }

    /// <summary>A letter in its direction's frame: U along the baseline, V across it, from the bottom up.</summary>
    private readonly record struct Item(int Index, PdfLetter Letter, double U0, double U1, double V, double Size, Kind Kind);

    /// <summary>A line, at the baseline position and size of the letter that founded it, its largest.</summary>
    private sealed class Line(double v, double size, int id)
    {
        public double V { get; } = v;

        public double Size { get; } = size;

        public int Id { get; } = id;

        /// <summary>Letters that joined the line, then letters placed in <see cref="Items"/>.</summary>
        public int Count { get; set; }

        /// <summary>The letters, once every letter has found its line.</summary>
        public Item[] Items { get; set; } = [];
    }
}
