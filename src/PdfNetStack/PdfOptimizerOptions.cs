using System.IO.Compression;

namespace PdfNetStack;

/// <summary>How hard Flate compression works. Higher effort trades CPU for smaller output.</summary>
public enum CompressionEffort
{
    /// <summary>zlib level 1. Use for throughput-bound batch jobs.</summary>
    Fast,

    /// <summary>zlib level 6.</summary>
    Balanced,

    /// <summary>zlib level 9. The default.</summary>
    Maximum,

    /// <summary>
    /// An exhaustive Deflate search for archival jobs: iterated optimal parsing over every match
    /// length with a cost model refined from each pass, block splitting, and the smallest of
    /// dynamic, fixed and stored blocks, in the manner of Zopfli. Output stays standard Flate and
    /// is never larger than zlib level 9, which is kept whenever it wins. Expect a few percent
    /// smaller streams for 2 to 150 times the CPU time, the most on long repetitive streams.
    /// </summary>
    Exhaustive,
}

/// <summary>
/// Options for <see cref="PdfOptimizer"/>. Defaults are lossless and conservative: anything the
/// engine cannot rewrite with certainty is returned byte for byte unchanged.
/// </summary>
public sealed record PdfOptimizerOptions
{
    /// <summary>Shared default instance.</summary>
    public static PdfOptimizerOptions Default { get; } = new();

    /// <summary>Minimum saving, in percent of the input size, required to keep the rewrite. Default 1.</summary>
    public double MinSavingPercent { get; init; } = 1.0;

    /// <summary>Largest input accepted, in bytes. Larger files are returned unchanged. Default 1 GiB.</summary>
    public long MaxInputBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Ceiling for any single decoded stream, the guard against decompression bombs. Streams over the
    /// limit are left untouched. Default 256 MiB.
    /// </summary>
    public long MaxDecodedStreamBytes { get; init; } = 256L << 20;

    /// <summary>Flate effort for streams the optimizer re-encodes. Default <see cref="CompressionEffort.Maximum"/>.</summary>
    public CompressionEffort Compression { get; init; } = CompressionEffort.Maximum;

    /// <summary>Pack non-stream objects into compressed object streams (PDF 1.5). Default true.</summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>Objects per object stream. Larger compresses better; smaller loads pages faster. Default 200.</summary>
    public int ObjectsPerStream { get; init; } = 200;

    /// <summary>
    /// Merge identical fonts, images and other resources. Streams are compared by what they decode
    /// to, so copies encoded differently merge too. Default true.
    /// </summary>
    public bool DeduplicateObjects { get; init; } = true;

    /// <summary>
    /// Remove resource entries (fonts, XObjects, graphics states, patterns, shadings, color spaces,
    /// property lists) that no content stream using the resource dictionary can name. Lossless:
    /// pages, forms, patterns, Type 3 glyphs and annotation appearances are all read, and anything
    /// that cannot be read for certain keeps its resources. Default true.
    /// </summary>
    public bool PruneUnusedResources { get; init; } = true;

    /// <summary>
    /// Rewrite content streams with minimal white space, canonical numbers and no comments. Every
    /// operator and operand stays the same; verification compares the streams token by token.
    /// Default true.
    /// </summary>
    public bool CompactContentStreams { get; init; } = true;

    /// <summary>
    /// Merge subsets of one TrueType font that keep the original glyph ids (composite fonts with an
    /// identity CIDToGIDMap) into a single program, when every glyph each font shows stays exactly
    /// the same. Common in merged documents. Requires <see cref="DeduplicateObjects"/>. Default true.
    /// </summary>
    public bool MergeFontSubsets { get; init; } = true;

    /// <summary>
    /// Write a linearized file (ISO 32000-1, Annex F), which a viewer can start showing before the
    /// whole file has arrived ("fast web view"). Inherited page attributes are moved onto the
    /// pages, and every object is written on its own without object streams, so the output can
    /// be larger than the input; a linearized rewrite of a file that is not linearized yet is kept
    /// even then, provided it verifies. A page tree that cannot be laid out page by page, such as
    /// one listing a page twice, is written as usual and the result says why. Default false.
    /// </summary>
    public bool Linearize { get; init; }

