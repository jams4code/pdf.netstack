using static PdfNetStack.Text.BidiClass;

namespace PdfNetStack.Text;

/// <summary>
/// The Unicode bidirectional algorithm, UAX #9 (Unicode 16): paragraph level (P2, P3), explicit
/// embeddings and isolates (X1 to X10), weak and neutral types with bracket pairs (W1 to N2),
/// implicit levels (I1, I2), then per line the whitespace rule (L1) and reordering (L2).
/// Conformance is tested against the Unicode BidiCharacterTest file.
/// </summary>
internal sealed class Bidi
{
    private const int MaxDepth = 125;
    private const int MaxBracketPairs = 63;

    private readonly int[] _codePoints;
    private readonly BidiClass[] _original;
    private readonly BidiClass[] _types;
    private readonly byte[] _levels;
    private byte[] _explicitLevels = [];
    private readonly bool[] _removed;
    private readonly int[] _matchingPdi;
    private readonly int[] _matchingInitiator;

    private Bidi(int[] codePoints, int direction)
    {
        int n = codePoints.Length;
        _codePoints = codePoints;
        _original = new BidiClass[n];
        for (int i = 0; i < n; i++) _original[i] = UnicodeData.Bidi(codePoints[i]);
        _types = (BidiClass[])_original.Clone();
        _levels = new byte[n];
        _removed = new bool[n];
        _matchingPdi = new int[n];
        _matchingInitiator = new int[n];
        MatchIsolates();
        ParagraphLevel = direction switch
        {
            0 => (byte)0,
            1 => (byte)1,
            _ => FirstStrongLevel(0, n) ?? 0,
        };
        Explicit();

        // sos and eos compare explicit levels; resolving a sequence must not change what the next one sees.
        _explicitLevels = (byte[])_levels.Clone();
        foreach (List<int> sequence in IsolatingRunSequences()) Resolve(sequence);
        AssignRemovedLevels();
    }

    /// <summary>The paragraph embedding level: 0 left to right, 1 right to left.</summary>
    public byte ParagraphLevel { get; }

    /// <summary>Resolved level of each code point before line rules. Characters removed by X9 take their neighbor's level.</summary>
    public byte[] Levels => _levels;

    /// <summary>Characters X9 removes: embedding controls and boundary neutrals.</summary>
    public bool[] Removed => _removed;

    /// <summary>Runs the algorithm on one paragraph.</summary>
    /// <param name="codePoints">The paragraph's code points.</param>
    /// <param name="direction">0 left to right, 1 right to left, 2 from the first strong character (P2, P3).</param>
    public static Bidi Analyze(int[] codePoints, int direction) => new(codePoints, direction);

    /// <summary>
    /// Levels of one line after rule L1: separators and trailing whitespace go back to the
    /// paragraph level.
    /// </summary>
    public byte[] LineLevels(int start, int end)
    {
        var levels = new byte[end - start];
        Array.Copy(_levels, start, levels, 0, levels.Length);
        bool trailing = true;
        for (int i = end - 1; i >= start; i--)
        {
            BidiClass t = _original[i];
            if (t is B or S)
            {
                levels[i - start] = ParagraphLevel;
                trailing = true;
            }
            else if (t is WS or FSI or LRI or RLI or PDI || _removed[i])
            {
                if (trailing) levels[i - start] = ParagraphLevel;
            }
            else
            {
                trailing = false;
            }
        }

        return levels;
    }

    /// <summary>L2: the visual order of positions 0 to n-1, given their levels.</summary>
    public static int[] Reorder(byte[] levels)
    {
        int n = levels.Length;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        if (n == 0) return order;

        // Reverse every run at or above each level, from the highest down to the lowest odd one.
        byte[] current = (byte[])levels.Clone();
        byte highest = levels.Max();
        int lowestOdd = levels.Where(l => l % 2 == 1).Select(l => (int)l).DefaultIfEmpty(highest + 1).Min();
        for (int level = highest; level >= lowestOdd; level--)
        {
            for (int i = 0; i < n;)
            {
                if (current[i] < level)
                {
                    i++;
                    continue;
                }

                int j = i;
                while (j < n && current[j] >= level) j++;
                Array.Reverse(order, i, j - i);
                Array.Reverse(current, i, j - i);
                i = j;
            }
        }

        return order;
    }

