using System.Runtime.InteropServices;

namespace PdfNetStack.Filters;

/// <summary>
/// A Deflate encoder (RFC 1951) that spends CPU on size, in the manner of Zopfli: for every
/// position it finds the nearest match of every length; the input is cut into blocks where the
/// symbol statistics change; each block is then parsed by shortest path over all match lengths,
/// with symbol costs taken from the previous parse and refined over several passes; each block
/// is written as whichever of dynamic, fixed and stored Huffman coding is smallest, with code
/// lengths limited by package-merge (Larmore and Hirschberg, 1990). Written from those
/// descriptions; the output is ordinary zlib data any inflater reads.
/// </summary>
/// <remarks>
/// Work is bounded: match search follows at most <see cref="MaxChain"/> candidates per position
/// and gives up past a byte comparison budget proportional to the input, and inputs over
/// <see cref="MaxInput"/> are refused. A null result means "use zlib".
/// </remarks>
internal static class ExhaustiveDeflate
{
    /// <summary>Largest input accepted.</summary>
    public const int MaxInput = 32 << 20;

    private const int WindowSize = 32768;
    private const int WindowMask = WindowSize - 1;
    private const int MinMatch = 3;
    private const int MaxMatch = 258;
    private const int MaxChain = 1024;
    private const int HashSize = 1 << 15;
    private const int MasterBlock = 1 << 20;
    private const int SplitChunk = 1024;
    private const int MaxBlocksPerMaster = 32;
    private const int EndOfBlock = 256;

    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    // Length 3..258 to its symbol offset (0..28), and distance 1..32768 to its symbol.
    private static readonly byte[] LengthSymbol = BuildLengthSymbols();
    private static readonly byte[] DistanceSymbolTable = BuildDistanceSymbols();

    /// <summary>Compresses into the zlib format, or returns null when the input is too large or the search over budget.</summary>
    /// <param name="data">Bytes to compress.</param>
    /// <param name="iterations">Refinement passes per block; 0 picks by size.</param>
    /// <param name="ct">Checked between passes and every few thousand positions.</param>
    public static byte[]? TryCompress(ReadOnlySpan<byte> data, int iterations = 0, CancellationToken ct = default)
    {
        if (data.Length > MaxInput) return null;
        if (iterations <= 0) iterations = data.Length <= 1 << 16 ? 15 : data.Length <= 1 << 20 ? 8 : 4;
        var writer = new BitWriter(Math.Max(64, data.Length / 2));
        writer.Byte(0x78);
        writer.Byte(0xDA);

        var finder = new MatchFinder(data, ct);
        if (data.Length == 0)
        {
            // One empty fixed block.
            writer.Bits(1, 1);
            writer.Bits(1, 2);
            writer.Bits(0, 7);
        }

        for (int start = 0; start < data.Length; start += MasterBlock)
        {
            int end = Math.Min(data.Length, start + MasterBlock);
            if (!finder.Collect(start, end)) return null;
            List<(int Start, int End)> blocks = Split(data, finder, start, end);
            for (int b = 0; b < blocks.Count; b++)
            {
                (int s, int e) = blocks[b];
                List<int> symbols = Optimize(data, finder, s, e, iterations, ct);
                WriteBlock(writer, data, s, e, symbols, last: end == data.Length && b == blocks.Count - 1);
            }
        }

        writer.Align();
        uint adler = Adler32(data);
        writer.Byte((byte)(adler >> 24));
        writer.Byte((byte)(adler >> 16));
        writer.Byte((byte)(adler >> 8));
        writer.Byte((byte)adler);
        return writer.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        int i = 0;
        while (i < data.Length)
        {
            // 5552 bytes keep the sums inside 32 bits before the modulo.
            int n = Math.Min(5552, data.Length - i);
            for (int k = 0; k < n; k++)
            {
                a += data[i + k];
                b += a;
            }

            a %= 65521;
            b %= 65521;
            i += n;
        }

        return (b << 16) | a;
    }

