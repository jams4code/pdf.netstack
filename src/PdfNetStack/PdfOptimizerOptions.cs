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

    /// <summary>Merge byte-identical fonts, images and other resources. Default true.</summary>
    public bool DeduplicateObjects { get; init; } = true;

    /// <summary>Re-encode streams with better lossless compression. Default true.</summary>
    public bool RecompressStreams { get; init; } = true;

    /// <summary>Try PNG prediction on raw images of 8 bits or fewer per component. Lossless. Default true.</summary>
    public bool UseImagePredictors { get; init; } = true;

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
    }
}
