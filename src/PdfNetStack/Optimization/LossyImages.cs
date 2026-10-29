using PdfNetStack.Filters;
using PdfNetStack.Imaging;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>What the lossy image pass may do. Null <see cref="MaxDpi"/> and false <see cref="ConvertToJpeg"/> turn it off.</summary>
/// <param name="MaxDpi">Downsample images drawn above 1.5 times this resolution to it.</param>
/// <param name="ConvertToJpeg">Re-encode raw continuous tone images as JPEG.</param>
/// <param name="Quality">JPEG quality for new encodings; null keeps a JPEG source's own.</param>
/// <param name="MinSsim">The perceptual gate, or null for none.</param>
/// <param name="Progressive">Also try progressive JPEG coding.</param>
internal sealed record ImageSettings(int? MaxDpi, bool ConvertToJpeg, int? Quality, double? MinSsim, bool Progressive);

/// <summary>The lossy image changes that won, for the statistics and the proof. A raw image may be both.</summary>
[Flags]
internal enum LossyKind
{
    None = 0,
    Downsampled = 1,
    ConvertedToJpeg = 2,
}

/// <summary>
/// Everything a worker needs to change one image lossily, resolved up front.
/// </summary>
/// <param name="Width">Image width in samples.</param>
/// <param name="Height">Image height in samples.</param>
/// <param name="Components">Color components, 1, 3 or 4.</param>
/// <param name="Jpeg">True when the data, after general filters, is a JPEG.</param>
/// <param name="ColorTransform">The DCTDecode /ColorTransform, when set.</param>
/// <param name="Target">Size to downsample to, or null to keep the size.</param>
/// <param name="MayConvert">Raw samples may become a JPEG.</param>
internal sealed record ImagePlan(int Width, int Height, int Components, bool Jpeg, int? ColorTransform, (int Width, int Height)? Target, bool MayConvert);

/// <summary>An image's size and coding, as the proof reads it from the dictionary.</summary>
internal sealed record ImageGeometry(int Width, int Height, int Components, bool Jpeg, int? ColorTransform);

/// <summary>What the proof accepts beyond exact equivalence, by source object number.</summary>
/// <param name="MayRequantize">Accept a JPEG whose coefficients are exactly the source's requantized.</param>
/// <param name="MayResample">Accept an image downsampled or converted to JPEG, proven by SSIM.</param>
/// <param name="MinSsim">The perceptual gate that held, or null when none was set.</param>
internal sealed record ImageAllowance(Predicate<int> MayRequantize, Predicate<int> MayResample, double? MinSsim)
{
    public static ImageAllowance None { get; } = new(static _ => false, static _ => false, null);
}

/// <summary>A lossy candidate: encoded data with its filter, and the size it now has.</summary>
internal sealed record LossyCandidate(byte[] Data, PdfObject Filter, PdfObject? DecodeParms, int Width, int Height, LossyKind Kind);

/// <summary>
/// The lossy image profile: downsampling to a target resolution and conversion of continuous
/// tone images to JPEG, each behind the perceptual gate when one is set. The gate decodes every
/// candidate and compares its luminance with the reference by SSIM: the source itself, or for a
/// downsampled image the exact resample of the source, so the gate judges the encoding at the
/// size the image is delivered and never second-guesses the resolution target.
/// </summary>
internal static class LossyImages
{
    /// <summary>Only images drawn at more than this multiple of the target resolution are resampled.</summary>
    public const double DownsampleThreshold = 1.5;

    /// <summary>A lossy candidate must be at most this fraction of the best lossless encoding.</summary>
    public const double RequiredGain = 0.9;

