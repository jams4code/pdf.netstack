using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// Proves that two PDFs describe the same document: walks both object graphs in lockstep from
/// the trailer and compares every reachable value. Streams are compared by decoded content, so
/// a different compression of the same bytes is equal, while any change to page content, fonts,
/// images, annotations, metadata or structure is reported with its path. JPEG data is compared
/// by DCT coefficients, so a re-encoding of the same image is equal too.
/// </summary>
internal sealed class EquivalenceVerifier
{
    private static readonly HashSet<string> EncodingKeys = new(StringComparer.Ordinal) { "Length", "Filter", "DecodeParms", "DL" };

    private readonly PdfFile _a;
    private readonly PdfFile _b;
    private readonly HashSet<string> _ignoredSourceKeys;
    private readonly long _decodeLimit;
    private readonly bool _allowJpegRequantization;
    private readonly HashSet<(int, int)> _visited = new();
    private readonly Dictionary<int, int> _aToB = new();
    private readonly Dictionary<int, int> _bToA = new();
    private readonly Queue<(int A, int B, string Path)> _pending = new();
    private readonly List<(PdfStream A, PdfStream B, string Path, List<FilterSpec>? FiltersA, List<FilterSpec>? FiltersB)> _streams = new();

    private EquivalenceVerifier(PdfFile a, PdfFile b, IEnumerable<string> ignoredSourceKeys, long decodeLimit, bool allowJpegRequantization)
    {
        _a = a;
        _b = b;
        _ignoredSourceKeys = new HashSet<string>(ignoredSourceKeys, StringComparer.Ordinal);
        _decodeLimit = decodeLimit;
        _allowJpegRequantization = allowJpegRequantization;
    }

    public int ObjectsCompared { get; private set; }

    public int StreamsCompared { get; private set; }

    /// <param name="ignoredSourceKeys">Keys allowed to exist only in the source, for opt-in stripping.</param>
    /// <param name="allowJpegRequantization">
    /// Accept JPEG images whose coefficients are exactly the source's requantized to coarser tables.
    /// Everything else about them must still match.
    /// </param>
    /// <returns>Null when equivalent, otherwise a description of the first difference found.</returns>
    public static string? Verify(PdfFile source, PdfFile output, IEnumerable<string> ignoredSourceKeys, long decodeLimit, bool allowJpegRequantization, int parallelism, CancellationToken ct, out int objectsCompared)
    {
        var v = new EquivalenceVerifier(source, output, ignoredSourceKeys, decodeLimit, allowJpegRequantization);
        string? difference = v.Run(parallelism, ct);
        objectsCompared = v.ObjectsCompared;
        return difference;
    }

    private string? Run(int parallelism, CancellationToken ct)
    {
        foreach (string key in new[] { "Root", "Info", "ID" })
        {
            string? d = Compare(_a.Trailer[key], _b.Trailer[key], "trailer/" + key, 0);
            if (d is not null) return d;
        }

        int n = 0;
        while (_pending.Count > 0)
        {
            if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
            (int a, int b, string path) = _pending.Dequeue();
            ObjectsCompared++;
            string? d = CompareResolved(_a.GetObject(a), _b.GetObject(b), path, 0);
            if (d is not null) return d;
        }

        // Filter parameters were resolved during the walk; stream content is compared in parallel.
        var jobs = _streams;
        string? failure = null;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, (job, state) =>
        {
            string? d = CompareStreamData(job.A, job.B, job.FiltersA, job.FiltersB, job.Path);
            if (d is not null)
            {
                Interlocked.CompareExchange(ref failure, d, null);
                state.Stop();
            }
        });

