using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>What the lossy image pass may do. Null <see cref="MaxDpi"/> and false <see cref="ConvertToJpeg"/> turn it off.</summary>
/// <param name="MaxDpi">Downsample images drawn above 1.5 times this resolution to it.</param>
/// <param name="ConvertToJpeg">Re-encode raw and JPEG 2000 continuous tone images as JPEG.</param>
/// <param name="Quality">JPEG quality for new encodings; null keeps a JPEG source's own.</param>
/// <param name="MinSsim">The perceptual gate, or null for none.</param>
/// <param name="Progressive">Also try progressive JPEG coding.</param>
internal sealed record ImageSettings(int? MaxDpi, bool ConvertToJpeg, int? Quality, double? MinSsim, bool Progressive)
{
    /// <summary>Raw RGB images may be reduced to a palette.</summary>
    public bool Quantize { get; init; }
}

/// <summary>The lossy image changes that won, for the statistics and the proof. A raw image may be both.</summary>
[Flags]
internal enum LossyKind
{
    None = 0,
    Downsampled = 1,
    ConvertedToJpeg = 2,
    Quantized = 4,
}

/// <summary>How an image's samples are coded under its general filters.</summary>
internal enum ImageCoding
{
    /// <summary>Samples, with nothing but general filters.</summary>
    Raw,

    /// <summary>DCTDecode.</summary>
    Jpeg,

    /// <summary>JPXDecode.</summary>
    Jpx,

    /// <summary>JBIG2Decode.</summary>
    Jbig2,

    /// <summary>CCITTFaxDecode.</summary>
    Ccitt,
}

/// <summary>
/// Everything a worker needs to change one image lossily, resolved up front.
/// </summary>
/// <param name="Width">Image width in samples.</param>
/// <param name="Height">Image height in samples.</param>
/// <param name="Components">Color components, 1, 3 or 4.</param>
/// <param name="Coding">The image codec under the general filters.</param>
/// <param name="ColorTransform">The DCTDecode /ColorTransform, when set.</param>
/// <param name="Target">Size to downsample to, or null to keep the size.</param>
/// <param name="MayConvert">Raw or JPEG 2000 samples may become a JPEG.</param>
internal sealed record ImagePlan(int Width, int Height, int Components, ImageCoding Coding, int? ColorTransform, (int Width, int Height)? Target, bool MayConvert)
{
    /// <summary>True when the data, after general filters, is a JPEG.</summary>
    public bool Jpeg => Coding == ImageCoding.Jpeg;

    /// <summary>Bits per sample: 1 for a bilevel image, else 8.</summary>
    public int Bits { get; init; } = 8;

    /// <summary>The CCITT parameters of CCITT data.</summary>
    public CcittParameters? Ccitt { get; init; }

    /// <summary>The decoded JBIG2Globals of JBIG2 data, when it has them.</summary>
    public byte[]? Globals { get; init; }

    /// <summary>The dictionary has no /BitsPerComponent, which a JPEG 2000 image may leave out and any other coding needs.</summary>
    public bool NeedsBits { get; init; }

    /// <summary>The image's color space, set when its samples may be reduced to a palette in that space.</summary>
    public PdfObject? PaletteBase { get; init; }
}

/// <summary>An image's size and coding, as the proof reads it from the dictionary.</summary>
internal sealed record ImageGeometry(int Width, int Height, int Components, ImageCoding Coding, int? ColorTransform)
{
    /// <summary>True when the data, after general filters, is a JPEG.</summary>
    public bool Jpeg => Coding == ImageCoding.Jpeg;

    /// <summary>Bits per sample: 1 for a bilevel image, else 8.</summary>
    public int Bits { get; init; } = 8;

    /// <summary>The CCITT parameters of CCITT data.</summary>
    public CcittParameters? Ccitt { get; init; }

    /// <summary>For an image the lossy pass wrote with a palette: its lookup table, whose colors are <see cref="Components"/> samples each.</summary>
    public byte[]? Palette { get; init; }

    /// <summary>Bits per index of an image with a <see cref="Palette"/>.</summary>
    public int PaletteBits { get; init; }

    /// <summary>The base of the Indexed color space of an image with a <see cref="Palette"/>.</summary>
    public PdfObject? PaletteBase { get; init; }

    /// <summary>The filter that decodes the coding, or null for samples.</summary>
    public string? Filter => LossyImages.Filter(Coding);
}

/// <summary>What the proof accepts beyond exact equivalence, by source object number.</summary>
/// <param name="MayRequantize">Accept a JPEG whose coefficients are exactly the source's requantized.</param>
/// <param name="MayResample">Accept an image downsampled or converted to JPEG, proven by SSIM.</param>
/// <param name="MinSsim">The perceptual gate that held, or null when none was set.</param>
internal sealed record ImageAllowance(Predicate<int> MayRequantize, Predicate<int> MayResample, double? MinSsim)
{
    public static ImageAllowance None { get; } = new(static _ => false, static _ => false, null);
}

/// <summary>A lossy candidate: encoded data with its filter, and the size it now has.</summary>
internal sealed record LossyCandidate(byte[] Data, PdfObject Filter, PdfObject? DecodeParms, int Width, int Height, LossyKind Kind)
{
    /// <summary>The Indexed color space a quantized image now needs.</summary>
    public PdfObject? ColorSpace { get; init; }

    /// <summary>The bits per index that color space needs.</summary>
    public int PaletteBits { get; init; }
}