    /// <summary>BD9: the PDI matching each isolate initiator, and the reverse; -1 when unmatched.</summary>
    private void MatchIsolates()
    {
        int n = _original.Length;
        for (int i = 0; i < n; i++)
        {
            _matchingPdi[i] = -1;
            _matchingInitiator[i] = -1;
        }

        var open = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            switch (_original[i])
            {
                case LRI or RLI or FSI:
                    open.Push(i);
                    break;
                case PDI when open.Count > 0:
                    int initiator = open.Pop();
                    _matchingPdi[initiator] = i;
                    _matchingInitiator[i] = initiator;
                    break;
                case B:
                    open.Clear();
                    break;
            }
        }
    }

    /// <summary>P2, P3: level of the first strong character outside isolates in [start, end), or null.</summary>
    private byte? FirstStrongLevel(int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            switch (_original[i])
            {
                case L:
                    return 0;
                case R or AL:
                    return 1;
                case LRI or RLI or FSI:
                    if (_matchingPdi[i] < 0) return null;
                    i = _matchingPdi[i];
                    break;
                case B:
                    return null;
            }
        }

        return null;
    }

    /// <summary>X1 to X9: explicit levels and directional overrides.</summary>
    private void Explicit()
    {
        var stack = new Stack<(byte Level, BidiClass? Override, bool Isolate)>();
        stack.Push((ParagraphLevel, null, false));
        int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;
        for (int i = 0; i < _original.Length; i++)
        {
            BidiClass t = _original[i];
            (byte level, BidiClass? overrideType, _) = stack.Peek();
            switch (t)
            {
                case RLE or LRE or RLO or LRO:
                {
                    _levels[i] = level;
                    byte next = t is RLE or RLO ? NextOdd(level) : NextEven(level);
                    if (next <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        stack.Push((next, t == RLO ? R : t == LRO ? L : null, false));
                    }
                    else if (overflowIsolates == 0)
                    {
                        overflowEmbeddings++;
                    }

                    break;
                }

                case RLI or LRI or FSI:
                {
                    _levels[i] = level;
                    if (overrideType is BidiClass o) _types[i] = o;
                    bool rtl = t == RLI || (t == FSI && FirstStrongLevel(i + 1, _matchingPdi[i] < 0 ? _original.Length : _matchingPdi[i]) == 1);
                    byte next = rtl ? NextOdd(level) : NextEven(level);
                    if (next <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        validIsolates++;
                        stack.Push((next, null, true));
                    }
                    else
                    {
                        overflowIsolates++;
                    }

                    break;
                }

                case PDI:
                    if (overflowIsolates > 0)
                    {
                        overflowIsolates--;
                    }
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while (!stack.Peek().Isolate) stack.Pop();
                        stack.Pop();
                        validIsolates--;
                    }

                    (level, overrideType, _) = stack.Peek();
                    _levels[i] = level;
                    if (overrideType is BidiClass po) _types[i] = po;
                    break;

                case PDF:
                    _levels[i] = level;
                    if (overflowIsolates > 0)
                    {
                    }
                    else if (overflowEmbeddings > 0)
                    {
                        overflowEmbeddings--;
                    }
                    else if (!stack.Peek().Isolate && stack.Count >= 2)
                    {
                        stack.Pop();
                    }

                    break;

                case B:
                    _levels[i] = ParagraphLevel;
                    break;

                case BN:
                    _levels[i] = level;
                    break;

                default:
                    _levels[i] = level;
                    if (overrideType is BidiClass ot) _types[i] = ot;
                    break;
            }

            _removed[i] = t is RLE or LRE or RLO or LRO or PDF or BN;
        }
    }

    /// <summary>X10 (BD13): level runs chained through matching isolate initiators and PDIs.</summary>
    private List<List<int>> IsolatingRunSequences()
    {
        var runs = new List<List<int>>();
        List<int>? current = null;
        int previousLevel = -1;
        for (int i = 0; i < _levels.Length; i++)
        {
            if (_removed[i]) continue;
            if (current is null || _levels[i] != previousLevel)
            {
                current = [];
                runs.Add(current);
                previousLevel = _levels[i];
            }

            current.Add(i);
        }

        var runOf = new Dictionary<int, List<int>>();
        foreach (List<int> run in runs) runOf[run[0]] = run;

        var sequences = new List<List<int>>();
        foreach (List<int> run in runs)
        {
            int first = run[0];
            if (_original[first] == PDI && _matchingInitiator[first] >= 0) continue; // continues an earlier sequence
            var sequence = new List<int>(run);
            List<int> last = run;
            while (_original[last[^1]] is LRI or RLI or FSI && _matchingPdi[last[^1]] is int pdi && pdi >= 0
                && runOf.TryGetValue(pdi, out List<int>? next))
            {
                sequence.AddRange(next);
                last = next;
            }

            sequences.Add(sequence);
        }

        return sequences;
    }

    /// <summary>W1 to I2 on one isolating run sequence.</summary>
    private void Resolve(List<int> seq)
    {
        int n = seq.Count;
        byte level = _levels[seq[0]];
        int before = PreviousKept(seq[0]);
        int after = _original[seq[^1]] is LRI or RLI or FSI ? -1 : NextKept(seq[^1]);
        byte beforeLevel = before < 0 ? ParagraphLevel : _explicitLevels[before];
        byte afterLevel = after < 0 ? ParagraphLevel : _explicitLevels[after];
        BidiClass sos = Math.Max(beforeLevel, level) % 2 == 1 ? R : L;
        BidiClass eos = Math.Max(afterLevel, level) % 2 == 1 ? R : L;
        BidiClass embedding = level % 2 == 1 ? R : L;

        var t = new BidiClass[n];
        for (int k = 0; k < n; k++) t[k] = _types[seq[k]];

        // W1
        for (int k = 0; k < n; k++)
        {
            if (t[k] != NSM) continue;
            t[k] = k == 0 ? sos : _original[seq[k - 1]] is LRI or RLI or FSI or PDI ? ON : t[k - 1];
        }

        // W2, W3
        BidiClass lastStrong = sos;
        for (int k = 0; k < n; k++)
        {
            if (t[k] is L or R or AL) lastStrong = t[k];
            else if (t[k] == EN && lastStrong == AL) t[k] = AN;
        }

        for (int k = 0; k < n; k++)
        {
            if (t[k] == AL) t[k] = R;
        }

        // W4
        for (int k = 1; k < n - 1; k++)
        {
            if (t[k] == ES && t[k - 1] == EN && t[k + 1] == EN) t[k] = EN;
            else if (t[k] == CS && t[k - 1] == EN && t[k + 1] == EN) t[k] = EN;
            else if (t[k] == CS && t[k - 1] == AN && t[k + 1] == AN) t[k] = AN;
        }

        // W5
        for (int k = 0; k < n; k++)
        {
            if (t[k] != ET) continue;
            int end = k;
            while (end < n && t[end] == ET) end++;
            bool nextToNumber = (k > 0 && t[k - 1] == EN) || (end < n && t[end] == EN);
            if (nextToNumber)
            {
                for (int j = k; j < end; j++) t[j] = EN;
            }

            k = end - 1;
        }

        // W6, W7
        for (int k = 0; k < n; k++)
        {
            if (t[k] is ES or ET or CS) t[k] = ON;
        }

        lastStrong = sos;
        for (int k = 0; k < n; k++)
        {
            if (t[k] is L or R) lastStrong = t[k];
            else if (t[k] == EN && lastStrong == L) t[k] = L;
        }

        ResolveBrackets(seq, t, sos, embedding);

        // N1, N2
        for (int k = 0; k < n; k++)
        {
            if (!Neutral(t[k])) continue;
            int end = k;
            while (end < n && Neutral(t[end])) end++;
            BidiClass leading = k == 0 ? sos : Strong(t[k - 1]);
            BidiClass trailing = end == n ? eos : Strong(t[end]);
            BidiClass resolved = leading == trailing ? leading : embedding;
            for (int j = k; j < end; j++) t[j] = resolved;
            k = end - 1;
        }

        // I1, I2
        for (int k = 0; k < n; k++)
        {
            byte l = level;
            if (l % 2 == 0)
            {
                if (t[k] == R) l += 1;
                else if (t[k] is AN or EN) l += 2;
            }
            else if (t[k] is L or EN or AN)
            {
                l += 1;
            }

            _levels[seq[k]] = l;
            _types[seq[k]] = t[k];
        }
    }

    /// <summary>BD16 and N0: paired brackets take the direction of what they enclose or surround.</summary>
    private void ResolveBrackets(List<int> seq, BidiClass[] t, BidiClass sos, BidiClass embedding)
    {
        var pairs = new List<(int Open, int Close)>();
        var stack = new List<(int Pair, int Position)>();
        for (int k = 0; k < seq.Count; k++)
        {
            if (t[k] != ON || !UnicodeData.Bracket(_codePoints[seq[k]], out int pair, out bool opening)) continue;
            int cp = Canonical(_codePoints[seq[k]]);
            if (opening)
            {
                if (stack.Count == MaxBracketPairs) break;
                stack.Add((Canonical(pair), k));
            }
            else
            {
                for (int s = stack.Count - 1; s >= 0; s--)
                {
                    if (stack[s].Pair != cp) continue;
                    pairs.Add((stack[s].Position, k));
                    stack.RemoveRange(s, stack.Count - s);
                    break;
                }
            }
        }

        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));
        BidiClass opposite = embedding == L ? R : L;
        foreach ((int open, int close) in pairs)
        {
            bool sameInside = false, oppositeInside = false;
            for (int k = open + 1; k < close; k++)
            {
                BidiClass s = StrongOrNone(t[k]);
                if (s == embedding) sameInside = true;
                else if (s == opposite) oppositeInside = true;
            }

            BidiClass? resolved = null;
            if (sameInside)
            {
                resolved = embedding;
            }
            else if (oppositeInside)
            {
                BidiClass context = sos;
                for (int k = open - 1; k >= 0; k--)
                {
                    if (StrongOrNone(t[k]) is BidiClass s and (L or R))
                    {
                        context = s;
                        break;
                    }
                }

                resolved = context == opposite ? opposite : embedding;
            }

            if (resolved is not BidiClass r) continue;
            t[open] = r;
            t[close] = r;
            foreach (int bracket in new[] { open, close })
            {
                for (int k = bracket + 1; k < seq.Count && _original[seq[k]] == NSM; k++) t[k] = r;
            }
        }
    }

    /// <summary>Strong direction for N0: L, or R for R, EN and AN; ON otherwise.</summary>
    private static BidiClass StrongOrNone(BidiClass t) => t switch
    {
        L => L,
        R or AL or EN or AN => R,
        _ => ON,
    };

    private static BidiClass Strong(BidiClass t) => t is EN or AN ? R : t;

    private static bool Neutral(BidiClass t) => t is B or S or WS or ON or LRI or RLI or FSI or PDI;

    /// <summary>The angle brackets U+2329 and U+232A are canonically equivalent to U+3008 and U+3009.</summary>
    private static int Canonical(int cp) => cp switch
    {
        0x2329 => 0x3008,
        0x232A => 0x3009,
        _ => cp,
    };

    private int PreviousKept(int i)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            if (!_removed[j]) return j;
        }

        return -1;
    }

    private int NextKept(int i)
    {
        for (int j = i + 1; j < _removed.Length; j++)
        {
            if (!_removed[j]) return j;
        }

        return -1;
    }

    /// <summary>Removed characters take the level of the character before them, or the paragraph level.</summary>
    private void AssignRemovedLevels()
    {
        for (int i = 0; i < _levels.Length; i++)
        {
            if (_removed[i]) _levels[i] = i > 0 ? _levels[i - 1] : ParagraphLevel;
        }
    }

    private static byte NextOdd(byte level) => (byte)(level % 2 == 0 ? level + 1 : level + 2);

    private static byte NextEven(byte level) => (byte)(level % 2 == 0 ? level + 2 : level + 1);
}
