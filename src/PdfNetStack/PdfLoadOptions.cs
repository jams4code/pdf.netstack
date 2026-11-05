using System.Text;

namespace PdfNetStack;

/// <summary>How an opened document holds its file.</summary>
public enum PdfLoadMode
{
    /// <summary>
    /// The whole file is read into memory and every object reachable from the catalog is parsed
    /// when opening. The file or stream is not used afterwards. The default.
    /// </summary>
    InMemory,

    /// <summary>
    /// Only the cross-reference data and the page tree are read when opening. Every other object
    /// is parsed when first used, and stream data (page content, images, fonts) is read from the
    /// file each time it is needed and not kept. Memory follows what the application touches,
    /// not the size of the file. Dispose the document to release the file.
    /// </summary>
    OnDemand,
}

/// <summary>
/// Options for opening a document with <see cref="PdfDocument.Open(Stream, PdfLoadOptions)"/> and
/// its overloads: the password, ceilings on decoding, and whether the file is read into memory or
/// on demand, with where a stream that cannot seek is copied to.
/// </summary>
/// <example>
/// <code>
/// // A request body or a storage download: read on demand, copied to /tmp past 8 MiB.
/// var options = new PdfLoadOptions
/// {
///     Mode = PdfLoadMode.OnDemand,
///     SpoolThreshold = 8 &lt;&lt; 20,
///     TempDirectory = "/tmp",
///     MaxDecodedStreamBytes = 64 &lt;&lt; 20,
///     MaxImagePixels = 50_000_000,
/// };
/// using PdfDocument doc = await PdfDocument.OpenAsync(body, options, cancellationToken);
/// </code>
/// </example>
public sealed record PdfLoadOptions
{
    /// <summary>Default of <see cref="MaxDecodedStreamBytes"/>: 256 MiB.</summary>
    public const long DefaultMaxDecodedStreamBytes = 256L << 20;

    /// <summary>Default and largest value of <see cref="MaxImagePixels"/>: 2^28, a 16384 by 16384 image.</summary>
    public const long DefaultMaxImagePixels = 1L << 28;

    /// <summary>Default of <see cref="SpoolThreshold"/>: 16 MiB.</summary>
    public const long DefaultSpoolThreshold = 16L << 20;

    /// <summary>Shared default instance: in memory, no password, default limits.</summary>
    public static PdfLoadOptions Default { get; } = new();

    /// <summary>User or owner password for encrypted files. Null tries the empty user password.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Ceiling for any single decoded stream, a defence against decompression bombs, in bytes.
    /// It also bounds the bytes of one decoded image. Default 256 MiB.
    /// </summary>
    public long MaxDecodedStreamBytes { get; init; } = DefaultMaxDecodedStreamBytes;

    /// <summary>
    /// Most pixels one image of the document may have when it is decoded, whatever its encoding:
    /// raw samples (with or without PNG or TIFF predictors), JPEG, JPEG 2000, JBIG2 and CCITT fax.
    /// A larger image is refused with <see cref="PdfFormatException"/> before its pixels are
    /// allocated. From 1 to 2^28, the default. A decoded image takes up to three bytes a pixel.
    /// </summary>
    public long MaxImagePixels { get; init; } = DefaultMaxImagePixels;

    /// <summary>Whether the file is read into memory or on demand. Default <see cref="PdfLoadMode.InMemory"/>.</summary>
    public PdfLoadMode Mode { get; init; }

    /// <summary>
    /// For <see cref="PdfLoadMode.OnDemand"/> from a stream that cannot seek (an HTTP request body,
    /// a storage download) and for every stream given to <see cref="PdfDocument.OpenAsync"/>: up to
    /// this many bytes are copied to memory; a longer stream is copied to a temporary file in
    /// <see cref="TempDirectory"/> instead. 0 always uses a file. Default 16 MiB.
    /// </summary>
    public long SpoolThreshold { get; init; } = DefaultSpoolThreshold;

    /// <summary>
    /// Folder for the temporary copy of a stream longer than <see cref="SpoolThreshold"/>. Null uses
    /// <see cref="Path.GetTempPath"/>; on AWS Lambda that is /tmp, the one writable folder. The
    /// file is created for this document alone, deleted when the document is disposed, and marked
    /// to be deleted by the operating system should the process end first where it supports that.
    /// </summary>
    public string? TempDirectory { get; init; }

    internal void Validate()
    {
        if (MaxDecodedStreamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDecodedStreamBytes));
        if (MaxImagePixels is < 1 or > DefaultMaxImagePixels) throw new ArgumentOutOfRangeException(nameof(MaxImagePixels), MaxImagePixels, "The pixel limit must be from 1 to 2^28.");
        if (Mode is not (PdfLoadMode.InMemory or PdfLoadMode.OnDemand)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (SpoolThreshold < 0) throw new ArgumentOutOfRangeException(nameof(SpoolThreshold));
        if (TempDirectory is { Length: 0 }) throw new ArgumentException("The temporary folder must not be empty; null uses the system's.", nameof(TempDirectory));
    }

    // The password stays out of logs that print the options.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Password = ").Append(Password is null ? "null" : "(set)");
        builder.Append(", MaxDecodedStreamBytes = ").Append(MaxDecodedStreamBytes);
        builder.Append(", MaxImagePixels = ").Append(MaxImagePixels);
        builder.Append(", Mode = ").Append(Mode);
        builder.Append(", SpoolThreshold = ").Append(SpoolThreshold);
        builder.Append(", TempDirectory = ").Append(TempDirectory ?? "null");
        return true;
    }
}
