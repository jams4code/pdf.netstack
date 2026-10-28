using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using PdfNetStack.Writing;

namespace PdfNetStack.Optimization;

/// <summary>What the JPEG pass may do. Null quality keeps every image lossless.</summary>
internal readonly record struct JpegSettings(int? Quality);

/// <summary>Streams whose encoding changed, and how many of them were JPEG images.</summary>
/// <param name="RequantizedObjects">Source object numbers of the JPEG images that were requantized.</param>
internal readonly record struct RecompressionCounts(int Streams, int JpegImages, HashSet<int> RequantizedObjects)
{
    public int JpegRequantized => RequantizedObjects.Count;
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

    public static RecompressionCounts Run(PdfFile file, ObjectGraph graph, CompressionLevel level, bool imagePredictors, JpegSettings? jpeg, long decodeLimit, int parallelism, CancellationToken ct)
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
        HashSet<int> masks = jpeg?.Quality is null ? [] : SoftMaskTargets(file, streams);
        var plans = new Plan?[streams.Count];
        for (int i = 0; i < streams.Count; i++)
        {
            JpegSettings? settings = jpeg is { } j && masks.Contains(numbers[i]) ? j with { Quality = null } : jpeg;
            plans[i] = Prepare(file, streams[i], settings);
        }

        // Largest first, handed out one at a time, so one big image never runs alone at the end.
        int[] schedule = Enumerable.Range(0, streams.Count).OrderByDescending(i => streams[i].Data.Length).ToArray();
        int changed = 0, jpegImages = 0;
        var requantized = new bool[streams.Count];
        var partitioner = System.Collections.Concurrent.Partitioner.Create(schedule, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering);
        Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i =>
        {
            Plan? plan = plans[i];
            if (plan is null) return;
            Result? result = Recompress(streams[i], plan, level, imagePredictors, decodeLimit);
            if (result is null) return;
            if (result.Jpeg) Interlocked.Increment(ref jpegImages);
            requantized[i] = result.Requantized;
            PdfDictionary d = streams[i].Dictionary;
            streams[i].Data = result.Data;
            d.Remove("DL");
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
        for (int i = 0; i < streams.Count; i++)
        {
            if (requantized[i]) requantizedObjects.Add(numbers[i]);
        }

        return new RecompressionCounts(changed, jpegImages, requantizedObjects);
    }

    /// <param name="Jpeg">Set for a JPEG image the JPEG pass may re-encode.</param>
    private sealed record Plan(List<FilterSpec> Filters, int OriginalOverhead, PredictorParams? ImageLayout, bool KeepUnfiltered, JpegPlan? Jpeg);

    /// <param name="Quality">Requantization target, or null to stay lossless.</param>
    /// <param name="ColorTransform">The DCTDecode /ColorTransform parameter, when present.</param>
    /// <param name="Components">Components the image's color space has; the JPEG must match.</param>
    private sealed record JpegPlan(int? Quality, int? ColorTransform, int Components);

    private sealed record Result(ReadOnlyMemory<byte> Data, PdfObject? Filter, PdfObject? DecodeParms, bool Jpeg = false, bool Requantized = false);

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

    private static Plan? Prepare(PdfFile file, PdfStream stream, JpegSettings? jpeg)
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

        return new Plan(resolved, probe.Length, ImageLayout(file, d), keepUnfiltered, jpegPlan);
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
        return new JpegPlan(settings.Quality, colorTransform, components);
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

    private static Result? Recompress(PdfStream stream, Plan plan, CompressionLevel level, bool imagePredictors, long decodeLimit)
    {
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
            return Choose(decoded, plan, originalCost, level, imagePredictors, decodeLimit);
        }
    }

    private static Result? Choose(DecodedStream decoded, Plan plan, int originalCost, CompressionLevel level, bool imagePredictors, long decodeLimit)
    {
        if (decoded.LayersDecoded == 0 && plan.Filters.Count > 0 && plan.Jpeg is null) return null;

        Result? best = null;
        int bestCost = originalCost;

        // Candidates that point into the pooled decode buffer are copied only if they win.
        void Consider(ReadOnlyMemory<byte> data, PdfObject? filter, PdfObject? parms, bool borrowed = false, bool jpeg = false, bool requantized = false)
        {
            int overhead = 0;
            if (filter is not null)
            {
                var probe = new ByteSink(64);
                var serializer = new PdfSerializer(r => r.Number);
                PdfSerializer.WriteName(probe, "Filter");
                serializer.Write(probe, filter);
                if (parms is not null)
                {
                    PdfSerializer.WriteName(probe, "DecodeParms");
                    serializer.Write(probe, parms);
                }

                overhead = probe.Length;
            }

            int cost = data.Length + overhead;
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
            byte[] deflated = StreamCodec.Deflate(decoded.Data.Span, level);
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
                Consider(StreamCodec.Deflate(decoded.Data.Span, level), new PdfName("FlateDecode"), null);
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
                Consider(StreamCodec.Deflate(predicted, level), new PdfName("FlateDecode"), StreamCodec.PredictorDictionary(layout));
                if (level == CompressionLevel.Fastest) Consider(StreamCodec.Deflate(decoded.Data.Span, level), new PdfName("FlateDecode"), null);
            }
            else
            {
                Consider(StreamCodec.Deflate(decoded.Data.Span, level), new PdfName("FlateDecode"), null);
            }

            return best;
        }

        // Undecodable inner filters (DCT, JPX, ...) remain. Drop the decodable outer layers,
        // and also try a Flate wrapper in case the inner data still compresses.
        (PdfObject innerFilter, PdfObject? innerParms) = BuildChain(remaining, null);
        if (plan.Jpeg is { } jpeg && remaining.Count == 1)
        {
            byte[]? transcoded = JpegTranscoder.TryTranscode(decoded.Data.Span, jpeg.Quality, jpeg.ColorTransform, jpeg.Components, decodeLimit, out bool requantized);
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