/// <summary>
/// The lossy image profile: downsampling to a target resolution and conversion of continuous
/// tone images to JPEG, each behind the perceptual gate when one is set. The gate decodes every
/// candidate and compares its luminance with the reference by SSIM: the source itself, or for a
/// downsampled image the exact resample of the source, so the gate judges the encoding at the
/// size the image is delivered and never second-guesses the resolution target. Bilevel images
/// (raw, CCITT or JBIG2) are reduced by <see cref="BilevelResampler"/> and stay bilevel, coded
/// as Group 4 or Flate, both lossless at the new size. JPEG 2000 images, which the optimizer does
/// not write, become JPEG or, downsampled, Flate.
/// </summary>
internal static class LossyImages
{
    /// <summary>Only images drawn at more than this multiple of the target resolution are resampled.</summary>
    public const double DownsampleThreshold = 1.5;

    /// <summary>A lossy candidate must be at most this fraction of the best lossless encoding.</summary>
    public const double RequiredGain = 0.9;

    /// <summary>
    /// An image this small losslessly, in bytes, keeps its size and is not worth judging: a JPEG's
    /// headers alone are larger, and a palette's dictionary entry takes what it could save.
    /// </summary>
    public const int MinWorthwhileCost = 256;

    /// <summary>
    /// The plan for one image, or null when the pass must leave it alone: masks, images with a
    /// mask of their own that must match their size, and anything <see cref="Geometry"/> does not
    /// describe. Bilevel images are only ever downsampled.
    /// </summary>
    /// <param name="globals">Reads a JBIG2Globals stream, or returns null when it cannot.</param>
    public static ImagePlan? PlanFor(PdfFile file, PdfDictionary d, List<FilterSpec> filters, ImageSettings settings, bool jpegAllowed, PlacedSize? placed, Func<PdfObject?, byte[]?> globals)
    {
        if (!MayResample(file, d) || Geometry(file, d, filters) is not { } g) return null;
        if (g.Jpeg && !jpegAllowed) return null;

        (int, int)? target = settings.MaxDpi is { } dpi && placed is { } size && !HasMatteSoftMask(file, d) ? Target(g.Width, g.Height, size, dpi) : null;
        if (g.Bits == 1)
        {
            if (target is null) return null;
            byte[]? shared = null;
            if (g.Coding == ImageCoding.Jbig2 && file.Resolve(filters[^1].Parms) is PdfDictionary parms && file.Resolve(parms["JBIG2Globals"]) is not (null or PdfNull))
            {
                // Globals that cannot be read leave the page without the segments it refers to.
                shared = globals(parms["JBIG2Globals"]);
                if (shared is null) return null;
            }

            return new ImagePlan(g.Width, g.Height, 1, g.Coding, null, target, false) { Bits = 1, Ccitt = g.Ccitt, Globals = shared };
        }

        bool mayConvert = settings.ConvertToJpeg && g.Coding is ImageCoding.Raw or ImageCoding.Jpx && g.Components is 1 or 3 && settings.Quality is not null;
        bool mayQuantize = settings.Quantize && MayQuantize(file, d, g);
        if (target is null && !mayConvert && !mayQuantize) return null;
        return new ImagePlan(g.Width, g.Height, g.Components, g.Coding, g.ColorTransform, target, mayConvert)
        {
            NeedsBits = d["BitsPerComponent"] is null or PdfNull,
            PaletteBase = mayQuantize ? d["ColorSpace"] : null,
        };
    }

    /// <summary>
    /// True for an image a palette can stand in for as readers take it: raw 8 bit RGB, with no
    /// /Decode, which means something else for an index, and no /Interpolate, which readers apply
    /// to color images and may not apply to an Indexed one. A /Matte soft mask is left out too:
    /// the colors there are preblended, and undoing the matte needs them exactly.
    /// </summary>
    private static bool MayQuantize(PdfFile file, PdfDictionary d, ImageGeometry g) =>
        g is { Coding: ImageCoding.Raw, Components: 3, Bits: 8 } && !HasMatteSoftMask(file, d)
        && file.Resolve(d["Decode"]) is null or PdfNull && file.Resolve(d["Interpolate"]) is not PdfBoolean { Value: true };

    /// <summary>
    /// About the most memory the lossy pass holds at once for an image: JPEG 2000 decodes into 32
    /// bit planes, JPEG into 16 bit coefficients, and every coding into 8 bit samples that are
    /// resampled and encoded; a bilevel image keeps its bits and the coverage of its target.
    /// </summary>
    public static long Footprint(ImagePlan plan)
    {
        long samples = (long)plan.Width * plan.Height * plan.Components;
        if (plan.Bits == 1) return (samples / 2) + (plan.Target is { } t ? (long)t.Width * t.Height * 10 : 0);
        return samples * plan.Coding switch
        {
            ImageCoding.Jpx => 7,
            ImageCoding.Jpeg => 4,
            _ => 3,
        };
    }

    /// <summary>The filter that decodes a coding, or null for samples.</summary>
    public static string? Filter(ImageCoding coding) => coding switch
    {
        ImageCoding.Jpeg => "DCTDecode",
        ImageCoding.Jpx => "JPXDecode",
        ImageCoding.Jbig2 => "JBIG2Decode",
        ImageCoding.Ccitt => "CCITTFaxDecode",
        _ => null,
    };

    /// <summary>
    /// False for images the lossy pass never changes, whatever their format: stencil masks, and
    /// images with a /Mask, whose stencil or color key must line up with their samples.
    /// </summary>
    public static bool MayResample(PdfFile file, PdfDictionary d) =>
        file.Resolve(d["ImageMask"]) is not PdfBoolean { Value: true } && d["Mask"] is null or PdfNull;

    /// <summary>True when the image's soft mask has /Matte, so both must keep one size (ISO 32000-1, 11.6.5.3).</summary>
    public static bool HasMatteSoftMask(PdfFile file, PdfDictionary d) =>
        file.Resolve(d["SMask"]) is PdfStream mask && mask.Dictionary["Matte"] is not null and not PdfNull;

