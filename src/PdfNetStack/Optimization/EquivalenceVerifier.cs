using PdfNetStack.Content;
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
/// by DCT coefficients, so a re-encoding of the same image is equal too. Lossy image changes are
/// accepted only where <see cref="VerificationRules.Images"/> allows them. The lossless structural
/// passes are accepted when <see cref="VerificationRules"/> allows them, each checked against an
/// analysis of the source: resource entries no content can name may be missing, content streams
/// may differ in layout but not in tokens, and a TrueType program may change where no glyph its
/// fonts show is affected.
/// </summary>
internal sealed class EquivalenceVerifier
{
    private static readonly HashSet<string> EncodingKeys = new(StringComparer.Ordinal) { "Length", "Filter", "DecodeParms", "DL" };

    private readonly PdfFile _a;
    private readonly PdfFile _b;
    private readonly HashSet<string> _ignoredSourceKeys;
    private static readonly HashSet<string> SizeKeys = new(StringComparer.Ordinal) { "Width", "Height" };

    private readonly long _decodeLimit;
    private readonly ImageAllowance _allowance;
    private readonly VerificationRules _rules;
    private readonly Lazy<ContentAnalysis?> _analysis;
    private readonly Lazy<Dictionary<int, HashSet<int>>> _fontPrograms;
    private readonly HashSet<(int, int)> _visited = new();
    private readonly Dictionary<int, int> _aToB = new();
    private readonly Dictionary<int, int> _bToA = new();
    private readonly Queue<(int A, int B, string Path)> _pending = new();
    private readonly List<StreamJob> _streams = new();

    private EquivalenceVerifier(PdfFile a, PdfFile b, VerificationRules rules, long decodeLimit, CancellationToken ct)
    {
        _a = a;
        _b = b;
        _rules = rules;
        _ignoredSourceKeys = new HashSet<string>(rules.IgnoredSourceKeys, StringComparer.Ordinal);
        _decodeLimit = decodeLimit;
        _allowance = rules.Images;

        // The source is analysed only once a difference needs it; most comparisons never do.
        _analysis = new Lazy<ContentAnalysis?>(() => Analyze(ct));
        _fontPrograms = new Lazy<Dictionary<int, HashSet<int>>>(() =>
            _analysis.Value is { } analysis ? FontProgramMerger.MergeablePrograms(_a, analysis, _decodeLimit) : new Dictionary<int, HashSet<int>>());
    }

    private ContentAnalysis? Analyze(CancellationToken ct)
    {
        try
        {
            ObjectGraph graph = ObjectGraph.Collect(_a, stripThumbnails: false, stripPieceInfo: false, ct);
            return ContentAnalysis.Run(_a, graph.Order, _ignoredSourceKeys, _decodeLimit, ct);
        }
        catch (PdfFormatException)
        {
            return null;
        }
    }

    /// <param name="Images">Source and output geometry, set when the image may have been resampled.</param>
    private sealed record StreamJob(PdfStream A, PdfStream B, int NumberA, string Path, List<FilterSpec>? FiltersA, List<FilterSpec>? FiltersB, (ImageGeometry A, ImageGeometry B)? Images);

    public int ObjectsCompared { get; private set; }

    public int StreamsCompared { get; private set; }

    /// <param name="rules">The changes beyond re-encoding that the output may contain.</param>
    /// <returns>Null when equivalent, otherwise a description of the first difference found.</returns>
    public static string? Verify(PdfFile source, PdfFile output, VerificationRules rules, long decodeLimit, int parallelism, CancellationToken ct, out int objectsCompared)
    {
        var v = new EquivalenceVerifier(source, output, rules, decodeLimit, ct);
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
            string? d = CompareResolved(_a.GetObject(a), _b.GetObject(b), path, 0, a);
            if (d is not null) return d;
        }

