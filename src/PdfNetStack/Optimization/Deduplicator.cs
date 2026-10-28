using System.Security.Cryptography;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Optimization;

/// <summary>
/// Merges indirect objects that are byte-for-byte identical once their references are
/// canonicalized. Runs to a fixpoint, because merging two fonts can make the resource
/// dictionaries that point at them identical too. Objects with identity semantics (pages,
/// annotations, structure elements, outline items and anything reached through such keys)
/// are never merged.
/// </summary>
internal static class Deduplicator
{
    internal static readonly HashSet<string> NeverMergeTypes = new(StringComparer.Ordinal)
    {
        "Catalog", "Pages", "Page", "Annot", "StructTreeRoot", "StructElem", "OBJR", "MCR",
        "Outlines", "Sig", "SigRef", "DocTimeStamp", "ObjStm", "XRef", "Action", "Filespec",
        "EmbeddedFile", "Template", "Thread", "Bead", "OCG", "OCMD", "Metadata",
    };

    // Keys that only make sense on an object with its own identity: annotations (Rect), form
    // fields (FT), tree nodes and structure tree participants.
    private static readonly string[] IdentityMarkers = ["Rect", "FT", "Parent", "Kids", "K", "P", "Pg", "StructParent", "StructParents", "First", "Next", "Prev"];

    /// <summary>True when a dictionary must never be merged with an equal twin.</summary>
    internal static bool HasIdentity(PdfDictionary dict)
    {
        if (dict.GetName("Type") is { } type && NeverMergeTypes.Contains(type)) return true;
        foreach (string key in IdentityMarkers)
        {
            if (dict.ContainsKey(key)) return true;
        }

        return false;
    }

    /// <returns>Representative object number for every number in the graph, and the merge count.</returns>
    public static (Dictionary<int, int> Representative, int Merged) Run(PdfFile file, ObjectGraph graph, CancellationToken ct)
    {
        var parent = new Dictionary<int, int>(graph.Order.Count);
        foreach (int n in graph.Order) parent[n] = n;

        var candidates = new List<int>();
        var payloadHash = new Dictionary<int, byte[]>();
        foreach (int number in graph.Order)
        {
            if (graph.IdentitySensitive.Contains(number)) continue;
            PdfObject obj = file.GetObject(number);
            PdfDictionary? dict = obj switch
            {
                PdfStream s => s.Dictionary,
                PdfDictionary d => d,
                _ => null,
            };

            if (dict is not null && HasIdentity(dict)) continue;
            if (obj is PdfStream st) payloadHash[number] = Compat.Sha256(st.Data.Span);
            candidates.Add(number);
        }

        int Find(int n)
        {
            while (parent.TryGetValue(n, out int p) && p != n)
            {
                int grand = parent.TryGetValue(p, out int g) ? g : p;
                parent[n] = grand;
                n = grand;
            }

            return n;
        }

        var serializer = new PdfSerializer(r => parent.ContainsKey(r.Number) ? Find(r.Number) : -1);
        var sink = new ByteSink(256);
        int merged = 0;

        for (int round = 0; round < 32; round++)
        {
            ct.ThrowIfCancellationRequested();
            var seen = new Dictionary<HashKey, int>(candidates.Count);
            int mergedThisRound = 0;
            var survivors = new List<int>(candidates.Count);
            foreach (int number in candidates)
            {
                if (Find(number) != number) continue;
                sink.Clear();
                PdfObject obj = file.GetObject(number);
                if (obj is PdfStream s)
                {
                    sink.Write((byte)'S');
                    sink.Write(payloadHash[number]);
                    serializer.WriteDictionary(sink, s.Dictionary, skipKey: "Length");
                }
                else
                {
                    sink.Write((byte)'O');
                    serializer.Write(sink, obj);
                }

                var key = new HashKey(Compat.Sha256(sink.WrittenSpan));
                if (seen.TryGetValue(key, out int existing))
                {
                    parent[number] = existing;
                    mergedThisRound++;
                }
                else
                {
                    seen[key] = number;
                    survivors.Add(number);
                }
            }

            merged += mergedThisRound;
            candidates = survivors;
            if (mergedThisRound == 0) break;
        }

        var representative = new Dictionary<int, int>(parent.Count);
        foreach (int n in graph.Order) representative[n] = Find(n);
        return (representative, merged);
    }

    private readonly struct HashKey : IEquatable<HashKey>
    {
        private readonly long _a, _b, _c, _d;

        public HashKey(byte[] sha)
        {
            _a = BitConverter.ToInt64(sha, 0);
            _b = BitConverter.ToInt64(sha, 8);
            _c = BitConverter.ToInt64(sha, 16);
            _d = BitConverter.ToInt64(sha, 24);
        }

        public bool Equals(HashKey other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;

        public override bool Equals(object? obj) => obj is HashKey k && Equals(k);

        public override int GetHashCode() => (int)_a ^ (int)(_a >> 32) ^ (int)_b;
    }
}