    /// <summary>Re-encode streams with better lossless compression. Default true.</summary>
    public bool RecompressStreams { get; init; } = true;

    /// <summary>Try PNG prediction on raw images of 8 bits or fewer per component. Lossless. Default true.</summary>
    public bool UseImagePredictors { get; init; } = true;

    /// <summary>
    /// Re-encode JPEG images (DCTDecode) with Huffman tables built for each image, and without the
    /// segments a PDF reader ignores (Exif, XMP, embedded ICC profiles, comments; the image's color
    /// space comes from the PDF). The DCT coefficients, and so the decoded pixels, stay the same.
    /// Part of stream recompression. Default true.
    /// </summary>
    public bool OptimizeJpegImages { get; init; } = true;

    /// <summary>
    /// Re-encode bilevel (1 bit) images stored as raw samples, under Flate, LZW or RunLength, or as
    /// CCITT Group 3, as CCITT Group 4 when that is smaller. The encoder follows ITU-T T.6, and
    /// the proof decodes both versions and requires every pixel to match, so the image stays
    /// exactly the same. Group 3 data is only re-encoded when it decodes without a flaw and its
    /// parameters leave nothing to a reader's defaults. Part of stream recompression. Default true.
    /// </summary>
    public bool OptimizeBilevelImages { get; init; } = true;

    /// <summary>
    /// Requantize JPEG images to this quality, 1 to 100, on the scale libjpeg and most image tools
    /// use. Not lossless: fine detail is traded for size. A table is never made finer than the
    /// image's own, so images already at or below this quality only get the lossless pass, and
    /// images used as soft masks always stay lossless. Null, the default, keeps every image lossless.
    /// Requires <see cref="RecompressStreams"/> and <see cref="OptimizeJpegImages"/>.
    /// </summary>
    public int? JpegQuality { get; init; }

    /// <summary>
    /// Downsample images drawn at more than 1.5 times this resolution, in pixels per inch, to
    /// this resolution. The resolution is measured from the largest size at which the image is
    /// drawn anywhere in the document: pages, forms, patterns, soft masks and annotation
    /// appearances. Images drawn in a way the measurement cannot follow, masks, images whose
    /// soft mask must match their size, and images other than 8 bit gray, RGB or CMYK, gray or
    /// RGB JPEG 2000, and bilevel images are left alone. The filter is Lanczos with three lobes.
    /// A downsampled JPEG is encoded again, at <see cref="JpegQuality"/> but never finer than it
    /// was; raw samples stay lossless after resampling unless <see cref="ConvertImagesToJpeg"/> is
    /// set. A downsampled JPEG 2000 image, which the optimizer does not write, becomes a JPEG at
    /// <see cref="JpegQuality"/>, or without it lossless samples. Bilevel images (raw, CCITT or
    /// JBIG2) stay bilevel: each new pixel takes the color that covers at least half of it, and
    /// strokes thinner than a new pixel are kept, so text stays legible; the result is CCITT Group
    /// 4 or Flate, whichever is smaller. JPEG sources need <see cref="OptimizeJpegImages"/>. Not
    /// lossless. Null, the default, keeps every image's size. Requires <see cref="RecompressStreams"/>.
    /// </summary>
    public int? MaxImageDpi { get; init; }

    /// <summary>
    /// Re-encode raw 8 bit gray and RGB images, and gray and RGB JPEG 2000 images, as JPEG at
    /// <see cref="JpegQuality"/> when they look photographic. Bilevel, indexed, line art and screenshot-like images are recognized from
    /// their pixels and left lossless, and a JPEG is kept only when it is at least 10% smaller
    /// than the best lossless encoding. Not lossless. Default false. Requires
    /// <see cref="JpegQuality"/> and <see cref="RecompressStreams"/>.
    /// </summary>
    public bool ConvertImagesToJpeg { get; init; }

