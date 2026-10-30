namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Regular grammars over shaping categories, the way the script specifications define syllables:
/// each grammar compiles to a Thompson NFA, and text splits into syllables by the longest match
/// of any grammar at each position, the earlier grammar winning a tie. Categories are below 64.
/// </summary>
internal static class SyllableGrammar
{
    /// <summary>A run of steps that cannot end a syllable stops the search; real syllables never have one this long.</summary>
    private const int MaxDeadSteps = 32;

    public static Rx Sym(params int[] categories) => new SymbolRx(categories.Aggregate(0L, (m, k) => m | 1L << k));

    public static Rx Seq(params Rx[] parts) => new SequenceRx(parts);

    public static Rx Or(params Rx[] parts) => new ChoiceRx(parts);

    public static Rx Opt(Rx r) => new ChoiceRx([r, new SequenceRx([])]);

    public static Rx Star(Rx r) => new StarRx(r);

    public static Rx Plus(Rx r) => Seq(r, Star(r));

    /// <summary>
    /// Splits <paramref name="categories"/> into syllables: the longest match of any machine, the
    /// earlier machine on a tie; where none matches, one item of type <paramref name="other"/>.
    /// </summary>
    public static List<(int Start, int Length, int Type)> Split(Machine[] machines, IReadOnlyList<int> categories, int other)
    {
        var result = new List<(int, int, int)>();
        var scratch = new Scratch();
        for (int start = 0; start < categories.Count;)
        {
            int best = 0, type = other;
            for (int m = 0; m < machines.Length; m++)
            {
                int length = machines[m].Longest(categories, start, scratch);
                if (length <= best) continue;
                best = length;
                type = m;
            }

            if (best == 0) best = 1;
            result.Add((start, best, type));
            start += best;
        }

        return result;
    }

    /// <summary>A regular expression over categories.</summary>
    internal abstract class Rx
    {
        public abstract (int Start, int End) Build(List<List<(long Mask, int To)>> edges);

        protected static int State(List<List<(long Mask, int To)>> edges)
        {
            edges.Add([]);
            return edges.Count - 1;
        }
    }

    private sealed class SymbolRx(long mask) : Rx
    {
        public override (int Start, int End) Build(List<List<(long Mask, int To)>> edges)
        {
            int s = State(edges), e = State(edges);
            edges[s].Add((mask, e));
            return (s, e);
        }
    }

    private sealed class SequenceRx(Rx[] parts) : Rx
    {
        public override (int Start, int End) Build(List<List<(long Mask, int To)>> edges)
        {
            int s = State(edges), e = s;
            foreach (Rx part in parts)
            {
                (int ps, int pe) = part.Build(edges);
                edges[e].Add((0, ps));
                e = pe;
            }

            return (s, e);
        }
    }

    private sealed class ChoiceRx(Rx[] parts) : Rx
    {
        public override (int Start, int End) Build(List<List<(long Mask, int To)>> edges)
        {
            int s = State(edges), e = State(edges);
            foreach (Rx part in parts)
            {
                (int ps, int pe) = part.Build(edges);
                edges[s].Add((0, ps));
                edges[pe].Add((0, e));
            }

            return (s, e);
        }
    }

    private sealed class StarRx(Rx inner) : Rx
    {
        public override (int Start, int End) Build(List<List<(long Mask, int To)>> edges)
        {
            int s = State(edges), e = State(edges);
            (int ps, int pe) = inner.Build(edges);
            edges[s].Add((0, ps));
            edges[s].Add((0, e));
            edges[pe].Add((0, s));
            return (s, e);
        }
    }

    /// <summary>Working state of a search, reused from one position and machine to the next.</summary>
    internal sealed class Scratch
    {
        public bool[] Current = [];
        public bool[] Next = [];
        public readonly Stack<int> Stack = new();
        public readonly List<int> Reached = [];
    }

    /// <summary>A compiled grammar.</summary>
    internal sealed class Machine
    {
        private readonly (long Mask, int To)[][] _edges;
        private readonly int _start, _accept;

        private Machine((long Mask, int To)[][] edges, int start, int accept)
        {
            _edges = edges;
            _start = start;
            _accept = accept;
        }

        public static Machine From(Rx rx)
        {
            var edges = new List<List<(long Mask, int To)>>();
            (int start, int end) = rx.Build(edges);
            return new Machine([.. edges.Select(e => e.ToArray())], start, end);
        }

        /// <summary>Length of the longest match at <paramref name="start"/>, 0 when there is none.</summary>
        public int Longest(IReadOnlyList<int> categories, int start, Scratch scratch)
        {
            if (scratch.Current.Length < _edges.Length)
            {
                scratch.Current = new bool[_edges.Length];
                scratch.Next = new bool[_edges.Length];
            }

            bool[] current = scratch.Current, next = scratch.Next;
            Array.Clear(current, 0, current.Length);
            Stack<int> stack = scratch.Stack;
            List<int> reached = scratch.Reached;
            reached.Clear();
            reached.Add(_start);
            Close(current, reached, stack);
            int best = 0, dead = 0;
            for (int k = start; k < categories.Count && dead < MaxDeadSteps; k++)
            {
                Array.Clear(next, 0, next.Length);
                reached.Clear();
                long bit = 1L << categories[k];
                for (int s = 0; s < _edges.Length; s++)
                {
                    if (!current[s]) continue;
                    foreach ((long mask, int to) in _edges[s])
                    {
                        if ((mask & bit) != 0) reached.Add(to);
                    }
                }

                if (reached.Count == 0) break;
                Close(next, reached, stack);
                (current, next) = (next, current);
                if (current[_accept])
                {
                    best = k - start + 1;
                    dead = 0;
                }
                else
                {
                    dead++;
                }
            }

            return best;
        }

        private void Close(bool[] set, List<int> seeds, Stack<int> stack)
        {
            foreach (int s in seeds)
            {
                if (set[s]) continue;
                set[s] = true;
                stack.Push(s);
            }

            while (stack.Count > 0)
            {
                int s = stack.Pop();
                foreach ((long mask, int to) in _edges[s])
                {
                    if (mask != 0 || set[to]) continue;
                    set[to] = true;
                    stack.Push(to);
                }
            }
        }
    }
}
