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

        var lines = new List<List<PdfWord>>();
        foreach ((PdfPoint direction, List<int> members) in groups)
        {
            foreach (List<int> block in Blocks(letters, direction, members))
            {
                foreach (List<Item> line in GroupLines(letters, direction, block))
                {
                    List<PdfWord> words = Words(line);
                    if (words.Count > 0) lines.Add(words);
                }
            }
        }

        return lines;
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
    private static List<List<int>> Blocks(IReadOnlyList<PdfLetter> letters, PdfPoint d, List<int> members)
    {
        var n = new PdfPoint(-d.Y, d.X);
        var boxes = new List<Box>(members.Count);
        foreach (int i in members)
        {
            PdfLetter l = letters[i];
            double a = Dot(l.BaselineStart, d), b = Dot(l.BaselineEnd, d), v = Dot(l.BaselineStart, n), size = Math.Max(l.FontSize, 0.01);
            boxes.Add(new Box(i, Math.Min(a, b), Math.Max(a, b), v - (0.25 * size), v + (0.75 * size), size, Classify(l.Text) == Kind.Right, Math.Max(1, l.Text.Length)));
        }

        var result = new List<List<int>>();
        Cut(boxes, 0, result);
        return result;
    }

    private static void Cut(List<Box> region, int depth, List<List<int>> result)
    {
        if (depth < MaxCutDepth && region.Sum(b => b.Chars) >= 2 * MinColumnLines * MinLettersPerLine)
        {
            double em = Median(region.Select(b => b.Size));
            if (Columns(region, em) is (List<Box> first, List<Box> second))
            {
                Cut(first, depth + 1, result);
                Cut(second, depth + 1, result);
                return;
            }

            List<List<Box>> bands = Bands(region, em);
            if (bands.Count > 1)
            {
                foreach (List<Box> band in bands) Cut(band, depth + 1, result);
                return;
            }
        }

        result.Add([.. region.Select(b => b.Index)]);
    }

    /// <summary>The widest gutter of at least an em between two columns of prose, the reading side first, or null.</summary>
    private static (List<Box> First, List<Box> Second)? Columns(List<Box> region, double em)
    {
        List<Box> sorted = [.. region.OrderBy(b => b.U0)];
        double reach = double.MinValue, widest = 0;
        int split = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            double gap = sorted[i].U0 - reach;
            if (i > 0 && gap >= em && gap > widest && Prose(sorted, 0, i, em) && Prose(sorted, i, sorted.Count, em)) (widest, split) = (gap, i);
            reach = Math.Max(reach, sorted[i].U1);
        }

        if (split < 0) return null;
        List<Box> left = sorted.GetRange(0, split), right = sorted.GetRange(split, sorted.Count - split);
        bool rightToLeft = region.Where(b => b.RightToLeft).Sum(b => b.Chars) * 2 > region.Sum(b => b.Chars);
        return rightToLeft ? (right, left) : (left, right);
    }

    /// <summary>
    /// Whether letters [from, to) form a column of prose: enough lines, each long enough. Counted
    /// in characters, since an ActualText span is one letter for a whole run.
    /// </summary>
    private static bool Prose(List<Box> sorted, int from, int to, double em)
    {
        var lines = new HashSet<long>();
        int chars = 0;
        for (int i = from; i < to; i++)
        {
            lines.Add(Key(sorted[i].V0, em));
            chars += sorted[i].Chars;
        }

        return lines.Count >= MinColumnLines && chars / lines.Count >= MinLettersPerLine;
    }

    /// <summary>Bands parted by gaps across the whole region of at least 0.8 em, from the top down.</summary>
    private static List<List<Box>> Bands(List<Box> region, double em)
    {
        List<Box> sorted = [.. region.OrderByDescending(b => b.V1)];
        var bands = new List<List<Box>> { new() };
        double floor = double.MaxValue;
        foreach (Box box in sorted)
        {
            if (bands[^1].Count > 0 && floor - box.V1 >= 0.8 * em)
            {
                bands.Add([]);
                floor = double.MaxValue;
            }

            bands[^1].Add(box);
            floor = Math.Min(floor, box.V0);
        }

        return bands;
    }

    private static double Median(IEnumerable<double> values)
    {
        List<double> sorted = [.. values.OrderBy(v => v)];
        return sorted.Count == 0 ? 1 : sorted[sorted.Count / 2];
    }

    /// <summary>A letter's extent in its direction's frame: along the baseline, and across it from descent to ascent.</summary>
    private readonly record struct Box(int Index, double U0, double U1, double V0, double V1, double Size, bool RightToLeft, int Chars);

    /// <summary>The text of a page: lines top to bottom, words separated by a space, lines by a line feed.</summary>
    public static string Text(IReadOnlyList<PdfLetter> letters)
    {
        var sb = new StringBuilder();
        foreach (List<PdfWord> line in Lines(letters))
        {
            if (sb.Length > 0) sb.Append('\n');
            for (int i = 0; i < line.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(line[i].Text);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Lines across one direction. Each letter has a band across the baseline, from a quarter of
    /// its size below to three quarters above. The largest letters found the lines; each smaller
    /// letter joins the line whose band its own overlaps most, by at least half its size, so a
    /// superscript joins its line rather than a heading just above it. Lines read from the top
    /// of the frame down, letters along the baseline.
    /// </summary>
    private static List<List<Item>> GroupLines(IReadOnlyList<PdfLetter> letters, PdfPoint d, List<int> members)
    {
        var n = new PdfPoint(-d.Y, d.X);
        var items = new List<Item>(members.Count);
        foreach (int i in members)
        {
            PdfLetter l = letters[i];
            double a = Dot(l.BaselineStart, d), b = Dot(l.BaselineEnd, d);
            items.Add(new Item(i, l, Math.Min(a, b), Math.Max(a, b), Dot(l.BaselineStart, n), Math.Max(l.FontSize, 0.01)));
        }

        items.Sort((x, y) => x.Size != y.Size ? y.Size.CompareTo(x.Size) : x.V != y.V ? y.V.CompareTo(x.V) : x.Index.CompareTo(y.Index));

        // Lines are found through buckets as tall as the largest letter, so a letter only looks at
        // lines near it; a bucket is searched from its newest line, at most MaxCandidates of them.
        double bucket = items.Count > 0 ? items[0].Size : 1;
        var buckets = new Dictionary<long, List<Line>>();
        var lines = new List<Line>();
        foreach (Item item in items)
        {
            long key = Key(item.V, bucket);
            Line? best = null;
            double bestScore = 0;
            for (long k = key - 1; k <= key + 1; k++)
            {
                if (!buckets.TryGetValue(k, out List<Line>? near)) continue;
                for (int j = near.Count - 1; j >= 0 && j >= near.Count - MaxCandidates; j--)
                {
                    Line line = near[j];
                    double score = Overlap(item, line.Reference) / item.Size;
                    if (score >= 0.5 && (score > bestScore || (score == bestScore && Math.Abs(item.V - line.Reference.V) < Math.Abs(item.V - best!.Reference.V))))
                    {
                        (best, bestScore) = (line, score);
                    }
                }
            }

            if (best is null)
            {
                best = new Line(item, lines.Count);
                lines.Add(best);
                if (!buckets.TryGetValue(key, out List<Line>? list)) buckets[key] = list = [];
                list.Add(best);
            }

            best.Items.Add(item);
        }

        lines.Sort((x, y) => x.Reference.V != y.Reference.V ? y.Reference.V.CompareTo(x.Reference.V) : x.Id.CompareTo(y.Id));
        var result = new List<List<Item>>(lines.Count);
        foreach (Line line in lines)
        {
            line.Items.Sort((x, y) => x.U0 != y.U0 ? x.U0.CompareTo(y.U0) : x.Index.CompareTo(y.Index));
            result.Add(line.Items);
        }

        return result;
    }

    private static long Key(double v, double bucket) => (long)Math.Floor(Math.Max(-1e15, Math.Min(1e15, v / bucket)));

    /// <summary>How far two letters' bands overlap across the baseline.</summary>
    private static double Overlap(Item a, Item b)
    {
        double top = Math.Min(a.V + (0.75 * a.Size), b.V + (0.75 * b.Size));
        double bottom = Math.Max(a.V - (0.25 * a.Size), b.V - (0.25 * b.Size));
        return top - bottom;
    }

    /// <summary>Splits a line, in visual order, into words in reading order.</summary>
    private static List<PdfWord> Words(List<Item> visual)
    {
        // A letter drawn again over itself, as for fake bold, counts once.
        var kept = new List<Item>(visual.Count);
        foreach (Item item in visual)
        {
            if (kept.Count > 0 && kept[^1].Letter.Text == item.Letter.Text && Math.Abs(kept[^1].U0 - item.U0) < 0.1 * item.Size
                && Math.Abs(kept[^1].V - item.V) < 0.1 * item.Size)
            {
                continue;
            }

            kept.Add(item);
        }

        // A gap wider than a fraction of the font size separates words, as a space glyph does.
        var gapBefore = new bool[kept.Count];
        for (int k = 1; k < kept.Count; k++)
        {
            double size = (kept[k - 1].Size + kept[k].Size) / 2;
            gapBefore[k] = kept[k].U0 - kept[k - 1].U1 > WordGap * size;
        }

        int[] order = LogicalOrder(kept);
        var words = new List<PdfWord>();
        var text = new StringBuilder();
        var members = new List<PdfLetter>();
        for (int j = 0; j < order.Length; j++)
        {
            Item item = kept[order[j]];
            if (j > 0)
            {
                int a = order[j - 1], b = order[j];
                if (Math.Abs(a - b) != 1 || gapBefore[Math.Max(a, b)]) Flush();
            }

            foreach (char c in item.Letter.Text)
            {
                if (char.IsWhiteSpace(c))
                {
                    Flush();
                    continue;
                }

                text.Append(c);
                if (members.Count == 0 || members[^1] != item.Letter) members.Add(item.Letter);
            }
        }

        Flush();
        return words;

        void Flush()
        {
            if (text.Length > 0)
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
            }

            text.Clear();
            members.Clear();
        }
    }

    /// <summary>
    /// The logical order of a line's letters from their visual order (Unicode Standard Annex #9,
    /// rule L2, run backwards). In a right to left paragraph left to right letters sit at level 2, right to left letters at level 1, numbers take
    /// the left to right level, and neutrals take the level around them or the paragraph's. Since
    /// reversing runs is its own inverse, applying L2 to the visual order gives the logical order.
    /// </summary>
    private static int[] LogicalOrder(List<Item> visual)
    {
        int n = visual.Count;
        int[] order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var types = new Kind[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            types[i] = Classify(visual[i].Letter.Text);
            any |= types[i] == Kind.Right;
        }

        if (!any) return order;

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
    private readonly record struct Item(int Index, PdfLetter Letter, double U0, double U1, double V, double Size);

    /// <summary>A line and the letter that founded it, its largest.</summary>
    private sealed class Line(Item reference, int id)
    {
        public Item Reference { get; } = reference;

        public int Id { get; } = id;

        public List<Item> Items { get; } = [];
    }
}