    /// <summary>True when the image has a color key mask, which matches exact sample values.</summary>
    public static bool HasColorKey(PdfFile file, PdfDictionary d) => file.Resolve(d["Mask"]) is PdfArray;

    /// <summary>
    /// What a check of another tool's output may accept: requantization only of JPEG images the
    /// optimizer would requantize, and resampling or conversion only of images the lossy pass may
    /// change, by the same exclusions. Masks and soft masks (anything another image uses as /Mask
    /// or /SMask), stencil masks, color keyed images, and for resampling also images with any
    /// /Mask or a /Matte soft mask, must match exactly.
    /// </summary>
    public static ImageAllowance AllowanceFor(PdfFile source, bool requantize, double? minSsim, CancellationToken ct)
    {
        if (!requantize && minSsim is null) return ImageAllowance.None;
        ObjectGraph graph = ObjectGraph.Collect(source, false, false, ct);
        var images = new List<(int Number, PdfDictionary Dictionary)>();
        var targets = new HashSet<int>();
        foreach (int number in graph.Order)
        {
            if (source.GetObject(number) is not PdfStream stream || stream.Dictionary.GetName("Subtype") != "Image") continue;
            images.Add((number, stream.Dictionary));
            if (stream.Dictionary["SMask"] is PdfReference soft) targets.Add(soft.Number);
            if (stream.Dictionary["Mask"] is PdfReference hard) targets.Add(hard.Number);
        }

        var requantizable = new HashSet<int>();
        var resamplable = new HashSet<int>();
        foreach ((int number, PdfDictionary d) in images)
        {
            if (targets.Contains(number) || source.Resolve(d["ImageMask"]) is PdfBoolean { Value: true } || HasColorKey(source, d)) continue;
            requantizable.Add(number);
            if (MayResample(source, d) && !HasMatteSoftMask(source, d)) resamplable.Add(number);
        }

        return new ImageAllowance(
            requantize ? requantizable.Contains : static _ => false,
            minSsim is null ? static _ => false : resamplable.Contains,
            minSsim);
    }

    /// <summary>
    /// The size that brings the denser axis down to <paramref name="dpi"/>, keeping the pixel
    /// aspect, or null when the image is not over the threshold or its placement is degenerate.
    /// </summary>
    internal static (int Width, int Height)? Target(int width, int height, PlacedSize placed, int dpi)
    {
        if (placed.Width <= 1e-6 || placed.Height <= 1e-6) return null;
        double scale = Math.Max(dpi * placed.Width / width, dpi * placed.Height / height);
        if (scale * DownsampleThreshold > 1) return null;
        int w = Math.Max(1, (int)Math.Ceiling(width * scale)), h = Math.Max(1, (int)Math.Ceiling(height * scale));
        return w < width && h < height ? (w, h) : null;
    }

    /// <summary>
    /// The lossy candidates for an image, gated. <paramref name="data"/> is the image after its
    /// general filters: samples, or the coded image.
    /// </summary>
    /// <param name="rejected">Set when a candidate failed the perceptual gate.</param>
    public static List<LossyCandidate> Candidates(ReadOnlySpan<byte> data, ImagePlan plan, ImageSettings settings, Flate level, bool imagePredictors, long decodeLimit, out bool rejected)
    {
        if (plan.Bits == 1) return BilevelCandidates(data, plan, level, decodeLimit, out rejected);

        rejected = false;
        var candidates = new List<LossyCandidate>();
        Raster? source = Source(data, plan, decodeLimit, out int? sourceQuality, out bool sourceSubsampled, out bool adobe);
        if (source is null) return candidates;

        Raster reference = plan.Target is { } t ? Resampler.Downsample(source, t.Width, t.Height) : source;
        LossyKind resized = plan.Target is null ? LossyKind.None : LossyKind.Downsampled;
        if (plan.Target is not null && !plan.Jpeg)
        {
            // Downsampled samples, still lossless on top of the resampling.
            (byte[] flate, PdfObject? parms) = Lossless(reference, level, imagePredictors);
            candidates.Add(new LossyCandidate(flate, new PdfName("FlateDecode"), parms, reference.Width, reference.Height, resized));
        }

        // A JPEG source stays a JPEG, never finer than it was. Raw samples become one when allowed
        // and photographic; so does a JPEG 2000 image, which also becomes one whenever it is
        // downsampled and a quality is set.
        int? quality = plan.Coding switch
        {
            ImageCoding.Jpeg => Math.Min(settings.Quality ?? 100, sourceQuality ?? 100),
            ImageCoding.Jpx when plan.Target is not null => settings.Quality,
            _ => plan.MayConvert && ImageClassifier.IsContinuousTone(reference) ? settings.Quality : null,
        };
        if (quality is { } q && (!plan.Jpeg || plan.Components != 4 || adobe))
        {
            bool subsample = plan.Jpeg ? sourceSubsampled : q < 90;
            byte[]? jpeg = EncodeJpeg(reference, q, subsample, settings.Progressive);
            if (jpeg is not null)
            {
                if (!Passes(reference, jpeg, plan.Components, settings.MinSsim, decodeLimit))
                {
                    rejected = true;
                }
                else
                {
                    LossyKind kind = plan.Jpeg ? resized : resized | LossyKind.ConvertedToJpeg;
                    candidates.Add(new LossyCandidate(jpeg, new PdfName("DCTDecode"), null, reference.Width, reference.Height, kind));
                }
            }
        }

        if (plan.PaletteBase is { } paletteBase)
        {
            if (Quantized(reference, paletteBase, resized, settings.MinSsim, level, imagePredictors) is { } quantized) candidates.Add(quantized);
            else if (candidates.Count == 0) rejected = true;
        }

        return candidates;
    }

