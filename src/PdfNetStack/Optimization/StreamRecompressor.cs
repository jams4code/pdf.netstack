using System.IO.Compression;
using PdfNetStack.Content;
using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Optimization;

/// <summary>What the JPEG pass may do. Null quality keeps every image lossless.</summary>
/// <param name="Quality">Requantization target, or null.</param>
/// <param name="Progressive">Also try progressive coding.</param>
/// <param name="MinSsim">The perceptual gate for requantization, or null for none.</param>
internal readonly record struct JpegSettings(int? Quality, bool Progressive = false, double? MinSsim = null);

/// <summary>zlib at a level, optionally followed by the exhaustive encoder on data that is final.</summary>
internal readonly record struct Flate(CompressionLevel Level, bool Exhaustive, CancellationToken Cancellation = default)
{
    public byte[] Encode(ReadOnlySpan<byte> data) => StreamCodec.Deflate(data, Level, Exhaustive, Cancellation);
}

/// <summary>Streams whose encoding changed, and what the image passes did.</summary>
/// <param name="RequantizedObjects">Source object numbers of the JPEG images that were requantized.</param>
internal sealed record RecompressionCounts(int Streams, int JpegImages, HashSet<int> RequantizedObjects)
{
    public int JpegRequantized => RequantizedObjects.Count;

    /// <summary>Source object numbers of images that were downsampled or converted to JPEG.</summary>
    public HashSet<int> ResampledObjects { get; init; } = [];

    public int Downsampled { get; init; }

    public int ConvertedToJpeg { get; init; }

    /// <summary>Images kept as they were because every lossy candidate failed the perceptual gate.</summary>
    public int KeptByGate { get; init; }

    /// <summary>Content streams rewritten by <see cref="ContentCompactor"/>.</summary>
    public int Compacted { get; init; }

    /// <summary>Bilevel images re-encoded as CCITT Group 4 with the same pixels.</summary>
    public int Group4 { get; init; }

    /// <summary>
    /// For each stream that was decoded, a hash of what it holds: the bytes left after the
    /// decodable layers, the predictor still to undo and the filters still to apply, or the
    /// final encoding when the image passes changed the image. Streams with equal hashes decode
    /// to the same content whatever their encoding.
    /// </summary>
    public Dictionary<int, byte[]> ContentHashes { get; init; } = [];
}

/// <summary>Which streams the recompressor works on and what it may do with them.</summary>
/// <param name="Recompress">Re-encode streams in general.</param>
/// <param name="Compact">Content streams to compact; they are processed even when <paramref name="Recompress"/> is off.</param>
/// <param name="Skip">Streams another pass has already encoded.</param>
/// <param name="HashContent">Record <see cref="RecompressionCounts.ContentHashes"/>.</param>
internal sealed record RecompressionScope(bool Recompress, HashSet<int> Compact, HashSet<int> Skip, bool HashContent)
{
    /// <summary>Everything, as the optimizer did before the structural passes.</summary>
    public static RecompressionScope All { get; } = new(true, [], [], false);
}

/// <summary>
/// Re-encodes streams. Text based encodings (ASCIIHex, ASCII85) are removed, LZW and RunLength
/// become Flate, Flate is recompressed at the configured level, and raw 8-bit or lower images get
/// PNG prediction when it helps. JPEG images are re-encoded at the coefficient level, losslessly
/// unless a quality is set. Bilevel images stored as samples or as CCITT Group 3 may become CCITT
/// Group 4 with the same pixels. Content streams may also be compacted. A new encoding is kept
/// only when it is smaller. JPX and JBIG2 data, and CCITT data other than Group 3, is decoded
/// only by the lossy image pass.
/// </summary>
internal static class StreamRecompressor
{
    private const int FlateOverhead = 19; // "/Filter/FlateDecode"