    // ---------------------------------------------------------------- symbols

    /// <summary>A parsed symbol: a literal byte, or a match packed as length and distance.</summary>
    private static int Literal(int value) => value;

    private static int Match(int length, int distance) => (length << 16) | distance;

    private static bool IsMatch(int symbol) => symbol >= 1 << 16;

    private static int MatchLength(int symbol) => symbol >> 16;

    private static int MatchDistance(int symbol) => symbol & 0xFFFF;

    private static int DistanceSymbol(int distance) => DistanceSymbolTable[distance];

    private static byte[] BuildLengthSymbols()
    {
        var table = new byte[MaxMatch + 1];
        for (int code = 0; code < LengthBase.Length; code++)
        {
            int top = code + 1 < LengthBase.Length ? LengthBase[code + 1] : MaxMatch + 1;
            for (int length = LengthBase[code]; length < top && length <= MaxMatch; length++) table[length] = (byte)code;
        }

        // 258 has its own code (285), not the last of 227..257.
        table[MaxMatch] = 28;
        return table;
    }

    private static byte[] BuildDistanceSymbols()
    {
        var table = new byte[WindowSize + 1];
        for (int code = 0; code < DistanceBase.Length; code++)
        {
            int top = code + 1 < DistanceBase.Length ? DistanceBase[code + 1] : WindowSize + 1;
            for (int d = DistanceBase[code]; d < top; d++) table[d] = (byte)code;
        }

        return table;
    }

    /// <summary>Literal/length and distance symbol counts, with the extra bits the matches carry.</summary>
    private sealed class Histogram
    {
        public long[] LiteralLength { get; } = new long[288];

        public long[] Distance { get; } = new long[30];

        public long ExtraBits { get; set; }

        public void Add(int symbol, int sign = 1)
        {
            if (!IsMatch(symbol))
            {
                LiteralLength[symbol] += sign;
                return;
            }

            int lengthCode = LengthSymbol[MatchLength(symbol)], distanceCode = DistanceSymbol(MatchDistance(symbol));
            LiteralLength[257 + lengthCode] += sign;
            Distance[distanceCode] += sign;
            ExtraBits += sign * (LengthExtra[lengthCode] + DistanceExtra[distanceCode]);
        }

        public void AddAll(Histogram other, int sign)
        {
            for (int i = 0; i < 288; i++) LiteralLength[i] += sign * other.LiteralLength[i];
            for (int i = 0; i < 30; i++) Distance[i] += sign * other.Distance[i];
            ExtraBits += sign * other.ExtraBits;
        }

        public static Histogram Of(List<int> symbols, int from, int to)
        {
            var h = new Histogram();
            for (int i = from; i < to; i++) h.Add(symbols[i]);
            h.LiteralLength[EndOfBlock] = 1;
            return h;
        }
    }

    // ---------------------------------------------------------------- matching

    /// <summary>
    /// For every position of a master block, the nearest distance for each match length, stored
    /// as break points: (length, distance) pairs where all lengths up to that length, beyond the
    /// previous break point, are reached at that distance. Distances only grow along the list.
    /// </summary>
    private sealed class MatchFinder
    {
        private readonly byte[] _data;
        private readonly int[] _head = new int[HashSize];
        private readonly int[] _previous = new int[WindowSize];
        private readonly long _budget;
        private readonly CancellationToken _ct;
        private int _inserted;
        private long _work;
        private int[] _offsets = [];
        private int[] _pairs = new int[1024];
        private int _pairCount;
        private int _base;

        public MatchFinder(ReadOnlySpan<byte> data, CancellationToken ct)
        {
            _data = data.ToArray();
            _ct = ct;
            _budget = (2000L * data.Length) + (1L << 22);
            _head.AsSpan().Fill(-1);
        }

