using System.IO.Compression;
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
}

/// <summary>
/// Re-encodes streams. Text based encodings (ASCIIHex, ASCII85) are removed, LZW and RunLength
/// become Flate, Flate is recompressed at the configured level, and raw 8-bit or lower images get
/// PNG prediction when it helps. JPEG images are re-encoded at the coefficient level, losslessly
/// unless a quality is set. A new encoding is kept only when it is smaller. JPX, JBIG2 and CCITT
/// data is never decoded.
/// </summary>
internal static class StreamRecompressor
{
    private const int FlateOverhead = 19; // "/Filter/FlateDecode"

    /// <param name="exhaustive">Also run the exhaustive Flate encoder on each stream's winning data.</param>
    /// <param name="images">The lossy image pass, or null to leave image samples alone.</param>
    public static RecompressionCounts Run(PdfFile file, ObjectGraph graph, CompressionLevel level, bool exhaustive, bool imagePredictors, JpegSettings? jpeg, ImageSettings? images, long decodeLimit, int parallelism, CancellationToken ct)
    {
        var streams = new List<PdfStream>();
        var numbers = new List<int>();
        foreach (int number in graph.Order)
        {
            if (file.GetObject(number) is not PdfStream s) continue;
            streams.Add(s);
            numbers.Add(number);
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
            plans[i] = Prepare(file, streams[i], settings, mask ? null : images, placed.TryGetValue(numbers[i], out PlacedSize size) ? size : null);
        }

        // Largest first, handed out one at a time, so one big image never runs alone at the end.
        int[] schedule = Enumerable.Range(0, streams.Count).OrderByDescending(i => streams[i].Data.Length).ToArray();
        int changed = 0, jpegImages = 0, keptByGate = 0;
        var requantized = new bool[streams.Count];
        var lossyKinds = new LossyKind[streams.Count];
        var partitioner = System.Collections.Concurrent.Partitioner.Create(schedule, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering);
        Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i =>
        {
            Plan? plan = plans[i];
            if (plan is null) return;
            Result? result = Recompress(streams[i], plan, new Flate(level, exhaustive, ct), imagePredictors, decodeLimit, out bool rejected);
            if (rejected) Interlocked.Increment(ref keptByGate);
            if (result is null) return;
            if (result.Jpeg) Interlocked.Increment(ref jpegImages);
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
        for (int i = 0; i < streams.Count; i++)
        {
            if (requantized[i]) requantizedObjects.Add(numbers[i]);
            if (lossyKinds[i] != LossyKind.None) resampledObjects.Add(numbers[i]);
        }

        return new RecompressionCounts(changed, jpegImages, requantizedObjects)
        {
            ResampledObjects = resampledObjects,
            Downsampled = lossyKinds.Count(k => (k & LossyKind.Downsampled) != 0),
            ConvertedToJpeg = lossyKinds.Count(k => (k & LossyKind.ConvertedToJpeg) != 0),
            KeptByGate = keptByGate,
        };
    }

    /// <param name="Jpeg">Set for a JPEG image the JPEG pass may re-encode.</param>
    /// <param name="Image">Set for an image the lossy image pass may change, with <paramref name="Images"/>.</param>
    private sealed record Plan(List<FilterSpec> Filters, int OriginalOverhead, PredictorParams? ImageLayout, bool KeepUnfiltered, JpegPlan? Jpeg, ImagePlan? Image, ImageSettings? Images);

    /// <param name="Quality">Requantization target, or null to stay lossless.</param>
    /// <param name="ColorTransform">The DCTDecode /ColorTransform parameter, when present.</param>
    /// <param name="Components">Components the image's color space has; the JPEG must match.</param>
    /// <param name="Progressive">Also try progressive coding.</param>
    /// <param name="MinSsim">The perceptual gate for requantization, or null.</param>
    private sealed record JpegPlan(int? Quality, int? ColorTransform, int Components, bool Progressive, double? MinSsim);

    private sealed record Result(ReadOnlyMemory<byte> Data, PdfObject? Filter, PdfObject? DecodeParms, bool Jpeg = false, bool Requantized = false)
    {
        public LossyKind Lossy { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }
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

    private static Plan? Prepare(PdfFile file, PdfStream stream, JpegSettings? jpeg, ImageSettings? images, PlacedSize? placed)
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
        ImagePlan? imagePlan = images is null ? null : LossyImages.PlanFor(file, d, filters, images, jpegPlan is not null, placed);
        if (filters.Count > 0 && !StreamCodec.IsDecodable(filters[0].Name) && jpegPlan is null) return null;
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

        return new Plan(resolved, probe.Length, ImageLayout(file, d), keepUnfiltered, jpegPlan, imagePlan, images);
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
        return new JpegPlan(settings.Quality, colorTransform, components, settings.Progressive, settings.MinSsim);
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

    private static Result? Recompress(PdfStream stream, Plan plan, Flate level, bool imagePredictors, long decodeLimit, out bool rejected)
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
            Result? best = Choose(decoded, plan, originalCost, level, imagePredictors, decodeLimit, out rejected);
            if (plan.Image is null || plan.Images is null) return best;

            // Lossy candidates must beat the best lossless (or requantized) encoding by a clear margin.
            int bestCost = best is null ? originalCost : best.Data.Length + Overhead(best.Filter, best.DecodeParms);
            Result? lossy = ChooseLossy(decoded, plan.Image, plan.Images, bestCost, level, imagePredictors, decodeLimit, out bool gateRejected);
            rejected |= gateRejected && lossy is null;
            return lossy ?? best;
        }
    }

    private static Result? ChooseLossy(DecodedStream decoded, ImagePlan image, ImageSettings settings, int bestCost, Flate level, bool imagePredictors, long decodeLimit, out bool rejected)
    {
        rejected = false;
        ReadOnlyMemory<byte> data = decoded.Data;
        BoundedBuffer? unpredicted = null;
        try
        {
            if (image.Jpeg)
            {
                if (decoded.PendingPredictor is not null || decoded.Remaining is not [{ Name: "DCTDecode" }]) return null;
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
        if (decoded.LayersDecoded == 0 && plan.Filters.Count > 0 && plan.Jpeg is null) return null;

        CompressionLevel level = flate.Level;
        Result? best = null;
        int bestCost = originalCost;

        // Candidates that point into the pooled decode buffer are copied only if they win.
        void Consider(ReadOnlyMemory<byte> data, PdfObject? filter, PdfObject? parms, bool borrowed = false, bool jpeg = false, bool requantized = false)
        {
            int cost = data.Length + Overhead(filter, parms);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = new Result(borrowed ? data.ToArray() : data, filter, parms, jpeg, requantized);
            }
        }

        List<FilterSpec> remaining = decoded.Remaining;
        if (decoded.PendingPredictor is { } pending)
        {
            // Keep the existing predictor, recompress underneath it.
            byte[] deflated = flate.Encode(decoded.Data.Span);
            (PdfObject filter, PdfObject? parms) = BuildChain(remaining, StreamCodec.PredictorDictionary(pending));
            Consider(deflated, filter, parms);
            return best;
        }

        if (remaining.Count == 0)
        {
            Consider(decoded.Data, null, null, borrowed: true);

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