        // Filter parameters were resolved during the walk; stream content is compared in parallel.
        var jobs = _streams;
        string? failure = null;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, (job, state) =>
        {
            string? d = CompareStreamData(job);
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

    /// <param name="numberA">Object number of <paramref name="a"/> when it is an indirect object, else -1.</param>
    private string? CompareResolved(PdfObject? a, PdfObject? b, string path, int depth, int numberA = -1)
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
                return b is PdfDictionary bDict ? CompareDictionaries(aDict, bDict, path, depth, -1) : path + ": dictionary vs other";
            case PdfStream aStream:
            {
                if (b is not PdfStream bStream) return path + ": stream vs other";
                List<FilterSpec>? fa = SafeFilters(_a, aStream);
                List<FilterSpec>? fb = SafeFilters(_b, bStream);

                // A resampled image may change its size and its coding between raw samples and
                // JPEG; the data comparison proves the rest. Images the lossy pass never changes
                // must match exactly, whatever the allowance says.
                (ImageGeometry, ImageGeometry)? images = null;
                if (numberA >= 0 && _allowance.MayResample(numberA) && fa is not null && fb is not null
                    && LossyImages.MayResample(_a, aStream.Dictionary) && !LossyImages.HasMatteSoftMask(_a, aStream.Dictionary)
                    && LossyImages.Geometry(_a, aStream.Dictionary, fa) is { } ga && LossyImages.Geometry(_b, bStream.Dictionary, fb) is { } gb)
                {
                    // A soft mask with /Matte must keep its image's size (ISO 32000-1, 11.6.5.3).
                    bool resized = ga.Width != gb.Width || ga.Height != gb.Height;
                    if (resized && LossyImages.HasMatteSoftMask(_b, bStream.Dictionary)) return path + ": image with a /Matte soft mask changed size";
                    images = (ga, gb);
                }

                string? d = CompareDictionaries(aStream.Dictionary, bStream.Dictionary, path, depth, numberA, isStream: true, sizeMayChange: images is not null);
                if (d is not null) return d;

                // Parameters of filters that stay encoded (JBIG2Globals, DCT settings) are part of
                // the content, and may reference other objects, so they go through the full walk.
                if (fa is not null && fb is not null && images is null)
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

                _streams.Add(new StreamJob(aStream, bStream, numberA, path, fa, fb, images));
                return null;
            }

            default:
                return path + ": unexpected object";
        }
    }

    /// <param name="numberA">Object number of the source stream whose dictionary this is, else -1.</param>
    private string? CompareDictionaries(PdfDictionary a, PdfDictionary b, string path, int depth, int numberA, bool isStream = false, bool sizeMayChange = false)
    {
        // A merged TrueType program has a new length; /Length1 describes the encoding of the data.
        bool mergedProgram = isStream && _rules.AllowFontMerging && numberA >= 0 && a["Length1"] is not null && _fontPrograms.Value.ContainsKey(numberA);

        // Inherited page attributes may move between tree nodes and pages. Nodes are not compared
        // on them; each page is compared on the values it ends up with.
        bool page = !isStream && _rules.AllowInheritedAttributes && a.GetName("Type") == "Page" && b.GetName("Type") == "Page";
        bool node = !isStream && _rules.AllowInheritedAttributes && a.GetName("Type") == "Pages" && b.GetName("Type") == "Pages";
        if (page)
        {
            foreach (string key in InheritedAttributes.Keys)
            {
                string? d = Compare(Effective(_a, a, key), Effective(_b, b, key), path + "/" + key, depth + 1);
                if (d is not null) return d;
            }
        }

        foreach (string key in a.Keys)
        {
            if (isStream && EncodingKeys.Contains(key)) continue;
            if (sizeMayChange && SizeKeys.Contains(key)) continue;
            if (mergedProgram && key == "Length1") continue;
            if ((page || node) && Inheritable(key)) continue;
            if (_ignoredSourceKeys.Contains(key) && b[key] is null) continue;
            if (b[key] is null && _rules.AllowResourcePruning && Unused(a, key)) continue;
            string? d = Compare(a[key], b[key] ?? PdfNull.Instance, path + "/" + key, depth + 1);
            if (d is not null) return d;
        }

        foreach (string key in b.Keys)
        {
            if (isStream && EncodingKeys.Contains(key)) continue;
            if (sizeMayChange && SizeKeys.Contains(key)) continue;
            if (mergedProgram && key == "Length1") continue;
            if ((page || node) && Inheritable(key)) continue;
            if (a.ContainsKey(key)) continue;
            if (_b.Resolve(b[key]) is not PdfNull) return path + "/" + key + ": key only in output";
        }

        return null;
    }

    private static bool Inheritable(string key) => Array.IndexOf(InheritedAttributes.Keys, key) >= 0;

    /// <summary>A page's value for an inheritable attribute: its own, or the nearest ancestor's.</summary>
    private static PdfObject Effective(PdfFile file, PdfDictionary page, string key)
    {
        PdfDictionary? d = page;
        var seen = new HashSet<PdfDictionary>();
        for (int depth = 0; d is not null && depth <= 64 && seen.Add(d); depth++)
        {
            if (d[key] is { } value && file.Resolve(value) is not PdfNull) return value;
            d = file.ResolveDictionary(d["Parent"]);
        }

        return PdfNull.Instance;
    }

    /// <summary>True when the source analysis proves no content can look <paramref name="key"/> up in <paramref name="category"/>.</summary>
    private bool Unused(PdfDictionary category, string key) =>
        _analysis.Value is { Complete: true } analysis &&
        analysis.PrunableCategories.TryGetValue(category, out HashSet<string>? used) &&
        ResourcePruner.MayRemove(used, key);

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

    private string? CompareStreamData(StreamJob job)
    {
        (PdfStream a, PdfStream b, int numberA, string path, List<FilterSpec>? fa, List<FilterSpec>? fb) = (job.A, job.B, job.NumberA, job.Path, job.FiltersA, job.FiltersB);
        using DecodedStream? da = TryDecode(a, fa);
        using DecodedStream? db = TryDecode(b, fb);
        if (da is null || db is null)
        {
            // Undecodable data must have been carried over untouched, encoding and size included.
            if (job.Images is { } g && g.A != g.B) return path + ": image size or coding changed on undecodable data";
            if (!a.Data.Span.SequenceEqual(b.Data.Span)) return path + ": undecodable stream data changed";
            return SameFilterChain(fa, fb) ? null : path + ": filter chain changed on undecodable stream";
        }

        if (job.Images is { } images)
        {
            // The dictionary walk let the size differ; only an image carried over whole, same
            // size, coding and data, or one the lossy proof accepts, may pass.
            bool unchanged = images.A == images.B && da.PendingPredictor == db.PendingPredictor
                && SameFilterChain(da.Remaining, db.Remaining) && da.Data.Span.SequenceEqual(db.Data.Span);
            if (unchanged) return null;

            // Each side must decode as its own dictionary says: samples, or one JPEG.
            static bool Coded(DecodedStream d, ImageGeometry g) =>
                d.PendingPredictor is null && (g.Jpeg ? d.Remaining is [{ Name: "DCTDecode" }] : d.Remaining.Count == 0);
            if (!Coded(da, images.A) || !Coded(db, images.B)) return path + ": resampled image is not coded as its dictionary says";
            string? failure = LossyImages.Verify(da.Data.Span, images.A, db.Data.Span, images.B, _allowance.MinSsim, _decodeLimit);
            return failure is null ? null : path + ": " + failure;
        }

        if (da.PendingPredictor != db.PendingPredictor) return path + ": predictor differs";
        if (!SameFilterChain(da.Remaining, db.Remaining)) return path + ": remaining filters differ";
        if (da.Data.Span.SequenceEqual(db.Data.Span)) return null;

        bool plain = da.PendingPredictor is null && da.Remaining.Count == 0;
        if (plain && numberA >= 0 && _rules.AllowContentCompaction && _analysis.Value is { } analysis && analysis.ContentStreams.Contains(numberA))
        {
            string? difference = ContentCompactor.FindDifference(da.Data, db.Data);
            return difference is null ? null : path + ": content " + difference;
        }

        if (plain && numberA >= 0 && _rules.AllowFontMerging && _fontPrograms.Value.TryGetValue(numberA, out HashSet<int>? glyphs))
        {
            string? difference = TrueTypeGlyphs.FindDifference(da.Data.Span, db.Data.Span, glyphs);
            return difference is null ? null : path + ": font program " + difference;
        }

        // Re-encoded JPEG data differs in bytes; equality is decided on its coefficients.
        if (da.PendingPredictor is null && da.Remaining is [{ Name: "DCTDecode" } dct])
        {
            bool requantize = numberA >= 0 && _allowance.MayRequantize(numberA);
            string? difference = JpegEquivalence.FindDifference(da.Data.Span, db.Data.Span, requantize, _decodeLimit);
            if (difference is null && requantize && _allowance.MinSsim is { } gate)
            {
                int? colorTransform = dct.Parms is PdfDictionary parms && parms["ColorTransform"] is PdfNumber { IsInteger: true } t && t.IntegerValue is 0 or 1
                    ? (int)t.IntegerValue
                    : null;
                if (!LossyImages.RequantizationPasses(da.Data.Span, db.Data.Span, colorTransform, gate, _decodeLimit)) difference = "requantized JPEG is below the perceptual gate";
            }

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

/// <summary>What the verifier accepts beyond a different encoding of the same streams.</summary>
internal sealed record VerificationRules
{
    /// <summary>Keys allowed to exist only in the source, for opt-in stripping.</summary>
    public IReadOnlyCollection<string> IgnoredSourceKeys { get; init; } = [];

    /// <summary>
    /// Lossy image changes to accept, by source object number: requantization, proven exactly,
    /// and resampling or conversion to JPEG, proven by SSIM. Everything else about such an image
    /// must still match. With a gate, requantized images must also meet it. None by default.
    /// </summary>
    public ImageAllowance Images { get; init; } = ImageAllowance.None;

    /// <summary>Resource entries no content can name may be missing from the output.</summary>
    public bool AllowResourcePruning { get; init; }

    /// <summary>Content streams may be laid out differently if they read as the same tokens.</summary>
    public bool AllowContentCompaction { get; init; }

    /// <summary>TrueType programs of identity mapped composite fonts may change where no shown glyph is affected.</summary>
    public bool AllowFontMerging { get; init; }

    /// <summary>Inheritable page attributes may move from page tree nodes onto the pages that inherit them.</summary>
    public bool AllowInheritedAttributes { get; init; }

    /// <summary>Every lossless structural change the optimizer makes, and no lossy image change.</summary>
    public static VerificationRules Lossless { get; } = new() { AllowResourcePruning = true, AllowContentCompaction = true, AllowFontMerging = true, AllowInheritedAttributes = true };
}
