namespace PdfNetStack;

/// <summary>What the optimizer wrote to the output.</summary>
public enum OptimizationDisposition
{
    /// <summary>A smaller, verified rewrite was written.</summary>
    Optimized,

    /// <summary>The input was written unchanged: no worthwhile saving, or the rewrite failed verification.</summary>
    OriginalRetained,

    /// <summary>The input was written unchanged because it uses features the optimizer must not touch.</summary>
    UnsupportedRetained,
}

/// <summary>Counters describing what each pass did.</summary>
/// <param name="ObjectsInFile">In-use objects listed by the source cross-reference data.</param>
/// <param name="ReachableObjects">Objects reachable from the document catalog and info dictionary.</param>
/// <param name="ObjectsWritten">Indirect objects in the output, excluding object and xref streams.</param>
/// <param name="StreamsRecompressed">Streams whose encoding changed.</param>
/// <param name="ObjectsDeduplicated">Objects merged into an identical twin.</param>
/// <param name="EntriesStripped">Entries removed by opt-in stripping options.</param>
/// <param name="ObjectsVerified">Object pairs proven equivalent by verification.</param>
/// <param name="Elapsed">Wall-clock time spent.</param>
/// <param name="Phases">Wall-clock time per pass.</param>
public sealed record OptimizationStatistics(
    int ObjectsInFile,
    int ReachableObjects,
    int ObjectsWritten,
    int StreamsRecompressed,
    int ObjectsDeduplicated,
    int EntriesStripped,
    int ObjectsVerified,
    TimeSpan Elapsed,
    PhaseTimings Phases)
{
    /// <summary>Objects dropped because nothing referenced them.</summary>
    public int UnreachableRemoved => Math.Max(0, ObjectsInFile - ReachableObjects);

    /// <summary>JPEG images re-encoded, counted in <see cref="StreamsRecompressed"/> as well.</summary>
    public int JpegImagesRecompressed { get; init; }

    /// <summary>JPEG images among <see cref="JpegImagesRecompressed"/> that were requantized, and so changed lossily.</summary>
    public int JpegImagesRequantized { get; init; }

    internal static OptimizationStatistics Empty(TimeSpan elapsed) => new(0, 0, 0, 0, 0, 0, 0, elapsed, new PhaseTimings());
}

/// <summary>Wall-clock time per optimization pass.</summary>
public sealed record PhaseTimings
{
    /// <summary>Parsing, safety checks and reachability.</summary>
    public TimeSpan Analyze { get; init; }

    /// <summary>Stream re-encoding.</summary>
    public TimeSpan Recompress { get; init; }

    /// <summary>Duplicate detection.</summary>
    public TimeSpan Deduplicate { get; init; }

    /// <summary>Serialization.</summary>
    public TimeSpan Write { get; init; }

    /// <summary>Equivalence proof.</summary>
    public TimeSpan Verify { get; init; }
}

/// <summary>Outcome of one optimization. The output always holds a valid PDF: the rewrite or the original.</summary>
/// <param name="Disposition">What was written.</param>
/// <param name="OriginalBytes">Input size.</param>
/// <param name="OutputBytes">Output size.</param>
/// <param name="SourceSha256">Lowercase hex SHA-256 of the input.</param>
/// <param name="OutputSha256">Lowercase hex SHA-256 of the output.</param>
/// <param name="PageCount">Pages according to the page tree, or 0 when it could not be read.</param>
/// <param name="Reason">Why this disposition was chosen.</param>
/// <param name="Statistics">Per pass counters.</param>
public sealed record PdfOptimizationResult(
    OptimizationDisposition Disposition,
    long OriginalBytes,
    long OutputBytes,
    string SourceSha256,
    string OutputSha256,
    int PageCount,
    string Reason,
    OptimizationStatistics Statistics)
{
    /// <summary>Saving in percent of the input size.</summary>
    public double SavingPercent => OriginalBytes == 0 ? 0 : 100.0 * (OriginalBytes - OutputBytes) / OriginalBytes;
}