    /// <param name="exhaustive">Also run the exhaustive Flate encoder on each stream's winning data.</param>
    /// <param name="group4">Try CCITT Group 4 on bilevel images.</param>
    /// <param name="images">The lossy image pass, or null to leave image samples alone.</param>
    public static RecompressionCounts Run(PdfFile file, ObjectGraph graph, RecompressionScope scope, CompressionLevel level, bool exhaustive, bool imagePredictors, bool group4, JpegSettings? jpeg, ImageSettings? images, long decodeLimit, int parallelism, CancellationToken ct)
    {
        var streams = new List<PdfStream>();
        var numbers = new List<int>();
        foreach (int number in graph.Order)
        {
            if (file.GetObject(number) is not PdfStream s || scope.Skip.Contains(number)) continue;
            if (!scope.Recompress && !scope.Compact.Contains(number)) continue;
            streams.Add(s);
            numbers.Add(number);
        }

        // Without recompression only content streams are touched, and only by compaction.
        if (!scope.Recompress)
        {
            jpeg = null;
            images = null;
            group4 = false;
        }

        // Pages of one document usually share their JBIG2Globals; each is decoded once.
        var globals = new Dictionary<int, byte[]?>();
        byte[]? Globals(PdfObject? reference)
        {
            if (reference is not PdfReference r) return LossyImages.ReadGlobals(file, reference, decodeLimit);
            if (!globals.TryGetValue(r.Number, out byte[]? data)) globals[r.Number] = data = LossyImages.ReadGlobals(file, r, decodeLimit);
            return data;
        }

        // Pre-resolve everything a worker may need so that workers never touch the parser.
        HashSet<int> masks = jpeg?.Quality is null && images is null ? [] : SoftMaskTargets(file, streams);
        Dictionary<int, PlacedSize> placed = images?.MaxDpi is null
            ? []
            : ImagePlacement.Measure(file, graph, [.. numbers.Where((n, i) => streams[i].Dictionary.GetName("Subtype") == "Image")], decodeLimit, ct);
        var plans = new Plan?[streams.Count];
        for (int i = 0; i < streams.Count; i++)
        {
            bool mask = masks.Contains(numbers[i]);
            JpegSettings? settings = jpeg is { } j && mask ? j with { Quality = null } : jpeg;
            plans[i] = Prepare(file, streams[i], settings, mask ? null : images, placed.TryGetValue(numbers[i], out PlacedSize size) ? size : null, group4, Globals)
                is { } plan ? plan with { Recompress = scope.Recompress, Compact = scope.Compact.Contains(numbers[i]) } : null;
        }

        // Largest first, handed out one at a time, so one big image never runs alone at the end.
        int[] schedule = Enumerable.Range(0, streams.Count).OrderByDescending(i => streams[i].Data.Length).ToArray();
        int changed = 0, jpegImages = 0, keptByGate = 0, compacted = 0, group4Images = 0;
        var requantized = new bool[streams.Count];
        var hashes = new byte[]?[streams.Count];
        var lossyKinds = new LossyKind[streams.Count];
        var partitioner = System.Collections.Concurrent.Partitioner.Create(schedule, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering);

        // Decoding images for the lossy pass takes several times their samples in memory; workers
        // share a budget of twice the decode limit, so a document of large scans cannot multiply
        // that by the number of threads.
        var gate = new MemoryGate(2 * decodeLimit);
        Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i =>
        {
            Plan? plan = plans[i];
            if (plan is null) return;
            long weight = gate.Enter(plan.Image is { } image ? LossyImages.Footprint(image) : 0, ct);
            Result? result;
            bool rejected;
            try
            {
                result = Recompress(streams[i], plan, new Flate(level, exhaustive, ct), imagePredictors, decodeLimit,
                    scope.HashContent ? hash => hashes[i] = hash : null, out rejected);
            }
            finally
            {
                gate.Exit(weight);
            }

            if (rejected) Interlocked.Increment(ref keptByGate);
            if (result is null) return;
            if (result.Jpeg) Interlocked.Increment(ref jpegImages);
            if (result.Compacted) Interlocked.Increment(ref compacted);
            if (result.Group4) Interlocked.Increment(ref group4Images);
            requantized[i] = result.Requantized;
            lossyKinds[i] = result.Lossy;
            PdfDictionary d = streams[i].Dictionary;
            streams[i].Data = result.Data;
            d.Remove("DL");
            if ((result.Lossy & LossyKind.Downsampled) != 0)
            {
                d.Set("Width", PdfNumber.FromInteger(result.Width));
                d.Set("Height", PdfNumber.FromInteger(result.Height));
            }

            // A JPEG 2000 image may leave its depth to the data; samples and JPEG must state it.
            if (result.SetsBits) d.Set("BitsPerComponent", PdfNumber.FromInteger(8));

            if (result.Filter is null)
            {
                d.Remove("Filter");
                d.Remove("DecodeParms");
            }
            else
            {
                d.Set("Filter", result.Filter);
                if (result.DecodeParms is null) d.Remove("DecodeParms");
                else d.Set("DecodeParms", result.DecodeParms);
            }

            Interlocked.Increment(ref changed);
        });