    /// <summary>
    /// The plan for one image, or null when the pass must leave it alone: masks, images with a
    /// mask of their own that must match their size, anything not 8 bits per component, indexed,
    /// Lab, separation and DeviceN images, and encodings other than raw samples and JPEG.
    /// </summary>
    public static ImagePlan? PlanFor(PdfFile file, PdfDictionary d, List<FilterSpec> filters, ImageSettings settings, bool jpegAllowed, PlacedSize? placed)
    {
        if (d.GetName("Subtype") != "Image" || !MayResample(file, d)) return null;
        if (Int(file, d["BitsPerComponent"]) != 8) return null;
        int width = Int(file, d["Width"]), height = Int(file, d["Height"]);
        if (width <= 0 || height <= 0 || (long)width * height > ImageSamples.MaxPixels) return null;

        int components = Components(file, d["ColorSpace"]);
        if (components is not (1 or 3 or 4)) return null;

        bool jpeg = filters.Count > 0 && filters[^1].Name == "DCTDecode";
        for (int i = 0; i < filters.Count - (jpeg ? 1 : 0); i++)
        {
            if (!StreamCodec.IsDecodable(filters[i].Name)) return null;
        }

        if (jpeg && !jpegAllowed) return null;
        int? colorTransform = jpeg && file.Resolve(filters[^1].Parms) is PdfDictionary parms &&
            file.Resolve(parms["ColorTransform"]) is PdfNumber { IsInteger: true } t && t.IntegerValue is 0 or 1
                ? (int)t.IntegerValue
                : null;

        (int, int)? target = settings.MaxDpi is { } dpi && placed is { } size && !HasMatteSoftMask(file, d) ? Target(width, height, size, dpi) : null;
        bool mayConvert = settings.ConvertToJpeg && !jpeg && components is 1 or 3 && settings.Quality is not null;
        if (target is null && !mayConvert) return null;
        return new ImagePlan(width, height, components, jpeg, colorTransform, target, mayConvert);
    }

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
    /// general filters: samples, or a JPEG.
    /// </summary>
    /// <param name="rejected">Set when a candidate failed the perceptual gate.</param>
    public static List<LossyCandidate> Candidates(ReadOnlySpan<byte> data, ImagePlan plan, ImageSettings settings, Flate level, bool imagePredictors, long decodeLimit, out bool rejected)
    {
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

        // A JPEG source stays a JPEG, never finer than it was; raw samples become one when allowed and photographic.
        int? quality = plan.Jpeg
            ? Math.Min(settings.Quality ?? 100, sourceQuality ?? 100)
            : plan.MayConvert && ImageClassifier.IsContinuousTone(reference) ? settings.Quality : null;
        if (quality is { } q && (!plan.Jpeg || plan.Components != 4 || adobe))
        {
            bool subsample = plan.Jpeg ? sourceSubsampled : q < 90;
            byte[]? jpeg = EncodeJpeg(reference, q, subsample, settings.Progressive);
            if (jpeg is not null)
            {
                if (settings.MinSsim is { } gate && !Passes(reference, jpeg, plan.Components, gate, decodeLimit))
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

        return candidates;
    }

    /// <summary>True when the decoded <paramref name="jpeg"/> matches <paramref name="reference"/> by SSIM at least <paramref name="gate"/>.</summary>
    public static bool Passes(Raster reference, ReadOnlySpan<byte> jpeg, int components, double gate, long decodeLimit)
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
            && Ssim.Compare(reference, decoded) >= gate;
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
    /// lossy pass handles (8 bit gray, RGB or CMYK; raw samples or a JPEG under general filters).
    /// </summary>
    public static ImageGeometry? Geometry(PdfFile file, PdfDictionary d, List<FilterSpec> filters)
    {
        if (d.GetName("Subtype") != "Image" || Int(file, d["BitsPerComponent"]) != 8) return null;
        int width = Int(file, d["Width"]), height = Int(file, d["Height"]), components = Components(file, d["ColorSpace"]);
        if (width <= 0 || height <= 0 || (long)width * height > ImageSamples.MaxPixels || components is not (1 or 3 or 4)) return null;
        bool jpeg = filters.Count > 0 && filters[^1].Name == "DCTDecode";
        int? colorTransform = jpeg && file.Resolve(filters[^1].Parms) is PdfDictionary parms &&
            file.Resolve(parms["ColorTransform"]) is PdfNumber { IsInteger: true } t && t.IntegerValue is 0 or 1
                ? (int)t.IntegerValue
                : null;
        return new ImageGeometry(width, height, components, jpeg, colorTransform);
    }

    /// <summary>
    /// Proves a resampled or converted image against its source: same components, a size no larger
    /// and of the same aspect, a mean color within <see cref="MaxMeanShift"/> per channel and
    /// luminance SSIM at least <paramref name="gate"/> against the source resampled to the new size.
    /// Without a gate the floor is <see cref="StructuralFloor"/>, which only catches broken output.
    /// </summary>
    /// <param name="source">The source image after its general filters: samples, or a JPEG.</param>
    /// <param name="output">The output image after its general filters.</param>
    /// <returns>Null when the proof holds, otherwise the reason it does not.</returns>
    public static string? Verify(ReadOnlySpan<byte> source, ImageGeometry a, ReadOnlySpan<byte> output, ImageGeometry b, double? gate, long decodeLimit)
    {
        if (a.Components != b.Components) return "resampled image changed its components";
        if (b.Width > a.Width || b.Height > a.Height) return "resampled image is larger than its source";
        if (Math.Abs(((long)b.Width * a.Height) - ((long)b.Height * a.Width)) >= Math.Max(a.Width, a.Height)) return "resampled image changed its aspect ratio";

        Raster? before = Decode(source, a, decodeLimit), after = Decode(output, b, decodeLimit);
        if (before is null) return "source image cannot be decoded";
        if (after is null) return "resampled image cannot be decoded";
        Raster reference = after.Width == before.Width && after.Height == before.Height ? before : Resampler.Downsample(before, after.Width, after.Height);
        if (Ssim.MeanShift(reference, after) > MaxMeanShift) return "resampled image changed its colors";

        // SSIM stays well above zero on flat, noisy windows whatever the content, so the picture
        // as a whole is also compared at a scale every faithful encoding keeps: a mirrored,
        // shifted or swapped image fails here.
        if (CoarseDifference(reference, after) > MaxCoarseDifference) return "resampled image differs from its source at coarse scale";
        double ssim = Ssim.Compare(reference, after), floor = gate ?? StructuralFloor;
        return ssim >= floor ? null : Compat.Format("resampled image has SSIM {0:F4}, below {1}", ssim, floor);
    }

    /// <summary>SSIM the proof requires of a resampled image when no gate is set.</summary>
    public const double StructuralFloor = 0.5;

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
        if (!g.Jpeg)
        {
            long size = (long)g.Width * g.Height * g.Components;
            return data.Length < size || size > decodeLimit ? null : new Raster(g.Width, g.Height, g.Components, data.Slice(0, (int)size).ToArray());
        }

        try
        {
            Raster r = JpegDecoder.Decode(data, g.ColorTransform, decodeLimit);
            return r.Width == g.Width && r.Height == g.Height && r.Channels == g.Components ? r : null;
        }
        catch (JpegFormatException)
        {
            return null;
        }
    }

    private static Raster? Source(ReadOnlySpan<byte> data, ImagePlan plan, long decodeLimit, out int? quality, out bool subsampled, out bool adobe)
    {
        quality = null;
        subsampled = false;
        adobe = false;
        if (!plan.Jpeg)
        {
            long size = (long)plan.Width * plan.Height * plan.Components;
            if (data.Length < size || size > decodeLimit) return null;
            return new Raster(plan.Width, plan.Height, plan.Components, data.Slice(0, (int)size).ToArray());
        }

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

    private static byte[]? EncodeJpeg(Raster raster, int quality, bool subsample, bool progressive)
    {
        using JpegImage image = JpegEncoder.Encode(raster, quality, subsample);
        return JpegWriter.TryWriteSmallest(image, raster.Samples.Length / 4, progressive);
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