        /// <summary>Finds the matches of positions start..end; false when over the work budget.</summary>
        public bool Collect(int start, int end)
        {
            _base = start;
            _offsets = new int[end - start + 1];
            _pairCount = 0;
            for (int position = start; position < end; position++)
            {
                if ((position & 0xFFF) == 0) _ct.ThrowIfCancellationRequested();
                _offsets[position - start] = _pairCount;
                Insert(position);
                Search(position);
                if (_work > _budget) return false;
            }

            _offsets[end - start] = _pairCount;
            return true;
        }

        /// <summary>The break points at <paramref name="position"/>, as packed length and distance.</summary>
        public ReadOnlySpan<int> At(int position)
        {
            int i = position - _base;
            return _pairs.AsSpan(_offsets[i], _offsets[i + 1] - _offsets[i]);
        }

        /// <summary>True when <paramref name="position"/> has a match of the full <see cref="MaxMatch"/> bytes.</summary>
        public bool Full(int position)
        {
            ReadOnlySpan<int> pairs = At(position);
            return pairs.Length > 0 && MatchLength(pairs[^1]) == MaxMatch;
        }

        /// <summary>The longest match at <paramref name="position"/>, limited to <paramref name="limit"/> bytes.</summary>
        public (int Length, int Distance) Longest(int position, int limit)
        {
            ReadOnlySpan<int> pairs = At(position);
            for (int k = pairs.Length - 1; k >= 0; k--)
            {
                int length = Math.Min(MatchLength(pairs[k]), limit);
                if (length >= MinMatch) return (length, MatchDistance(pairs[k]));
            }

            return (0, 0);
        }

        private int Hash(int position) =>
            ((_data[position] << 10) ^ (_data[position + 1] << 5) ^ _data[position + 2]) & (HashSize - 1);

        private void Insert(int position)
        {
            // Positions are hashed in order; the next three bytes must exist.
            for (; _inserted <= position; _inserted++)
            {
                if (_inserted + MinMatch > _data.Length) continue;
                int h = Hash(_inserted);
                _previous[_inserted & WindowMask] = _head[h];
                _head[h] = _inserted;
            }
        }

        private void Search(int position)
        {
            int available = Math.Min(MaxMatch, _data.Length - position);
            if (available < MinMatch) return;
            int best = MinMatch - 1;
            int candidate = _previous[position & WindowMask];
            for (int hits = 0; candidate >= 0 && hits < MaxChain; hits++)
            {
                int distance = position - candidate;
                if (distance <= 0 || distance > WindowSize) break;

                // Only a candidate that beats the best so far adds a break point.
                if (_data[candidate + best] == _data[position + best])
                {
                    int length = Common(candidate, position, available);
                    _work += length;
                    if (length > best)
                    {
                        Add(Match(length, distance));
                        best = length;
                        if (length == available) break;
                    }
                }

                _work++;
                int next = _previous[candidate & WindowMask];
                if (next >= candidate) break;
                candidate = next;
            }
        }

        /// <summary>Bytes the two positions have in common, up to <paramref name="limit"/>; eight at a time while they agree.</summary>
        private int Common(int a, int b, int limit)
        {
            ReadOnlySpan<byte> data = _data;
            int length = 0;
            while (length + 8 <= limit && MemoryMarshal.Read<ulong>(data.Slice(a + length)) == MemoryMarshal.Read<ulong>(data.Slice(b + length))) length += 8;
            while (length < limit && data[a + length] == data[b + length]) length++;
            return length;
        }

        private void Add(int pair)
        {
            if (_pairCount == _pairs.Length) Array.Resize(ref _pairs, _pairs.Length * 2);
            _pairs[_pairCount++] = pair;
        }
    }

    // ---------------------------------------------------------------- block splitting