    /// <summary>
    /// The smallest palette version of an image that stays faithful, or null when even the largest
    /// palette does not. Sizes are ranked with a cheap pass; only the winner gets the configured effort.
    /// </summary>
    private static LossyCandidate? Quantized(Raster reference, PdfObject paletteBase, LossyKind resized, double? gate, Flate level, bool imagePredictors)
    {
        ColorQuantizer quantizer = ColorQuantizer.For(reference);
        QuantizedImage? best = null;
        int bestSize = int.MaxValue;
        foreach (int colors in PaletteSizes)
        {
            // The palette already found fits this size too, so this size would find the same one.
            if (best is not null && quantizer.IsExact(best.Colors) && best.Colors <= colors) continue;

            QuantizedImage image = quantizer.Reduce(colors);

            // An exact palette has nothing to judge: every pixel is what it was.
            if (!quantizer.IsExact(colors) && PaletteFailure(reference, image.Expand(reference.Width, reference.Height), gate, coarse: false) is not null) break;
            int size = (image.Colors * 3) + StreamCodec.Deflate(image.PackedRows(reference.Width, reference.Height), System.IO.Compression.CompressionLevel.Fastest).Length;
            if (size < bestSize)
            {
                bestSize = size;
                best = image;
            }
        }

        // The coarse comparison resamples both images, so only the winner pays for it, as the proof will.
        if (best is null || PaletteFailure(reference, best.Expand(reference.Width, reference.Height), gate, coarse: true) is not null) return null;
        byte[] packed = best.PackedRows(reference.Width, reference.Height);
        (byte[] data, PdfObject? parms) = LosslessPacked(packed, reference.Width, reference.Height, best.Bits, level, imagePredictors);
        var space = new PdfArray();
        space.Add(new PdfName("Indexed"));
        space.Add(paletteBase);
        space.Add(PdfNumber.FromInteger(best.Colors - 1));
        space.Add(new PdfString(best.Palette));
        return new LossyCandidate(data, new PdfName("FlateDecode"), parms, reference.Width, reference.Height, resized | LossyKind.Quantized)
        {
            ColorSpace = space,
            PaletteBits = best.Bits,
        };
    }

    /// <summary>
    /// Why a palette version is not faithful to its reference, or null when it is. Mean color and
    /// coarse scale as for any lossy change, luminance SSIM at least the gate, and deviation bounds
    /// that see what luminance cannot, a color drifting at the same brightness.
    /// </summary>
    /// <param name="coarse">Also compare the images at coarse scale, the costly check.</param>
    private static string? PaletteFailure(Raster reference, Raster after, double? gate, bool coarse)
    {
        // How far pixels moved on their worst channel, as a histogram: a sort would cost too much.
        var histogram = new long[256];
        byte[] x = reference.Samples, y = after.Samples;
        long sum = 0;
        for (int i = 0; i + 2 < x.Length; i += 3)
        {
            int d = Math.Max(Math.Abs(x[i] - y[i]), Math.Max(Math.Abs(x[i + 1] - y[i + 1]), Math.Abs(x[i + 2] - y[i + 2])));
            histogram[d]++;
            sum += d;
        }

        long pixels = x.Length / 3, seen = 0;
        if (sum > MaxPaletteMeanDeviation * pixels) return "quantized image moved its colors too far on average";
        for (int d = 255; d > MaxPaletteDeviation; d--)
        {
            seen += histogram[d];
            if (seen * 100 > pixels) return "quantized image moved too many pixels too far";
        }

        if (Ssim.MeanShift(reference, after) > MaxMeanShift) return "quantized image changed its colors";
        double ssim = Ssim.Compare(reference, after);
        double floor = gate ?? PaletteMinSsim;
        if (ssim < floor) return Compat.Format("quantized image has SSIM {0:F4}, below {1}", ssim, floor);
        return coarse && CoarseDifference(reference, after) > MaxCoarseDifference ? "quantized image differs from its source at coarse scale" : null;
    }

    /// <summary>
    /// A bilevel image reduced to its target by <see cref="BilevelResampler"/> and coded
    /// losslessly, as Group 4 and as Flate. The result is exactly the reference the proof
    /// computes, so the gate has nothing to judge.
    /// </summary>
    private static List<LossyCandidate> BilevelCandidates(ReadOnlySpan<byte> data, ImagePlan plan, Flate level, long decodeLimit, out bool rejected)
    {
        rejected = false;
        var candidates = new List<LossyCandidate>();
        (int tw, int th) = plan.Target!.Value;
        byte[]? pixels = BilevelPixels(data, plan.Coding, plan.Ccitt, plan.Globals, plan.Width, plan.Height, decodeLimit);
        if (pixels is null || (long)tw * th * sizeof(double) > decodeLimit) return candidates;

        double[] coverage = BilevelResampler.Coverage(pixels, plan.Width, plan.Height, tw, th);
        byte[] reduced = BilevelResampler.Threshold(coverage, tw, th, BilevelResampler.OnesAreForeground(pixels, plan.Width, plan.Height));
        candidates.Add(new LossyCandidate(level.Encode(reduced), new PdfName("FlateDecode"), null, tw, th, LossyKind.Downsampled));
        if (BilevelImages.Group4(reduced, tw, th, reduced.Length) is { } g4)
        {
            candidates.Add(new LossyCandidate(g4.Data, new PdfName("CCITTFaxDecode"), g4.Parms, tw, th, LossyKind.Downsampled));
        }

        return candidates;
    }