        var requantizedObjects = new HashSet<int>();
        var resampledObjects = new HashSet<int>();
        var contentHashes = new Dictionary<int, byte[]>();
        for (int i = 0; i < streams.Count; i++)
        {
            if (requantized[i]) requantizedObjects.Add(numbers[i]);
            if (lossyKinds[i] != LossyKind.None) resampledObjects.Add(numbers[i]);
            if (hashes[i] is { } hash) contentHashes[numbers[i]] = hash;
        }

        return new RecompressionCounts(changed, jpegImages, requantizedObjects)
        {
            ResampledObjects = resampledObjects,
            Downsampled = lossyKinds.Count(k => (k & LossyKind.Downsampled) != 0),
            ConvertedToJpeg = lossyKinds.Count(k => (k & LossyKind.ConvertedToJpeg) != 0),
            KeptByGate = keptByGate,
            Compacted = compacted,
            Group4 = group4Images,
            ContentHashes = contentHashes,
        };
    }

    /// <summary>
    /// A budget of bytes shared by workers. A worker waits until its share fits beside the others;
    /// a share larger than the whole budget is cut to it, so it runs, alone.
    /// </summary>
    private sealed class MemoryGate(long budget)
    {
        private readonly object _lock = new();
        private long _inUse;

        /// <returns>The share taken, to give back to <see cref="Exit"/>.</returns>
        public long Enter(long bytes, CancellationToken ct)
        {
            long share = Math.Min(Math.Max(0, bytes), budget);
            if (share == 0) return 0;
            lock (_lock)
            {
                while (_inUse > 0 && _inUse + share > budget)
                {
                    ct.ThrowIfCancellationRequested();
                    Monitor.Wait(_lock, 100);
                }

                _inUse += share;
            }

            return share;
        }

        public void Exit(long share)
        {
            if (share == 0) return;
            lock (_lock)
            {
                _inUse -= share;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <param name="Jpeg">Set for a JPEG image the JPEG pass may re-encode.</param>
    /// <param name="Image">Set for an image the lossy image pass may change, with <paramref name="Images"/>.</param>
    /// <param name="Recompress">Re-encoding is allowed; without it only a compacted content stream may replace the data.</param>
    /// <param name="Compact">The stream is a content stream to compact.</param>
    private sealed record Plan(List<FilterSpec> Filters, int OriginalOverhead, PredictorParams? ImageLayout, bool KeepUnfiltered, JpegPlan? Jpeg, ImagePlan? Image, ImageSettings? Images, bool Recompress = true, bool Compact = false)
    {
        /// <summary>Set for a bilevel image that may become Group 4.</summary>
        public BilevelPlan? Bilevel { get; init; }
    }

    /// <summary>A bilevel image the Group 4 pass may re-encode.</summary>
    /// <param name="Ccitt">The parameters of Group 3 data, or null for samples.</param>
    private sealed record BilevelPlan(int Width, int Height, CcittParameters? Ccitt);

    /// <param name="Quality">Requantization target, or null to stay lossless.</param>
    /// <param name="ColorTransform">The DCTDecode /ColorTransform parameter, when present.</param>
    /// <param name="Components">Components the image's color space has; the JPEG must match.</param>
    /// <param name="Progressive">Also try progressive coding.</param>
    /// <param name="MinSsim">The perceptual gate for requantization, or null.</param>
    private sealed record JpegPlan(int? Quality, int? ColorTransform, int Components, bool Progressive, double? MinSsim);

    private sealed record Result(ReadOnlyMemory<byte> Data, PdfObject? Filter, PdfObject? DecodeParms, bool Jpeg = false, bool Requantized = false, bool Compacted = false)
    {
        public LossyKind Lossy { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        /// <summary>The image is now CCITT Group 4 with the same pixels.</summary>
        public bool Group4 { get; init; }

        /// <summary>The image needs /BitsPerComponent 8, which its dictionary leaves out.</summary>
        public bool SetsBits { get; init; }
    }

    /// <summary>
    /// Images that serve as another image's soft mask or explicit mask. Their samples are opacity,
    /// where requantization artifacts show as halos, so they only ever get the lossless pass.
    /// </summary>
    private static HashSet<int> SoftMaskTargets(PdfFile file, List<PdfStream> streams)
    {
        var targets = new HashSet<int>();
        foreach (PdfStream s in streams)
        {
            if (s.Dictionary.GetName("Subtype") != "Image") continue;
            if (s.Dictionary["SMask"] is PdfReference soft) targets.Add(soft.Number);
            if (s.Dictionary["Mask"] is PdfReference hard) targets.Add(hard.Number);
        }

        return targets;
    }

    private static Plan? Prepare(PdfFile file, PdfStream stream, JpegSettings? jpeg, ImageSettings? images, PlacedSize? placed, bool group4, Func<PdfObject?, byte[]?> globals)
    {
        PdfDictionary d = stream.Dictionary;

        // External file data lives outside the PDF.
        if (d["F"] is not null) return null;

        List<FilterSpec> filters;
        try
        {
            filters = StreamCodec.GetFilters(d, file.Resolve);
        }
        catch (StreamDecodeException)
        {
            return null;
        }

        JpegPlan? jpegPlan = jpeg is { } settings ? JpegPlanFor(file, d, filters, settings) : null;
        ImagePlan? imagePlan = images is null ? null : LossyImages.PlanFor(file, d, filters, images, jpegPlan is not null, placed, globals);
        BilevelPlan? bilevel = group4 ? BilevelPlanFor(file, d, filters) : null;
        if (filters.Count > 0 && !StreamCodec.IsDecodable(filters[0].Name) && jpegPlan is null && imagePlan is null && bilevel?.Ccitt is null) return null;
        if (filters.Exists(f => f.Name == "Crypt")) return null;

        // Resolve predictor parameters now; workers must not call back into the parser.
        var resolved = new List<FilterSpec>(filters.Count);
        foreach (FilterSpec f in filters)
        {
            PdfObject? p = file.Resolve(f.Parms);
            if (p is PdfDictionary pd) p = ResolveShallow(file, pd);
            if (f.Name is "FlateDecode" or "LZWDecode" && !HasRebuildableParms(p as PdfDictionary)) return null;
            resolved.Add(f with { Parms = p });
        }

        // An unfiltered XMP packet stays readable by tools that scan for it, as PDF/A expects.
        bool keepUnfiltered = filters.Count == 0 && d.GetName("Type") == "Metadata";
        if (keepUnfiltered) return null;

        var probe = new ByteSink(64);
        var serializer = new PdfSerializer(r => r.Number);
        if (d["Filter"] is { } fo)
        {
            PdfSerializer.WriteName(probe, "Filter");
            serializer.Write(probe, fo);
        }

        if (d["DecodeParms"] is { } dp)
        {
            PdfSerializer.WriteName(probe, "DecodeParms");
            serializer.Write(probe, dp);
        }

        return new Plan(resolved, probe.Length, ImageLayout(file, d), keepUnfiltered, jpegPlan, imagePlan, images) { Bilevel = bilevel };
    }

    /// <summary>
    /// A plan for a bilevel image (<see cref="BilevelImages.Size"/>) stored as samples under
    /// general filters, or as Group 3 data with parameters every reader takes alike. Null otherwise.
    /// </summary>
    private static BilevelPlan? BilevelPlanFor(PdfFile file, PdfDictionary d, List<FilterSpec> filters)
    {
        if (BilevelImages.Size(file, d) is not { } size) return null;
        bool coded = filters.Count > 0 && !StreamCodec.IsDecodable(filters[^1].Name);
        for (int i = 0; i < filters.Count - (coded ? 1 : 0); i++)
        {
            if (!StreamCodec.IsDecodable(filters[i].Name)) return null;
        }

        if (!coded) return new BilevelPlan(size.Width, size.Height, null);
        if (filters[^1].Name != "CCITTFaxDecode") return null;
        return BilevelImages.Parameters(filters[^1].Parms, size.Width, size.Height, file.Resolve) is { K: >= 0 } ccitt
            ? new BilevelPlan(size.Width, size.Height, ccitt)
            : null;
    }

    /// <summary>
    /// A plan for an image whose last filter is DCTDecode under nothing but decodable layers, with
    /// 8 bits per component and a color space whose component count is known. Null otherwise.
    /// </summary>
    private static JpegPlan? JpegPlanFor(PdfFile file, PdfDictionary d, List<FilterSpec> filters, JpegSettings settings)
    {
        if (filters.Count == 0 || filters[^1].Name != "DCTDecode") return null;
        for (int i = 0; i < filters.Count - 1; i++)
        {
            if (!StreamCodec.IsDecodable(filters[i].Name)) return null;
        }

        if (d.GetName("Subtype") != "Image") return null;
        if (file.Resolve(d["ImageMask"]) is PdfBoolean { Value: true }) return null;
        if (IntOf(file, d["BitsPerComponent"]) != 8) return null;

        // DCT data under a palette is invalid PDF; its samples are indices, so leave it alone.
        PdfObject? colorSpace = file.Resolve(d["ColorSpace"]);
        PdfObject? family = colorSpace is PdfArray { Count: > 0 } array ? file.Resolve(array[0]) : colorSpace;
        if (family is PdfName { Value: "Indexed" or "I" }) return null;
        int components = ColorComponents(file, colorSpace, 0);
        if (components is < 1 or > 4) return null;

        int? colorTransform = file.Resolve(filters[^1].Parms) is PdfDictionary parms &&
            file.Resolve(parms["ColorTransform"]) is PdfNumber { IsInteger: true } ct && ct.IntegerValue is 0 or 1
                ? (int)ct.IntegerValue
                : null;
        // A color key mask matches exact sample values, which requantization would move.
        int? quality = LossyImages.HasColorKey(file, d) ? null : settings.Quality;
        return new JpegPlan(quality, colorTransform, components, settings.Progressive, settings.MinSsim);
    }

    /// <summary>
    /// The recompressor rebuilds Flate and LZW parameters from their parsed values. That is only
    /// faithful when every key is a known predictor key holding a valid integer; anything else
    /// (a real, a reference, an unknown key, an invalid predictor) leaves the stream untouched.
    /// </summary>
    private static bool HasRebuildableParms(PdfDictionary? parms)
    {
        if (parms is null) return true;
        for (int i = 0; i < parms.Count; i++)
        {
            (string key, PdfObject value) = parms.EntryAt(i);
            if (value is PdfNull) continue;
            if (key is not ("Predictor" or "Colors" or "BitsPerComponent" or "Columns" or "EarlyChange")) return false;
            if (value is not PdfNumber { IsInteger: true } n || n.IntegerValue is < 0 or > int.MaxValue) return false;
        }

        PredictorParams p = StreamCodec.ReadPredictor(parms, static o => o);
        return p.Predictor <= 1 || p.IsValid;
    }

    private static PdfDictionary ResolveShallow(PdfFile file, PdfDictionary d)
    {
        var copy = new PdfDictionary(d.Count);
        for (int i = 0; i < d.Count; i++)
        {
            (string k, PdfObject v) = d.EntryAt(i);
            copy.Set(k, v is PdfReference && file.Resolve(v) is PdfNumber n ? n : v);
        }

        return copy;
    }

    /// <summary>Row layout of a raw image, used to try PNG prediction. Null when not applicable.</summary>
    private static PredictorParams? ImageLayout(PdfFile file, PdfDictionary d)
    {
        if (d.GetName("Subtype") != "Image") return null;
        int width = IntOf(file, d["Width"]);
        int height = IntOf(file, d["Height"]);
        if (width <= 0 || height <= 0) return null;

        bool mask = file.Resolve(d["ImageMask"]) is PdfBoolean { Value: true };
        int bpc = mask ? 1 : IntOf(file, d["BitsPerComponent"]);
        if (bpc is not (1 or 2 or 4 or 8)) return null;
        int colors = mask ? 1 : ColorComponents(file, d["ColorSpace"], 0);
        if (colors is < 1 or > 4) return null;
        return new PredictorParams(15, colors, bpc, width) with { };
    }

    private static int ColorComponents(PdfFile file, PdfObject? cs, int depth)
    {
        if (depth > 8) return 0;
        PdfObject? resolved = file.Resolve(cs);
        switch (resolved)
        {
            case PdfName n:
                return n.Value switch
                {
                    "DeviceGray" or "G" or "CalGray" => 1,
                    "DeviceRGB" or "RGB" or "CalRGB" or "Lab" => 3,
                    "DeviceCMYK" or "CMYK" => 4,
                    "Indexed" or "I" => 1,
                    _ => 0,
                };
            case PdfArray a when a.Count > 0 && file.Resolve(a[0]) is PdfName family:
                switch (family.Value)
                {
                    case "Indexed" or "I" or "Separation":
                        return 1;
                    case "CalGray":
                        return 1;
                    case "CalRGB" or "Lab":
                        return 3;
                    case "ICCBased" when a.Count > 1 && file.Resolve(a[1]) is PdfStream icc:
                        return IntOf(file, icc.Dictionary["N"]);
                    case "DeviceN" when a.Count > 1 && file.Resolve(a[1]) is PdfArray names:
                        return names.Count;
                    default:
                        return ColorComponents(file, family, depth + 1);
                }

            default:
                return 0;
        }
    }

    private static int IntOf(PdfFile file, PdfObject? o) =>
        file.Resolve(o) is PdfNumber { IsInteger: true } n && n.IntegerValue is > 0 and < int.MaxValue ? (int)n.IntegerValue : 0;

    private static Result? Recompress(PdfStream stream, Plan plan, Flate level, bool imagePredictors, long decodeLimit, Action<byte[]>? reportHash, out bool rejected)
    {
        rejected = false;
        int originalCost = stream.Data.Length + plan.OriginalOverhead;
        DecodedStream decoded;
        try
        {
            decoded = StreamCodec.Decode(stream.Data, plan.Filters, DecodeDepth.CompressionOnly, decodeLimit, static o => o);
        }
        catch (StreamDecodeException)
        {
            return null;
        }

        using (decoded)
        {
            byte[]? compacted = plan.Compact && decoded.Remaining.Count == 0 && decoded.PendingPredictor is null
                ? ContentCompactor.Compact(decoded.Data)
                : null;
            bool gateRejected = false;
            Result? result = compacted is not null
                ? ChooseCompacted(compacted, originalCost, level)
                : plan.Recompress ? Reencode(out gateRejected) : null;
            rejected = gateRejected;

            // Streams merge only when their final content is the same: compacted content is hashed
            // as compacted, a re-encoded or resampled image as its new encoding.
            if (reportHash is not null)
            {
                reportHash(result is { Jpeg: true } || result?.Lossy is not (null or LossyKind.None)
                    ? FinalHash(result!)
                    : ContentHash(compacted is not null && result is not null ? compacted : decoded.Data.Span, decoded.PendingPredictor, decoded.Remaining));
            }

            return result;
        }

        Result? Reencode(out bool gate)
        {
            Result? best = Choose(decoded, plan, originalCost, level, imagePredictors, decodeLimit, out gate);
            if (plan.Image is null || plan.Images is null) return best;

            // Lossy candidates must beat the best lossless (or requantized) encoding by a clear margin.
            int bestCost = best is null ? originalCost : best.Data.Length + Overhead(best.Filter, best.DecodeParms);
            Result? lossy = ChooseLossy(decoded, plan.Image, plan.Images, bestCost, level, imagePredictors, decodeLimit, out bool lossyRejected);
            gate |= lossyRejected && lossy is null;
            return lossy ?? best;
        }
    }

    private static byte[] ContentHash(ReadOnlySpan<byte> content, PredictorParams? predictor, List<FilterSpec> remaining)
    {
        var header = new ByteSink(64);
        var serializer = new PdfSerializer(r => r.Number) { KeepGenerations = true };
        if (predictor is { } p) serializer.Write(header, StreamCodec.PredictorDictionary(p));
        header.Write((byte)'|');
        foreach (FilterSpec f in remaining)
        {
            PdfSerializer.WriteName(header, f.Name);
            if (f.Parms is not null) serializer.Write(header, f.Parms);
        }

        header.Write((byte)'|');
        header.Write(Compat.Sha256(content));
        return Compat.Sha256(header.WrittenSpan);
    }

    /// <summary>A hash of a result's encoded data and the filters it is written with.</summary>
    private static byte[] FinalHash(Result result)
    {
        var header = new ByteSink(64);
        var serializer = new PdfSerializer(r => r.Number) { KeepGenerations = true };
        header.Write((byte)'F');
        if (result.Filter is not null) serializer.Write(header, result.Filter);
        header.Write((byte)'|');
        if (result.DecodeParms is not null) serializer.Write(header, result.DecodeParms);
        header.Write((byte)'|');
        header.Write(Compat.Sha256(result.Data.Span));
        return Compat.Sha256(header.WrittenSpan);
    }

    /// <summary>A compacted content stream, Flate encoded or plain, whichever beats the original.</summary>
    private static Result? ChooseCompacted(byte[] compacted, int originalCost, Flate flate)
    {
        byte[] deflated = flate.Encode(compacted);
        if (deflated.Length + FlateOverhead < Math.Min(originalCost, compacted.Length))
        {
            return new Result(deflated, new PdfName("FlateDecode"), null, Compacted: true);
        }

        return compacted.Length < originalCost ? new Result(compacted, null, null, Compacted: true) : null;
    }

    private static Result? ChooseLossy(DecodedStream decoded, ImagePlan image, ImageSettings settings, int bestCost, Flate level, bool imagePredictors, long decodeLimit, out bool rejected)
    {
        rejected = false;
        ReadOnlyMemory<byte> data = decoded.Data;
        BoundedBuffer? unpredicted = null;
        try
        {
            if (image.Coding != ImageCoding.Raw)
            {
                if (decoded.PendingPredictor is not null || decoded.Remaining is not [{ } only] || only.Name != LossyImages.Filter(image.Coding)) return null;
            }
            else
            {
                if (decoded.Remaining.Count > 0) return null;
                if (decoded.PendingPredictor is { } pending)
                {
                    if (!Predictors.TryDecode(data.Span, pending, decodeLimit, out unpredicted)) return null;
                    data = unpredicted!.Memory;
                }
            }

            Result? best = null;
            int limit = (int)(bestCost * LossyImages.RequiredGain);
            foreach (LossyCandidate c in LossyImages.Candidates(data.Span, image, settings, level, imagePredictors, decodeLimit, out rejected))
            {
                int cost = c.Data.Length + Overhead(c.Filter, c.DecodeParms);
                if (cost >= limit) continue;
                limit = cost;
                best = new Result(c.Data, c.Filter, c.DecodeParms, Jpeg: image.Jpeg)
                {
                    Lossy = c.Kind,
                    Width = c.Width,
                    Height = c.Height,
                    SetsBits = image.NeedsBits,
                };
            }

            return best;
        }
        finally
        {
            unpredicted?.Dispose();
        }
    }

    /// <summary>Bytes the /Filter and /DecodeParms entries take in the dictionary.</summary>
    private static int Overhead(PdfObject? filter, PdfObject? parms)
    {
        if (filter is null) return 0;
        var probe = new ByteSink(64);
        var serializer = new PdfSerializer(r => r.Number);
        PdfSerializer.WriteName(probe, "Filter");
        serializer.Write(probe, filter);
        if (parms is not null)
        {
            PdfSerializer.WriteName(probe, "DecodeParms");
            serializer.Write(probe, parms);
        }

        return probe.Length;
    }

    private static Result? Choose(DecodedStream decoded, Plan plan, int originalCost, Flate flate, bool imagePredictors, long decodeLimit, out bool rejected)
    {
        rejected = false;
        if (decoded.LayersDecoded == 0 && plan.Filters.Count > 0 && plan.Jpeg is null && plan.Bilevel?.Ccitt is null) return null;

        CompressionLevel level = flate.Level;
        Result? best = null;
        int bestCost = originalCost;

        // Candidates that point into the pooled decode buffer are copied only if they win.
        void Consider(ReadOnlyMemory<byte> data, PdfObject? filter, PdfObject? parms, bool borrowed = false, bool jpeg = false, bool requantized = false, bool group4 = false)
        {
            int cost = data.Length + Overhead(filter, parms);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = new Result(borrowed ? data.ToArray() : data, filter, parms, jpeg, requantized) { Group4 = group4 };
            }
        }

        // The same pixels as Group 4, from packed rows; nothing when they could not be had exactly.
        void ConsiderGroup4(byte[]? rows)
        {
            if (rows is null || plan.Bilevel is not { } b) return;
            if (BilevelImages.Group4(rows, b.Width, b.Height, bestCost) is { } g4) Consider(g4.Data, new PdfName("CCITTFaxDecode"), g4.Parms, group4: true);
        }

        List<FilterSpec> remaining = decoded.Remaining;
        if (decoded.PendingPredictor is { } pending)
        {
            // Keep the existing predictor, recompress underneath it.
            byte[] deflated = flate.Encode(decoded.Data.Span);
            (PdfObject filter, PdfObject? parms) = BuildChain(remaining, StreamCodec.PredictorDictionary(pending));
            Consider(deflated, filter, parms);
            if (plan.Bilevel is { Ccitt: null } bilevel && remaining.Count == 0 && Predictors.TryDecode(decoded.Data.Span, pending, decodeLimit, out BoundedBuffer? samples))
            {
                using BoundedBuffer unpredicted = samples!;
                ConsiderGroup4(BilevelImages.Pixels(unpredicted.Span, null, bilevel.Width, bilevel.Height, decodeLimit));
            }

            return best;
        }

        if (remaining.Count == 0)
        {
            Consider(decoded.Data, null, null, borrowed: true);
            if (plan.Bilevel is { Ccitt: null } bilevel) ConsiderGroup4(BilevelImages.Pixels(decoded.Data.Span, null, bilevel.Width, bilevel.Height, decodeLimit));

            bool predictable = imagePredictors && plan.ImageLayout is { RowBytes: > 0 } &&
                decoded.Data.Length % plan.ImageLayout.Value.RowBytes == 0 &&
                decoded.Data.Length / plan.ImageLayout.Value.RowBytes > 1;
            if (!predictable)
            {
                Consider(flate.Encode(decoded.Data.Span), new PdfName("FlateDecode"), null);
                return best;
            }

            // Rank plain and predicted data with a cheap pass, then spend the expensive
            // compression level only on the winner.
            PredictorParams layout = plan.ImageLayout!.Value;
            using BoundedBuffer predictedLease = Predictors.EncodePng(decoded.Data.Span, layout);
            ReadOnlySpan<byte> predicted = predictedLease.Span;
            bool usePredictor = level == CompressionLevel.Fastest
                || StreamCodec.Deflate(predicted, CompressionLevel.Fastest).Length < StreamCodec.Deflate(decoded.Data.Span, CompressionLevel.Fastest).Length;
            if (usePredictor)
            {
                Consider(flate.Encode(predicted), new PdfName("FlateDecode"), StreamCodec.PredictorDictionary(layout));
                if (level == CompressionLevel.Fastest) Consider(StreamCodec.Deflate(decoded.Data.Span, level), new PdfName("FlateDecode"), null);
            }
            else
            {
                Consider(flate.Encode(decoded.Data.Span), new PdfName("FlateDecode"), null);
            }

            return best;
        }

        // Undecodable inner filters (DCT, JPX, ...) remain. Drop the decodable outer layers,
        // and also try a Flate wrapper in case the inner data still compresses.
        (PdfObject innerFilter, PdfObject? innerParms) = BuildChain(remaining, null);
        if (plan.Bilevel is { Ccitt: { } ccitt } g3 && remaining is [{ Name: "CCITTFaxDecode" }])
        {
            ConsiderGroup4(BilevelImages.Pixels(decoded.Data.Span, ccitt, g3.Width, g3.Height, decodeLimit));
            if (decoded.LayersDecoded == 0) return best;
        }

        if (plan.Jpeg is { } jpeg && remaining.Count == 1)
        {
            byte[]? transcoded = JpegTranscoder.TryTranscode(decoded.Data.Span, jpeg.Quality, jpeg.ColorTransform, jpeg.Components, decodeLimit, out bool requantized, jpeg.Progressive);
            if (transcoded is not null && requantized && jpeg.MinSsim is { } gate
                && !LossyImages.RequantizationPasses(decoded.Data.Span, transcoded, jpeg.ColorTransform, gate, decodeLimit))
            {
                // The requantized image failed the gate: keep it lossless.
                rejected = true;
                transcoded = JpegTranscoder.TryTranscode(decoded.Data.Span, null, jpeg.ColorTransform, jpeg.Components, decodeLimit, out requantized, jpeg.Progressive);
            }

            if (transcoded is not null) Consider(transcoded, innerFilter, innerParms, jpeg: true, requantized: requantized);

            // A bare JPEG has no layers to drop, and entropy coded data does not deflate.
            if (decoded.LayersDecoded == 0) return best;
        }

        Consider(decoded.Data, innerFilter, innerParms, borrowed: true);
        byte[] wrapped = StreamCodec.Deflate(decoded.Data.Span, level);
        var withFlate = new List<FilterSpec>(remaining.Count + 1) { new("FlateDecode", null) };
        withFlate.AddRange(remaining);
        (PdfObject wf, PdfObject? wp) = BuildChain(withFlate, null);
        Consider(wrapped, wf, wp);
        return best;
    }

    /// <summary>Builds /Filter and /DecodeParms for a chain whose first element may get new parameters.</summary>
    private static (PdfObject Filter, PdfObject? Parms) BuildChain(List<FilterSpec> remaining, PdfDictionary? firstFlateParms)
    {
        var chain = new List<FilterSpec>();
        if (firstFlateParms is not null) chain.Add(new FilterSpec("FlateDecode", firstFlateParms));
        chain.AddRange(remaining);

        if (chain.Count == 1)
        {
            return (new PdfName(chain[0].Name), chain[0].Parms is PdfNull ? null : chain[0].Parms);
        }

        var names = new PdfArray();
        var parms = new PdfArray();
        bool anyParms = false;
        foreach (FilterSpec f in chain)
        {
            names.Items.Add(new PdfName(f.Name));
            parms.Items.Add(f.Parms ?? PdfNull.Instance);
            anyParms |= f.Parms is not null and not PdfNull;
        }

        return (names, anyParms ? parms : null);
    }
}