    /// <summary>
    /// Cuts a master block where statistics change. A lazy greedy parse gives the symbols; split
    /// points on a coarse grid are chosen recursively wherever two dynamic blocks cost less than one.
    /// </summary>
    private static List<(int Start, int End)> Split(ReadOnlySpan<byte> data, MatchFinder finder, int start, int end)
    {
        var symbols = new List<int>();
        var positions = new List<int>();
        for (int i = start; i < end;)
        {
            (int length, int distance) = finder.Longest(i, end - i);
            if (length >= MinMatch && i + 1 < end && finder.Longest(i + 1, end - i - 1).Length > length) length = 0;
            positions.Add(i);
            if (length >= MinMatch)
            {
                symbols.Add(Match(length, distance));
                i += length;
            }
            else
            {
                symbols.Add(Literal(data[i]));
                i++;
            }
        }

        // Histograms per chunk of symbols; a range's histogram is a sum of chunks.
        int chunks = (symbols.Count + SplitChunk - 1) / SplitChunk;
        var chunk = new Histogram[chunks];
        for (int c = 0; c < chunks; c++) chunk[c] = Histogram.Of(symbols, c * SplitChunk, Math.Min(symbols.Count, (c + 1) * SplitChunk));

        var cuts = new List<int> { 0, chunks };
        var pending = new Stack<(int From, int To)>();
        pending.Push((0, chunks));
        while (pending.Count > 0 && cuts.Count - 1 < MaxBlocksPerMaster)
        {
            (int from, int to) = pending.Pop();
            if (to - from < 2) continue;
            var total = new Histogram();
            for (int c = from; c < to; c++) total.AddAll(chunk[c], 1);
            total.LiteralLength[EndOfBlock] = 1;
            double whole = DynamicBits(total);

            var left = new Histogram();
            double bestCost = whole;
            int bestCut = -1;
            for (int cut = from + 1; cut < to; cut++)
            {
                left.AddAll(chunk[cut - 1], 1);
                var right = new Histogram();
                right.AddAll(total, 1);
                right.AddAll(left, -1);
                left.LiteralLength[EndOfBlock] = 1;
                right.LiteralLength[EndOfBlock] = 1;
                double cost = DynamicBits(left) + DynamicBits(right);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestCut = cut;
                }
            }

            // A split must pay for the extra block header with room to spare.
            if (bestCut < 0 || whole - bestCost < 256) continue;
            cuts.Add(bestCut);
            pending.Push((from, bestCut));
            pending.Push((bestCut, to));
        }

        cuts.Sort();
        var blocks = new List<(int, int)>();
        for (int k = 0; k + 1 < cuts.Count; k++)
        {
            int s = positions[cuts[k] * SplitChunk];
            int e = cuts[k + 1] * SplitChunk >= symbols.Count ? end : positions[cuts[k + 1] * SplitChunk];
            blocks.Add((s, e));
        }