    /// <summary>
    /// The pixels of a bilevel image as packed rows, 0 where the sample is 0: samples as they are,
    /// CCITT data decoded without a flaw, JBIG2 data decoded and turned to samples (T.88 codes black
    /// as 1, the filter's samples have 0 for black). Null when that fails or exceeds the limit.
    /// </summary>
    private static byte[]? BilevelPixels(ReadOnlySpan<byte> data, ImageCoding coding, CcittParameters? ccitt, byte[]? globals, int width, int height, long decodeLimit)
    {
        switch (coding)
        {
            case ImageCoding.Raw:
                return BilevelImages.Pixels(data, null, width, height, decodeLimit);
            case ImageCoding.Ccitt when ccitt is not null:
                return BilevelImages.Pixels(data, ccitt, width, height, decodeLimit);
            case ImageCoding.Jbig2:
                if ((long)width * height > Math.Min(decodeLimit, Compat.MaxArrayLength)) return null;
                try
                {
                    byte[] page = Jbig2Decoder.Decode(data.ToArray(), globals, width, height);
                    for (int i = 0; i < page.Length; i++) page[i] = (byte)~page[i];
                    return page;
                }
                catch (Jbig2FormatException)
                {
                    return null;
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// True when the decoded <paramref name="jpeg"/> keeps the proof's color and coarse scale
    /// bounds against <paramref name="reference"/>, and with a gate its SSIM is at least the gate.
    /// The proof checks the same again, so a candidate that would fail it is never chosen.
    /// </summary>
    public static bool Passes(Raster reference, ReadOnlySpan<byte> jpeg, int components, double? gate, long decodeLimit)
    {
        Raster decoded;
        try
        {
            decoded = JpegDecoder.Decode(jpeg, null, decodeLimit);
        }
        catch (JpegFormatException)
        {
            return false;
        }

        return decoded.Channels == components && decoded.Width == reference.Width && decoded.Height == reference.Height
            && Ssim.MeanShift(reference, decoded) <= MaxMeanShift && CoarseDifference(reference, decoded) <= MaxCoarseDifference
            && (gate is not { } g || Ssim.Compare(reference, decoded) >= g);
    }

    /// <summary>
    /// Requantized JPEG data against its source, both decoded: whether the gate holds.
    /// </summary>
    public static bool RequantizationPasses(ReadOnlySpan<byte> source, ReadOnlySpan<byte> requantized, int? colorTransform, double gate, long decodeLimit)
    {
        try
        {
            Raster a = JpegDecoder.Decode(source, colorTransform, decodeLimit);
            Raster b = JpegDecoder.Decode(requantized, colorTransform, decodeLimit);
            return a.Width == b.Width && a.Height == b.Height && a.Channels == b.Channels && Ssim.Compare(a, b) >= gate;
        }
        catch (JpegFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// What the proof needs to know about an image to decode it, or null when it is not one the
    /// lossy pass handles: 8 bit gray, RGB or CMYK as samples or a JPEG; gray or RGB JPEG 2000
    /// with its own /ColorSpace, no /Decode, no opacity of its own (/SMaskInData) and /BitsPerComponent
    /// 8 or none; or a bilevel image (see <see cref="BilevelImages.Size"/>) as samples, CCITT data
    /// every reader decodes alike, or JBIG2. General filters may come first. Stencil masks are not
    /// described.
    /// </summary>
    public static ImageGeometry? Geometry(PdfFile file, PdfDictionary d, List<FilterSpec> filters)
    {
        if (d.GetName("Subtype") != "Image" || file.Resolve(d["ImageMask"]) is PdfBoolean { Value: true }) return null;
        string? codec = filters.Count > 0 && !StreamCodec.IsDecodable(filters[^1].Name) ? filters[^1].Name : null;
        ImageCoding? coding = codec switch
        {
            null => ImageCoding.Raw,
            "DCTDecode" => ImageCoding.Jpeg,
            "JPXDecode" => ImageCoding.Jpx,
            "JBIG2Decode" => ImageCoding.Jbig2,
            "CCITTFaxDecode" => ImageCoding.Ccitt,
            _ => null,
        };
        if (coding is not { } c) return null;
        for (int i = 0; i < filters.Count - (codec is null ? 0 : 1); i++)
        {
            if (!StreamCodec.IsDecodable(filters[i].Name)) return null;
        }

        if (BilevelImages.Size(file, d) is { } bilevel)
        {
            if (c is not (ImageCoding.Raw or ImageCoding.Ccitt or ImageCoding.Jbig2)) return null;
            CcittParameters? ccitt = null;
            if (c == ImageCoding.Ccitt && (ccitt = BilevelImages.Parameters(filters[^1].Parms, bilevel.Width, bilevel.Height, file.Resolve)) is null) return null;
            return new ImageGeometry(bilevel.Width, bilevel.Height, 1, c, null) { Bits = 1, Ccitt = ccitt };
        }

        if (c is ImageCoding.Jbig2 or ImageCoding.Ccitt) return null;
        int width = Int(file, d["Width"]), height = Int(file, d["Height"]), components = Components(file, d["ColorSpace"]);
        if (width <= 0 || height <= 0 || (long)width * height > ImageSamples.MaxPixels) return null;
        if (c == ImageCoding.Jpx)
        {
            // The data's own bit depth and opacity count for JPEG 2000 unless the dictionary says otherwise.
            if (components is not (1 or 3) || d["Decode"] is not (null or PdfNull)) return null;
            if (file.Resolve(d["SMaskInData"]) is not (null or PdfNull or PdfNumber { IsInteger: true, IntegerValue: 0 })) return null;
            if (d["BitsPerComponent"] is not (null or PdfNull) && Int(file, d["BitsPerComponent"]) != 8) return null;
        }
        else if (Int(file, d["BitsPerComponent"]) != 8 || components is not (1 or 3 or 4))
        {
            return null;
        }

        int? colorTransform = c == ImageCoding.Jpeg && file.Resolve(filters[^1].Parms) is PdfDictionary parms &&
            file.Resolve(parms["ColorTransform"]) is PdfNumber { IsInteger: true } t && t.IntegerValue is 0 or 1
                ? (int)t.IntegerValue
                : null;
        return new ImageGeometry(width, height, components, c, colorTransform);
    }

    /// <summary>
    /// Proves a resampled or converted image against its source: same components, a size no larger
    /// and of the same aspect, a mean color within <see cref="MaxMeanShift"/> per channel and
    /// luminance SSIM at least <paramref name="gate"/> against the source resampled to the new size.
    /// Without a gate the floor is <see cref="StructuralFloor"/>, which only catches broken output.
    /// A bilevel image must stay bilevel; its reference is the reduction of the source by
    /// <see cref="BilevelResampler"/>, as the Lanczos resample is for other images.
    /// </summary>
    /// <param name="source">The source image after its general filters: samples, or the coded image.</param>
    /// <param name="output">The output image after its general filters.</param>
    /// <param name="globalsA">The source's decoded JBIG2Globals, if any.</param>
    /// <param name="globalsB">The output's decoded JBIG2Globals, if any.</param>
    /// <returns>Null when the proof holds, otherwise the reason it does not.</returns>
    public static string? Verify(ReadOnlySpan<byte> source, ImageGeometry a, byte[]? globalsA, ReadOnlySpan<byte> output, ImageGeometry b, byte[]? globalsB, double? gate, long decodeLimit)
    {
        if (a.Components != b.Components) return "resampled image changed its components";
        if (a.Bits != b.Bits) return "resampled image changed its bit depth";
        if (b.Width > a.Width || b.Height > a.Height) return "resampled image is larger than its source";
        if (Math.Abs(((long)b.Width * a.Height) - ((long)b.Height * a.Width)) >= Math.Max(a.Width, a.Height)) return "resampled image changed its aspect ratio";
        double floor = gate ?? StructuralFloor;
        Raster? reference, after;
        if (a.Bits == 1)
        {
            (reference, after) = DecodeBilevel(source, a, globalsA, output, b, globalsB, decodeLimit);
        }
        else
        {
            Raster? before = Decode(source, a, decodeLimit);
            after = Decode(output, b, decodeLimit);
            reference = before is null || after is null ? before
                : after.Width == before.Width && after.Height == before.Height ? before : Resampler.Downsample(before, after.Width, after.Height);
        }

        if (reference is null) return "source image cannot be decoded";
        if (after is null) return "resampled image cannot be decoded";
        if (b.Palette is not null) return PaletteFailure(reference, after, gate, coarse: true);
        if (Ssim.MeanShift(reference, after) > MaxMeanShift) return "resampled image changed its colors";

        // SSIM stays well above zero on flat, noisy windows whatever the content, so the picture
        // as a whole is also compared at a scale every faithful encoding keeps: a mirrored,
        // shifted or swapped image fails here.
        if (CoarseDifference(reference, after) > MaxCoarseDifference) return "resampled image differs from its source at coarse scale";
        double ssim = Ssim.Compare(reference, after);
        return ssim >= floor ? null : Compat.Format("resampled image has SSIM {0:F4}, below {1}", ssim, floor);
    }

    /// <summary>
    /// A bilevel source reduced to the output's size by <see cref="BilevelResampler"/>, and the
    /// output, both as 8 bit gray (255 for a 1 bit). The reference is null when the source does
    /// not decode, the output when it does not.
    /// </summary>
    private static (Raster? Reference, Raster? Output) DecodeBilevel(ReadOnlySpan<byte> source, ImageGeometry a, byte[]? globalsA, ReadOnlySpan<byte> output, ImageGeometry b, byte[]? globalsB, long decodeLimit)
    {
        byte[]? before = BilevelPixels(source, a.Coding, a.Ccitt, globalsA, a.Width, a.Height, decodeLimit);
        byte[]? after = BilevelPixels(output, b.Coding, b.Ccitt, globalsB, b.Width, b.Height, decodeLimit);
        if (before is null || (long)b.Width * b.Height * sizeof(double) > decodeLimit) return (null, null);
        double[] coverage = BilevelResampler.Coverage(before, a.Width, a.Height, b.Width, b.Height);
        byte[] reference = BilevelResampler.Threshold(coverage, b.Width, b.Height, BilevelResampler.OnesAreForeground(before, a.Width, a.Height));
        return (BilevelResampler.Gray(reference, b.Width, b.Height), after is null ? null : BilevelResampler.Gray(after, b.Width, b.Height));
    }

    /// <summary>SSIM the proof requires of a resampled image when no gate is set.</summary>
    public const double StructuralFloor = 0.5;

    /// <summary>SSIM a palette version must keep when no gate is set: hard to tell from the source.</summary>
    public const double PaletteMinSsim = 0.97;

    /// <summary>Largest mean, over pixels, of a pixel's change on its worst channel, for a palette version.</summary>
    public const double MaxPaletteMeanDeviation = 8.0;

    /// <summary>At most 1% of pixels may change by more than this on their worst channel, for a palette version.</summary>
    public const int MaxPaletteDeviation = 32;

    /// <summary>Palette sizes tried, largest first; the search stops at the first that is not faithful.</summary>
    private static readonly int[] PaletteSizes = [256, 64, 16, 4];

    /// <summary>Largest shift of a channel's mean, in sample steps, the proof accepts.</summary>
    public const double MaxMeanShift = 4.0;

    /// <summary>Largest mean absolute difference, in sample steps, between 32 pixel thumbnails of the two images.</summary>
    public const double MaxCoarseDifference = 12.0;

    /// <summary>Mean absolute difference per sample of both images reduced to at most 32 pixels across.</summary>
    private static double CoarseDifference(Raster a, Raster b)
    {
        double scale = Math.Min(1.0, 32.0 / Math.Max(a.Width, a.Height));
        int w = Math.Max(1, (int)Math.Round(a.Width * scale)), h = Math.Max(1, (int)Math.Round(a.Height * scale));
        Raster ta = Resampler.Downsample(a, w, h), tb = Resampler.Downsample(b, w, h);
        long total = 0;
        for (int i = 0; i < ta.Samples.Length; i++) total += Math.Abs(ta.Samples[i] - tb.Samples[i]);
        return (double)total / ta.Samples.Length;
    }

    private static Raster? Decode(ReadOnlySpan<byte> data, ImageGeometry g, long decodeLimit)
    {
        switch (g.Coding)
        {
            case ImageCoding.Raw when g.Palette is not null:
                return ExpandPalette(data, g, decodeLimit);
            case ImageCoding.Raw:
            {
                long size = (long)g.Width * g.Height * g.Components;
                return data.Length < size || size > decodeLimit ? null : new Raster(g.Width, g.Height, g.Components, data.Slice(0, (int)size).ToArray());
            }

            case ImageCoding.Jpeg:
                try
                {
                    Raster r = JpegDecoder.Decode(data, g.ColorTransform, decodeLimit);
                    return r.Width == g.Width && r.Height == g.Height && r.Channels == g.Components ? r : null;
                }
                catch (JpegFormatException)
                {
                    return null;
                }

            case ImageCoding.Jpx:
                return DecodeJpx(data, g.Width, g.Height, g.Components, decodeLimit);
            default:
                return null;
        }
    }

    /// <summary>
    /// Index samples expanded through the image's palette. Null when the data is short, an index
    /// is outside the lookup table, or the result would exceed the limit.
    /// </summary>
    private static Raster? ExpandPalette(ReadOnlySpan<byte> data, ImageGeometry g, long decodeLimit)
    {
        byte[] palette = g.Palette!;
        int bits = g.PaletteBits;
        long rowBytes = (((long)g.Width * bits) + 7) / 8;
        long size = (long)g.Width * g.Height * g.Components;
        if (data.Length < rowBytes * g.Height || size > decodeLimit) return null;

        int colors = palette.Length / 3, mask = (1 << bits) - 1;
        var samples = new byte[size];
        int o = 0;
        for (int y = 0; y < g.Height; y++)
        {
            int row = (int)(y * rowBytes);
            for (int x = 0; x < g.Width; x++, o += 3)
            {
                int bit = x * bits;
                int index = (data[row + (bit >> 3)] >> (8 - bits - (bit & 7))) & mask;
                if (index >= colors) return null;
                samples[o] = palette[index * 3];
                samples[o + 1] = palette[(index * 3) + 1];
                samples[o + 2] = palette[(index * 3) + 2];
            }
        }

        return new Raster(g.Width, g.Height, g.Components, samples);
    }

    /// <summary>
    /// A JPEG 2000 image as 8 bit samples in its dictionary's color space, as readers take it
    /// (ISO 32000-1, 7.4.9). Null unless the data has exactly that many color channels, no
    /// opacity channel, the dictionary's size, and colors that are not sYCC (which a dictionary
    /// color space would leave undefined).
    /// </summary>
    private static Raster? DecodeJpx(ReadOnlySpan<byte> data, int width, int height, int components, long decodeLimit)
    {
        try
        {
            Jpeg2000Image image = Jpeg2000Decoder.Decode(data, decodeLimit);
            if (image.Width != width || image.Height != height || image.ColorSpace == 18) return null;
            if (image.Channels.Count != components || image.Channels.Any(c => c.Opacity)) return null;
            return (long)width * height * components > decodeLimit ? null : image.ToRaster(components, scale: true);
        }
        catch (Jpeg2000FormatException)
        {
            return null;
        }
    }

    private static Raster? Source(ReadOnlySpan<byte> data, ImagePlan plan, long decodeLimit, out int? quality, out bool subsampled, out bool adobe)
    {
        quality = null;
        subsampled = false;
        adobe = false;
        if (plan.Coding == ImageCoding.Raw)
        {
            long size = (long)plan.Width * plan.Height * plan.Components;
            if (data.Length < size || size > decodeLimit) return null;
            return new Raster(plan.Width, plan.Height, plan.Components, data.Slice(0, (int)size).ToArray());
        }

        if (plan.Coding == ImageCoding.Jpx) return DecodeJpx(data, plan.Width, plan.Height, plan.Components, decodeLimit);

        try
        {
            using (JpegImage header = JpegReader.Read(data, decodeLimit))
            {
                if (header.Components.Length != plan.Components || header.Width != plan.Width || header.Height != plan.Height) return null;
                quality = JpegQuantization.EstimateQuality(header.Components[0].Quantization);
                subsampled = header.Components.Length == 3 && (header.MaxHorizontal > 1 || header.MaxVertical > 1);
                adobe = header.AdobeTransform is not null;

                // Four channels re-encode as stored under transform 0, which matches only Adobe CMYK
                // (transform 0 already) or YCCK (which the decoder turns into the same inverted CMYK).
                if (header.Components.Length == 4 && header.AdobeTransform is not (0 or 2)) return null;
            }

            return JpegDecoder.Decode(data, plan.ColorTransform, decodeLimit);
        }
        catch (JpegFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A JBIG2Globals stream fully decoded, or null when it is not a stream or does not decode
    /// to plain data within <paramref name="decodeLimit"/>.
    /// </summary>
    public static byte[]? ReadGlobals(PdfFile file, PdfObject? globals, long decodeLimit)
    {
        if (file.Resolve(globals) is not PdfStream stream) return null;
        try
        {
            using DecodedStream decoded = StreamCodec.Decode(stream.Data, StreamCodec.GetFilters(stream.Dictionary, file.Resolve), DecodeDepth.Full, decodeLimit, file.Resolve);
            return decoded.Remaining.Count == 0 && decoded.PendingPredictor is null ? decoded.Data.ToArray() : null;
        }
        catch (StreamDecodeException)
        {
            return null;
        }
    }

    private static byte[]? EncodeJpeg(Raster raster, int quality, bool subsample, bool progressive)
    {
        using JpegImage image = JpegEncoder.Encode(raster, quality, subsample);
        return JpegWriter.TryWriteSmallest(image, raster.Samples.Length / 4, progressive);
    }

    /// <summary>
    /// What the proof needs to read an image the lossy pass gave a palette, or null when it is not
    /// such an image: samples of 1, 2, 4 or 8 bits under general filters only, an Indexed color
    /// space over 3 components with its lookup table in a string, and no /Decode or /Interpolate,
    /// which the pass rules out on its sources as well.
    /// </summary>
    public static ImageGeometry? PaletteGeometry(PdfFile file, PdfDictionary d, List<FilterSpec> filters)
    {
        if (d.GetName("Subtype") != "Image" || file.Resolve(d["ImageMask"]) is PdfBoolean { Value: true }) return null;
        foreach (FilterSpec f in filters)
        {
            if (!StreamCodec.IsDecodable(f.Name)) return null;
        }

        if (file.Resolve(d["Decode"]) is not (null or PdfNull) || file.Resolve(d["Interpolate"]) is PdfBoolean { Value: true }) return null;
        if (file.Resolve(d["ColorSpace"]) is not PdfArray { Count: 4 } space || file.Resolve(space[0]) is not PdfName { Value: "Indexed" or "I" }) return null;
        if (file.Resolve(space[3]) is not PdfString lookup || Components(file, space[1]) != 3) return null;
        int width = Int(file, d["Width"]), height = Int(file, d["Height"]), bits = Int(file, d["BitsPerComponent"]);
        if (width <= 0 || height <= 0 || (long)width * height > ImageSamples.MaxPixels || bits is not (1 or 2 or 4 or 8)) return null;
        int high = file.Resolve(space[2]) is PdfNumber { IsInteger: true, IntegerValue: >= 0 and < 256 } h ? (int)h.IntegerValue : -1;
        if (high < 0 || lookup.Value.Length < (high + 1) * 3) return null;
        return new ImageGeometry(width, height, 3, ImageCoding.Raw, null)
        {
            Palette = lookup.Value.AsSpan(0, (high + 1) * 3).ToArray(),
            PaletteBits = bits,
            PaletteBase = space[1],
        };
    }

    /// <summary>Index rows under Flate, with or without PNG prediction, whichever is smaller.</summary>
    private static (byte[] Data, PdfObject? Parms) LosslessPacked(byte[] packed, int width, int height, int bits, Flate flate, bool imagePredictors)
    {
        if (!imagePredictors || height < 2) return (flate.Encode(packed), null);
        var layout = new PredictorParams(15, 1, bits, width);
        using BoundedBuffer predicted = Predictors.EncodePng(packed, layout);
        bool usePredictor = StreamCodec.Deflate(predicted.Span, System.IO.Compression.CompressionLevel.Fastest).Length
            < StreamCodec.Deflate(packed, System.IO.Compression.CompressionLevel.Fastest).Length;
        return usePredictor ? (flate.Encode(predicted.Span), StreamCodec.PredictorDictionary(layout)) : (flate.Encode(packed), null);
    }

    /// <summary>Flate with or without PNG prediction, whichever is smaller.</summary>
    private static (byte[] Data, PdfObject? Parms) Lossless(Raster raster, Flate flate, bool imagePredictors)
    {
        if (!imagePredictors || raster.Height < 2) return (flate.Encode(raster.Samples), null);

        // Rank with a cheap pass; only the winner gets the configured effort.
        var layout = new PredictorParams(15, raster.Channels, 8, raster.Width);
        using BoundedBuffer predicted = Predictors.EncodePng(raster.Samples, layout);
        bool usePredictor = StreamCodec.Deflate(predicted.Span, System.IO.Compression.CompressionLevel.Fastest).Length
            < StreamCodec.Deflate(raster.Samples, System.IO.Compression.CompressionLevel.Fastest).Length;
        return usePredictor ? (flate.Encode(predicted.Span), StreamCodec.PredictorDictionary(layout)) : (flate.Encode(raster.Samples), null);
    }

    private static int Components(PdfFile file, PdfObject? colorSpace)
    {
        PdfObject? cs = file.Resolve(colorSpace);
        string? family = cs switch
        {
            PdfName n => n.Value,
            PdfArray { Count: > 0 } a => (file.Resolve(a[0]) as PdfName)?.Value,
            _ => null,
        };
        return family switch
        {
            "DeviceGray" or "G" or "CalGray" => 1,
            "DeviceRGB" or "RGB" or "CalRGB" => 3,
            "DeviceCMYK" or "CMYK" => 4,
            "ICCBased" when cs is PdfArray { Count: > 1 } a && file.Resolve(a[1]) is PdfStream icc
                && file.Resolve(icc.Dictionary["N"]) is PdfNumber { IsInteger: true } n && n.IntegerValue is 1 or 3 or 4 => (int)n.IntegerValue,
            _ => 0,
        };
    }

    private static int Int(PdfFile file, PdfObject? o) =>
        file.Resolve(o) is PdfNumber { IsInteger: true } n && n.IntegerValue is > 0 and < int.MaxValue ? (int)n.IntegerValue : 0;
}