        StreamsCompared = jobs.Count;
        return failure;
    }

    private string? Compare(PdfObject? a, PdfObject? b, string path, int depth)
    {
        if (depth > PdfParser.MaxNesting) return path + ": nesting too deep to verify";

        if (a is PdfReference ra && b is PdfReference rb)
        {
            bool aExists = _a.Exists(ra.Number) && _a.GetObject(ra.Number) is not PdfNull;
            bool bExists = _b.Exists(rb.Number) && _b.GetObject(rb.Number) is not PdfNull;
            if (!aExists || !bExists)
            {
                return aExists == bExists ? null : path + ": reference resolves differently";
            }

            // Object identity: one source object must always land on the same output object, and
            // two source objects may share an output object only when merging them is harmless.
            if (_aToB.TryGetValue(ra.Number, out int mappedB) && mappedB != rb.Number)
            {
                return path + ": one source object maps to two output objects";
            }

            if (_bToA.TryGetValue(rb.Number, out int mappedA) && mappedA != ra.Number && !Mergeable(_b.GetObject(rb.Number)))
            {
                return path + ": two distinct source objects were merged into one";
            }

            _aToB[ra.Number] = rb.Number;
            _bToA.TryAdd(rb.Number, ra.Number);
            if (_visited.Add((ra.Number, rb.Number))) _pending.Enqueue((ra.Number, rb.Number, path));
            return null;
        }

        return CompareResolved(_a.Resolve(a), _b.Resolve(b), path, depth);
    }

    private static bool Mergeable(PdfObject obj) => obj switch
    {
        PdfStream s => !Deduplicator.HasIdentity(s.Dictionary),
        PdfDictionary d => !Deduplicator.HasIdentity(d),
        _ => true,
    };

    private string? CompareResolved(PdfObject? a, PdfObject? b, string path, int depth)
    {
        a ??= PdfNull.Instance;
        b ??= PdfNull.Instance;

        switch (a)
        {
            case PdfNull:
                return b is PdfNull ? null : path + ": null vs value";
            case PdfBoolean ab:
                return b is PdfBoolean bb && ab.Value == bb.Value ? null : path + ": boolean differs";
            case PdfNumber an:
                return b is PdfNumber bn && an.Text.AsSpan().SequenceEqual(bn.Text) ? null : path + ": number differs";
            case PdfName aName:
                return b is PdfName bName && aName.Value == bName.Value ? null : path + ": name differs";
            case PdfString aStr:
                return b is PdfString bStr && aStr.Value.AsSpan().SequenceEqual(bStr.Value) ? null : path + ": string differs";
            case PdfArray aArr:
            {
                if (b is not PdfArray bArr) return path + ": array vs other";
                if (aArr.Count != bArr.Count) return path + $": array length {aArr.Count} vs {bArr.Count}";
                for (int i = 0; i < aArr.Count; i++)
                {
                    string? d = Compare(aArr[i], bArr[i], path + "[" + i + "]", depth + 1);
                    if (d is not null) return d;
                }

                return null;
            }

            case PdfDictionary aDict:
                return b is PdfDictionary bDict ? CompareDictionaries(aDict, bDict, path, depth, isStream: false) : path + ": dictionary vs other";
            case PdfStream aStream:
            {
                if (b is not PdfStream bStream) return path + ": stream vs other";
                string? d = CompareDictionaries(aStream.Dictionary, bStream.Dictionary, path, depth, isStream: true);
                if (d is not null) return d;

                // Parameters of filters that stay encoded (JBIG2Globals, DCT settings) are part of
                // the content, and may reference other objects, so they go through the full walk.
                List<FilterSpec>? fa = SafeFilters(_a, aStream);
                List<FilterSpec>? fb = SafeFilters(_b, bStream);
                if (fa is not null && fb is not null)
                {
                    var ka = fa.SkipWhile(f => StreamCodec.IsDecodable(f.Name)).ToList();
                    var kb = fb.SkipWhile(f => StreamCodec.IsDecodable(f.Name)).ToList();
                    if (ka.Count != kb.Count) return path + ": encoded filter chain differs";
                    for (int i = 0; i < ka.Count; i++)
                    {
                        if (ka[i].Name != kb[i].Name) return path + ": encoded filter differs";
                        d = Compare(ka[i].Parms ?? PdfNull.Instance, kb[i].Parms ?? PdfNull.Instance, path + "/DecodeParms", depth + 1);
                        if (d is not null) return d;
                    }
                }

                _streams.Add((aStream, bStream, path, fa, fb));
                return null;
            }

            default:
                return path + ": unexpected object";
        }
    }

    private string? CompareDictionaries(PdfDictionary a, PdfDictionary b, string path, int depth, bool isStream)
    {
        foreach (string key in a.Keys)
        {
            if (isStream && EncodingKeys.Contains(key)) continue;
            if (_ignoredSourceKeys.Contains(key) && b[key] is null) continue;
            string? d = Compare(a[key], b[key] ?? PdfNull.Instance, path + "/" + key, depth + 1);
            if (d is not null) return d;
        }

        foreach (string key in b.Keys)
        {
            if (isStream && EncodingKeys.Contains(key)) continue;
            if (a.ContainsKey(key)) continue;
            if (_b.Resolve(b[key]) is not PdfNull) return path + "/" + key + ": key only in output";
        }

        return null;
    }

    private static List<FilterSpec>? SafeFilters(PdfFile file, PdfStream s)
    {
        try
        {
            List<FilterSpec> filters = StreamCodec.GetFilters(s.Dictionary, file.Resolve);
            for (int i = 0; i < filters.Count; i++)
            {
                PdfObject? p = file.Resolve(filters[i].Parms);
                if (p is PdfDictionary pd)
                {
                    var copy = new PdfDictionary(pd.Count);
                    for (int k = 0; k < pd.Count; k++)
                    {
                        (string key, PdfObject value) = pd.EntryAt(k);
                        copy.Set(key, value is PdfReference && file.Resolve(value) is PdfNumber num ? num : value);
                    }

                    p = copy;
                }

                filters[i] = filters[i] with { Parms = p };
            }

            return filters;
        }
        catch (StreamDecodeException)
        {
            return null;
        }
    }

    private string? CompareStreamData(PdfStream a, PdfStream b, List<FilterSpec>? fa, List<FilterSpec>? fb, string path)
    {
        using DecodedStream? da = TryDecode(a, fa);
        using DecodedStream? db = TryDecode(b, fb);
        if (da is null || db is null)
        {
            // Undecodable data must have been carried over untouched, encoding included.
            if (!a.Data.Span.SequenceEqual(b.Data.Span)) return path + ": undecodable stream data changed";
            return SameFilterChain(fa, fb) ? null : path + ": filter chain changed on undecodable stream";
        }

        if (da.PendingPredictor != db.PendingPredictor) return path + ": predictor differs";
        if (!SameFilterChain(da.Remaining, db.Remaining)) return path + ": remaining filters differ";
        if (da.Data.Span.SequenceEqual(db.Data.Span)) return null;

        // Re-encoded JPEG data differs in bytes; equality is decided on its coefficients.
        if (da.PendingPredictor is null && da.Remaining is [{ Name: "DCTDecode" }])
        {
            string? difference = JpegEquivalence.FindDifference(da.Data.Span, db.Data.Span, _allowJpegRequantization, _decodeLimit);
            return difference is null ? null : path + ": " + difference;
        }

        return path + ": decoded stream content differs";
    }

    private DecodedStream? TryDecode(PdfStream s, List<FilterSpec>? filters)
    {
        if (filters is null) return null;
        try
        {
            return StreamCodec.Decode(s.Data, filters, DecodeDepth.Full, _decodeLimit, static o => o);
        }
        catch (StreamDecodeException)
        {
            return null;
        }
    }

    private static bool SameFilterChain(List<FilterSpec>? a, List<FilterSpec>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Name != b[i].Name) return false;
            if (!ShallowEqual(a[i].Parms, b[i].Parms)) return false;
        }

        return true;
    }

    // Referenced parameter objects were already compared by the graph walk; this check only
    // guards the decoded comparison against a filter chain that changed shape.
    private static bool ShallowEqual(PdfObject? a, PdfObject? b)
    {
        if (a is null or PdfNull) return b is null or PdfNull;
        if (b is null or PdfNull) return false;
        return (a, b) switch
        {
            (PdfNumber x, PdfNumber y) => x.Text.AsSpan().SequenceEqual(y.Text),
            (PdfName x, PdfName y) => x.Value == y.Value,
            (PdfBoolean x, PdfBoolean y) => x.Value == y.Value,
            (PdfString x, PdfString y) => x.Value.AsSpan().SequenceEqual(y.Value),
            (PdfReference, PdfReference) => true,
            (PdfArray x, PdfArray y) => x.Count == y.Count && x.Items.Zip(y.Items, ShallowEqual).All(equal => equal),
            (PdfDictionary x, PdfDictionary y) => x.Count == y.Count && x.Keys.All(k => ShallowEqual(x[k], y[k])),
            _ => false,
        };
    }
}