        return blocks;
    }

    // ---------------------------------------------------------------- optimal parsing

    /// <summary>
    /// Shortest path parsing of one block, repeated with costs from the previous parse; the
    /// cheapest parse by exact block size wins. When a pass stops improving, the statistics are
    /// perturbed (deterministically) to leave the local minimum.
    /// </summary>
    private static List<int> Optimize(ReadOnlySpan<byte> data, MatchFinder finder, int start, int end, int iterations, CancellationToken ct)
    {
        int n = end - start;
        var cost = new double[n + 1];
        var length = new int[n + 1];
        var distance = new int[n + 1];

        // First costs: fixed Huffman code lengths, which need no statistics.
        var literalCost = new double[288];
        var distanceCost = new double[30];
        for (int s = 0; s < 288; s++) literalCost[s] = s < 144 ? 8 : s < 256 ? 9 : s < 280 ? 7 : 8;
        for (int s = 0; s < 30; s++) distanceCost[s] = 5;

        List<int>? best = null;
        double bestBits = double.MaxValue, lastBits = double.MaxValue;
        uint random = 1;
        for (int pass = 0; pass < iterations; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var lengthCost = new double[MaxMatch + 1];
            for (int l = MinMatch; l <= MaxMatch; l++) lengthCost[l] = literalCost[257 + LengthSymbol[l]] + LengthExtra[LengthSymbol[l]];
            var symbolDistanceCost = new double[30];
            for (int s = 0; s < 30; s++) symbolDistanceCost[s] = distanceCost[s] + DistanceExtra[s];

            cost.AsSpan().Fill(double.MaxValue);
            cost[0] = 0;
            for (int i = 0; i < n; i++)
            {
                double here = cost[i];
                int position = start + i;
                double viaLiteral = here + literalCost[data[position]];
                if (viaLiteral < cost[i + 1])
                {
                    cost[i + 1] = viaLiteral;
                    length[i + 1] = 1;
                }

                int previous = MinMatch - 1, room = n - i;
                ReadOnlySpan<int> pairs = finder.At(position);

                // Inside a long repetition every position has a full length match. Past its first
                // position only the full length is tried: a shorter match ends inside the
                // repetition, where a full length match from an earlier position can end too.
                // Without this a run costs MaxMatch updates per byte.
                if (room >= MaxMatch && i > 0 && finder.Full(position) && finder.Full(position - 1))
                {
                    int full = pairs[^1];
                    double c = here + symbolDistanceCost[DistanceSymbol(MatchDistance(full))] + lengthCost[MaxMatch];
                    if (c < cost[i + MaxMatch])
                    {
                        cost[i + MaxMatch] = c;
                        length[i + MaxMatch] = MaxMatch;
                        distance[i + MaxMatch] = MatchDistance(full);
                    }

                    continue;
                }

                foreach (int pair in pairs)
                {
                    int top = Math.Min(MatchLength(pair), room);
                    int d = MatchDistance(pair);
                    double baseCost = here + symbolDistanceCost[DistanceSymbol(d)];
                    for (int l = previous + 1; l <= top; l++)
                    {
                        double c = baseCost + lengthCost[l];
                        if (c < cost[i + l])
                        {
                            cost[i + l] = c;
                            length[i + l] = l;
                            distance[i + l] = d;
                        }
                    }

                    previous = Math.Max(previous, top);
                    if (previous >= room) break;
                }
            }

            var symbols = new List<int>();
            for (int i = n; i > 0; i -= length[i]) symbols.Add(length[i] == 1 ? Literal(data[start + i - 1]) : Match(length[i], distance[i]));
            symbols.Reverse();

            Histogram histogram = Histogram.Of(symbols, 0, symbols.Count);
            double bits = DynamicBits(histogram);
            if (bits < bestBits)
            {
                bestBits = bits;
                best = symbols;
            }

            // Entropy costs from this parse (Shannon, as Zopfli's cost model).
            if (bits >= lastBits) Perturb(histogram, ref random);
            lastBits = bits;
            EntropyCosts(histogram.LiteralLength, literalCost);
            EntropyCosts(histogram.Distance, distanceCost);
        }

        return best!;
    }

    private static void EntropyCosts(long[] counts, double[] costs)
    {
        long total = 0;
        foreach (long c in counts) total += c;
        double log2Total = total > 0 ? Math.Log(total, 2) : 0;
        for (int s = 0; s < counts.Length; s++) costs[s] = counts[s] > 0 ? log2Total - Math.Log(counts[s], 2) : log2Total + 1;
    }

    /// <summary>
    /// Replaces about a quarter of the counts with other counts picked at random, as Zopfli does
    /// to leave a local minimum. The xorshift sequence is fixed, so output is repeatable.
    /// </summary>
    private static void Perturb(Histogram histogram, ref uint state)
    {
        static uint Next(ref uint s)
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return s;
        }

        for (int i = 0; i < 288; i++)
        {
            if ((Next(ref state) & 3) == 0) histogram.LiteralLength[i] = histogram.LiteralLength[(int)(Next(ref state) % 288)];
        }

        for (int i = 0; i < 30; i++)
        {
            if ((Next(ref state) & 3) == 0) histogram.Distance[i] = histogram.Distance[(int)(Next(ref state) % 30)];
        }

        histogram.LiteralLength[EndOfBlock] = 1;
    }

    // ---------------------------------------------------------------- Huffman codes

    /// <summary>
    /// Code lengths of at most <paramref name="limit"/> bits minimizing the weighted length, by
    /// package-merge. Every used symbol gets a length; a tree is never left with fewer than two
    /// codes, so every inflater accepts it.
    /// </summary>
    internal static byte[] CodeLengths(long[] frequencies, int limit)
    {
        var lengths = new byte[frequencies.Length];
        var leaves = new List<(long Weight, int Symbol)>();
        for (int s = 0; s < frequencies.Length; s++)
        {
            if (frequencies[s] > 0) leaves.Add((frequencies[s], s));
        }

        // Pad to two symbols with the lowest unused ones.
        for (int s = 0; leaves.Count < 2 && s < frequencies.Length; s++)
        {
            if (frequencies[s] <= 0) leaves.Add((1, s));
        }

        leaves.Sort((x, y) => x.Weight != y.Weight ? x.Weight.CompareTo(y.Weight) : x.Symbol.CompareTo(y.Symbol));
        int n = leaves.Count;
        if (n == 2)
        {
            lengths[leaves[0].Symbol] = 1;
            lengths[leaves[1].Symbol] = 1;
            return lengths;
        }

        // Each list is the leaves merged with packages of pairs from the list before; symbol -1 marks a package.
        var lists = new List<(long Weight, int Symbol)>[limit];
        lists[0] = leaves;
        for (int k = 1; k < limit; k++)
        {
            List<(long Weight, int Symbol)> previous = lists[k - 1];
            var merged = new List<(long, int)>(2 * n);
            int leaf = 0, pair = 0;
            int packages = previous.Count / 2;
            while (leaf < n || pair < packages)
            {
                long packageWeight = pair < packages ? previous[2 * pair].Weight + previous[(2 * pair) + 1].Weight : long.MaxValue;
                if (leaf < n && leaves[leaf].Weight <= packageWeight) merged.Add(leaves[leaf++]);
                else merged.Add((packageWeight, -1 - pair++));
            }

            lists[k] = merged;
        }

        // The first 2n - 2 items of the last list; each package selected expands to two items of the list before.
        int take = (2 * n) - 2;
        for (int k = limit - 1; k >= 0 && take > 0; k--)
        {
            int packages = 0;
            for (int i = 0; i < take; i++)
            {
                int symbol = lists[k][i].Symbol;
                if (symbol >= 0) lengths[symbol]++;
                else packages++;
            }

            take = 2 * packages;
        }

        return lengths;
    }

    /// <summary>Canonical codes for the lengths (RFC 1951, 3.2.2), bit reversed for LSB first output.</summary>
    private static ushort[] Codes(byte[] lengths)
    {
        var count = new int[16];
        foreach (byte l in lengths) count[l]++;
        count[0] = 0;
        var next = new int[16];
        int code = 0;
        for (int bits = 1; bits < 16; bits++)
        {
            code = (code + count[bits - 1]) << 1;
            next[bits] = code;
        }

        var codes = new ushort[lengths.Length];
        for (int s = 0; s < lengths.Length; s++)
        {
            int l = lengths[s];
            if (l == 0) continue;
            int c = next[l]++, reversed = 0;
            for (int b = 0; b < l; b++) reversed |= ((c >> b) & 1) << (l - 1 - b);
            codes[s] = (ushort)reversed;
        }

        return codes;
    }

    /// <summary>Exact size in bits of a dynamic block with these statistics, header included.</summary>
    private static double DynamicBits(Histogram h)
    {
        byte[] literal = CodeLengths(h.LiteralLength, 15), distance = CodeLengths(h.Distance, 15);
        long bits = 3 + Header(literal, distance).Bits + h.ExtraBits;
        for (int s = 0; s < 288; s++) bits += h.LiteralLength[s] * literal[s];
        for (int s = 0; s < 30; s++) bits += h.Distance[s] * distance[s];
        return bits;
    }

    /// <summary>The dynamic block header: counts, code length code and run length coded lengths (RFC 1951, 3.2.7).</summary>
    private sealed record DynamicHeader(int Literals, int Distances, int CodeLengthCount, byte[] CodeLengthLengths, List<(int Symbol, int Extra)> Runs, long Bits);

    private static DynamicHeader Header(byte[] literal, byte[] distance)
    {
        int hlit = 286;
        while (hlit > 257 && literal[hlit - 1] == 0) hlit--;
        int hdist = 30;
        while (hdist > 1 && distance[hdist - 1] == 0) hdist--;

        var all = new byte[hlit + hdist];
        Array.Copy(literal, all, hlit);
        Array.Copy(distance, 0, all, hlit, hdist);

        // Runs: 16 repeats the previous length 3 to 6 times, 17 and 18 code 3 to 10 and 11 to 138 zeros.
        var runs = new List<(int Symbol, int Extra)>();
        for (int i = 0; i < all.Length;)
        {
            int value = all[i], run = 1;
            while (i + run < all.Length && all[i + run] == value) run++;
            i += run;
            if (value == 0)
            {
                while (run >= 11)
                {
                    int r = Math.Min(run, 138);
                    runs.Add((18, r - 11));
                    run -= r;
                }

                if (run >= 3)
                {
                    runs.Add((17, run - 3));
                    run = 0;
                }
            }
            else
            {
                runs.Add((value, 0));
                run--;
                while (run >= 3)
                {
                    int r = Math.Min(run, 6);
                    runs.Add((16, r - 3));
                    run -= r;
                }
            }

            for (; run > 0; run--) runs.Add((value, 0));
        }

        var frequencies = new long[19];
        foreach ((int symbol, _) in runs) frequencies[symbol]++;
        byte[] codeLengthLengths = CodeLengths(frequencies, 7);
        int hclen = 19;
        while (hclen > 4 && codeLengthLengths[CodeLengthOrder[hclen - 1]] == 0) hclen--;

        long bits = 5 + 5 + 4 + (3 * hclen);
        foreach ((int symbol, _) in runs) bits += codeLengthLengths[symbol] + (symbol == 16 ? 2 : symbol == 17 ? 3 : symbol == 18 ? 7 : 0);
        return new DynamicHeader(hlit, hdist, hclen, codeLengthLengths, runs, bits);
    }

    // ---------------------------------------------------------------- output

    private static void WriteBlock(BitWriter writer, ReadOnlySpan<byte> data, int start, int end, List<int> symbols, bool last)
    {
        Histogram h = Histogram.Of(symbols, 0, symbols.Count);
        byte[] literal = CodeLengths(h.LiteralLength, 15), distance = CodeLengths(h.Distance, 15);
        DynamicHeader header = Header(literal, distance);
        long dynamicBits = 3 + header.Bits + h.ExtraBits;
        long fixedBits = 3 + h.ExtraBits;
        for (int s = 0; s < 288; s++)
        {
            dynamicBits += h.LiteralLength[s] * literal[s];
            fixedBits += h.LiteralLength[s] * (s < 144 ? 8 : s < 256 ? 9 : s < 280 ? 7 : 8);
        }

        for (int s = 0; s < 30; s++)
        {
            dynamicBits += h.Distance[s] * distance[s];
            fixedBits += h.Distance[s] * 5;
        }

        int length = end - start;
        long storedBits = (((length + 65534) / 65535) * (3 + 7 + 32)) + (8L * length);
        if (storedBits < dynamicBits && storedBits < fixedBits)
        {
            WriteStored(writer, data.Slice(start, length), last);
            return;
        }

        if (fixedBits <= dynamicBits)
        {
            writer.Bits(last ? 1 : 0, 1);
            writer.Bits(1, 2);
            WriteSymbols(writer, symbols, FixedLiteralLengths, FixedDistanceLengths);
            return;
        }

        writer.Bits(last ? 1 : 0, 1);
        writer.Bits(2, 2);
        writer.Bits(header.Literals - 257, 5);
        writer.Bits(header.Distances - 1, 5);
        writer.Bits(header.CodeLengthCount - 4, 4);
        for (int i = 0; i < header.CodeLengthCount; i++) writer.Bits(header.CodeLengthLengths[CodeLengthOrder[i]], 3);
        ushort[] codeLengthCodes = Codes(header.CodeLengthLengths);
        foreach ((int symbol, int extra) in header.Runs)
        {
            writer.Bits(codeLengthCodes[symbol], header.CodeLengthLengths[symbol]);
            if (symbol == 16) writer.Bits(extra, 2);
            else if (symbol == 17) writer.Bits(extra, 3);
            else if (symbol == 18) writer.Bits(extra, 7);
        }

        WriteSymbols(writer, symbols, literal, distance);
    }

    private static readonly byte[] FixedLiteralLengths = Enumerable.Range(0, 288).Select(s => (byte)(s < 144 ? 8 : s < 256 ? 9 : s < 280 ? 7 : 8)).ToArray();
    private static readonly byte[] FixedDistanceLengths = Enumerable.Repeat((byte)5, 30).ToArray();

    private static void WriteSymbols(BitWriter writer, List<int> symbols, byte[] literalLengths, byte[] distanceLengths)
    {
        ushort[] literal = Codes(literalLengths), distance = Codes(distanceLengths);
        foreach (int symbol in symbols)
        {
            if (!IsMatch(symbol))
            {
                writer.Bits(literal[symbol], literalLengths[symbol]);
                continue;
            }

            int l = MatchLength(symbol), d = MatchDistance(symbol);
            int lc = LengthSymbol[l], dc = DistanceSymbol(d);
            writer.Bits(literal[257 + lc], literalLengths[257 + lc]);
            if (LengthExtra[lc] > 0) writer.Bits(l - LengthBase[lc], LengthExtra[lc]);
            writer.Bits(distance[dc], distanceLengths[dc]);
            if (DistanceExtra[dc] > 0) writer.Bits(d - DistanceBase[dc], DistanceExtra[dc]);
        }

        writer.Bits(literal[EndOfBlock], literalLengths[EndOfBlock]);
    }

    private static void WriteStored(BitWriter writer, ReadOnlySpan<byte> data, bool last)
    {
        int offset = 0;
        do
        {
            int length = Math.Min(65535, data.Length - offset);
            bool final = last && offset + length == data.Length;
            writer.Bits(final ? 1 : 0, 1);
            writer.Bits(0, 2);
            writer.Align();
            writer.Byte((byte)length);
            writer.Byte((byte)(length >> 8));
            writer.Byte((byte)~length);
            writer.Byte((byte)(~length >> 8));
            for (int i = 0; i < length; i++) writer.Byte(data[offset + i]);
            offset += length;
        }
        while (offset < data.Length);
    }

    /// <summary>Bits least significant first, as Deflate packs them.</summary>
    private sealed class BitWriter(int capacity)
    {
        private byte[] _buffer = new byte[capacity];
        private int _length;
        private ulong _accumulator;
        private int _count;

        public void Bits(int value, int count)
        {
            _accumulator |= (ulong)(uint)value << _count;
            _count += count;
            while (_count >= 8)
            {
                Put((byte)_accumulator);
                _accumulator >>= 8;
                _count -= 8;
            }
        }

        public void Align()
        {
            if (_count > 0) Bits(0, 8 - _count);
        }

        public void Byte(byte value)
        {
            Align();
            Put(value);
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        private void Put(byte value)
        {
            if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            _buffer[_length++] = value;
        }
    }
}