    /// <summary>
    /// The perceptual gate for every lossy image change: the minimum structural similarity
    /// (SSIM, on luminance, 0 to 1) a changed image must keep, or it stays as it was. A requantized
    /// JPEG is compared with the original; a downsampled image with the exact resample of the
    /// original, so the gate judges the encoding at the size the image is delivered rather than
    /// the resolution target. Around 0.95 keeps changes hard to see; 0.90 allows visible
    /// softening of fine texture. The proof checks the gate again on the output. Null, the
    /// default, sets no gate.
    /// </summary>
    public double? MinImageSsim { get; init; }

    /// <summary>
    /// Also write JPEG images progressively (spectral selection and successive approximation, as
    /// libjpeg's default progression) and keep that coding when it is smaller than optimized
    /// sequential coding. Applies to every JPEG the optimizer writes, lossless or not. The decoded
    /// image is the same either way. Default false. Requires <see cref="RecompressStreams"/>.
    /// </summary>
    public bool ProgressiveJpeg { get; init; }

    /// <summary>
    /// Re-read the candidate and prove it equivalent to the source before returning it.
    /// Disable only for trusted, already validated pipelines. Default true.
    /// </summary>
    public bool Verify { get; init; } = true;

    /// <summary>
    /// Allow flattening files with incremental updates into one revision. The visible document is
    /// unchanged, but earlier revisions are discarded. Default false.
    /// </summary>
    public bool AllowIncrementalUpdates { get; init; }

    /// <summary>
    /// Allow rewriting files whose cross-reference data or stream lengths had to be repaired.
    /// The output is a clean file, but another reader may have repaired the input differently. Default false.
    /// </summary>
    public bool AllowRepairedFiles { get; init; }

    /// <summary>Remove page thumbnail images (/Thumb). Viewers regenerate them. Not lossless. Default false.</summary>
    public bool RemovePageThumbnails { get; init; }

    /// <summary>
    /// Remove application private data (/PieceInfo), such as Illustrator editing state. Rendering is
    /// unaffected, but the authoring application loses its round-trip data. Default false.
    /// </summary>
    public bool RemovePieceInfo { get; init; }

    /// <summary>Worker threads for compression and verification. Default: processor count.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    internal CompressionLevel ZlibLevel => Compression switch
    {
        CompressionEffort.Fast => CompressionLevel.Fastest,
        CompressionEffort.Balanced => CompressionLevel.Optimal,
        _ => Compat.MaximumCompression,
    };

    internal void Validate()
    {
        if (MinSavingPercent is < 0 or >= 100) throw new ArgumentOutOfRangeException(nameof(MinSavingPercent));
        if (MaxInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxInputBytes));
        if (MaxDecodedStreamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDecodedStreamBytes));
        if (ObjectsPerStream is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(ObjectsPerStream));
        if (MaxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism));
        if (JpegQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(JpegQuality));
        if (JpegQuality is not null && !(RecompressStreams && OptimizeJpegImages))
        {
            throw new ArgumentException("JpegQuality needs RecompressStreams and OptimizeJpegImages enabled.", nameof(JpegQuality));
        }

        if (MaxImageDpi is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaxImageDpi));
        if (MinImageSsim is { } ssim && (double.IsNaN(ssim) || ssim is <= 0 or > 1)) throw new ArgumentOutOfRangeException(nameof(MinImageSsim));
        if (MaxImageDpi is not null && !RecompressStreams) throw new ArgumentException("MaxImageDpi needs RecompressStreams enabled.", nameof(MaxImageDpi));
        if (ProgressiveJpeg && !RecompressStreams) throw new ArgumentException("ProgressiveJpeg needs RecompressStreams enabled.", nameof(ProgressiveJpeg));
        if (ConvertImagesToJpeg && (JpegQuality is null || !RecompressStreams))
        {
            throw new ArgumentException("ConvertImagesToJpeg needs JpegQuality set and RecompressStreams enabled.", nameof(ConvertImagesToJpeg));
        }
    }
}
