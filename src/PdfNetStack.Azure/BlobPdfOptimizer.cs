using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace PdfNetStack.Azure;

/// <summary>Where the optimized PDF and its sidecar were written.</summary>
/// <param name="OutputBlobName">Content-addressed name of the PDF in the target container.</param>
/// <param name="SidecarBlobName">Name of the provenance JSON next to it.</param>
/// <param name="AlreadyExisted">True when another run had already written the same content.</param>
/// <param name="Result">The optimizer result.</param>
public sealed record BlobOptimizationOutcome(string OutputBlobName, string SidecarBlobName, bool AlreadyExisted, PdfOptimizationResult Result);

/// <summary>Provenance written next to every output blob.</summary>
public sealed record OptimizationSidecar(
    string SourceUri,
    string SourceETag,
    string SourceSha256,
    long SourceBytes,
    string OutputSha256,
    long OutputBytes,
    string Disposition,
    string Reason,
    int PageCount,
    double SavingPercent,
    double ElapsedMilliseconds,
    string Engine,
    DateTimeOffset ProcessedAt);

/// <summary>
/// Optimizes PDFs between two Blob Storage containers. The source blob is read at a pinned ETag
/// and never modified. Output names are derived from the output SHA-256, so retries and
/// concurrent workers converge on one blob, and uploads are conditional so nothing is overwritten.
/// </summary>
public sealed class BlobPdfOptimizer
{
    private readonly BlobServiceClient _service;
    private readonly PdfOptimizerOptions _options;

    /// <summary>Creates the pipeline for a storage account.</summary>
    /// <param name="accountUri">For example https://account.blob.core.windows.net.</param>
    /// <param name="credential">Defaults to <see cref="DefaultAzureCredential"/>: managed identity in Azure, developer login locally.</param>
    /// <param name="options">Optimizer options.</param>
    public BlobPdfOptimizer(Uri accountUri, TokenCredential? credential = null, PdfOptimizerOptions? options = null)
        : this(new BlobServiceClient(accountUri, credential ?? new DefaultAzureCredential()), options)
    {
    }

    /// <summary>Creates the pipeline over an existing client, for tests or custom pipelines.</summary>
    public BlobPdfOptimizer(BlobServiceClient service, PdfOptimizerOptions? options = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options ?? PdfOptimizerOptions.Default;
    }

    /// <summary>
    /// Optimizes one blob. Grant the identity Storage Blob Data Reader on the source container and
    /// Storage Blob Data Contributor on the target container. The containers must differ.
    /// </summary>
    public async Task<BlobOptimizationOutcome> OptimizeAsync(
        string sourceContainer,
        string sourceBlobName,
        string targetContainer,
        CancellationToken cancellationToken = default)
    {
        Compat.ThrowIfNullOrEmpty(sourceContainer, nameof(sourceContainer));
        Compat.ThrowIfNullOrEmpty(sourceBlobName, nameof(sourceBlobName));
        Compat.ThrowIfNullOrEmpty(targetContainer, nameof(targetContainer));
        if (string.Equals(sourceContainer, targetContainer, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Write optimized PDFs to a separate container so originals stay immutable.", nameof(targetContainer));
        }

        BlobClient source = _service.GetBlobContainerClient(sourceContainer).GetBlobClient(sourceBlobName);
        BlobProperties properties = (await source.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        if (properties.ContentLength > _options.MaxInputBytes)
        {
            throw new InvalidOperationException($"Blob is {properties.ContentLength} bytes, above MaxInputBytes.");
        }

        // Pin the read to the ETag observed above so a concurrent overwrite cannot mix versions.
        BlobDownloadResult download = (await source.DownloadContentAsync(
            new BlobDownloadOptions { Conditions = new BlobRequestConditions { IfMatch = properties.ETag } },
            cancellationToken).ConfigureAwait(false)).Value;
        ReadOnlyMemory<byte> input = download.Content.ToMemory();

        using var output = new MemoryStream();
        PdfOptimizationResult result = PdfOptimizer.Optimize(input, output, _options, cancellationToken);

        string sha = result.OutputSha256;
        string outputName = $"pdf/{sha[..2]}/{sha}.pdf";
        string sidecarName = $"pdf/{sha[..2]}/{sha}.json";
        BlobContainerClient target = _service.GetBlobContainerClient(targetContainer);

        var sidecar = new OptimizationSidecar(
            source.Uri.GetLeftPart(UriPartial.Path),
            properties.ETag.ToString(),
            result.SourceSha256,
            result.OriginalBytes,
            sha,
            result.OutputBytes,
            result.Disposition.ToString(),
            result.Reason,
            result.PageCount,
            Math.Round(result.SavingPercent, 3),
            Math.Round(result.Statistics.Elapsed.TotalMilliseconds, 1),
            "PdfNetStack/" + typeof(PdfOptimizer).Assembly.GetName().Version,
            DateTimeOffset.UtcNow);

        output.Position = 0;
        bool existed = !await UploadOnceAsync(target.GetBlobClient(outputName), output, "application/pdf", new Dictionary<string, string>
        {
            ["sha256"] = sha,
            ["sourcesha256"] = result.SourceSha256,
            ["disposition"] = result.Disposition.ToString().ToLowerInvariant(),
        }, cancellationToken).ConfigureAwait(false);

        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(sidecar, SidecarJson.Default.OptimizationSidecar));
        await UploadOnceAsync(target.GetBlobClient(sidecarName), json, "application/json", null, cancellationToken).ConfigureAwait(false);

        return new BlobOptimizationOutcome(outputName, sidecarName, existed, result);
    }

    /// <returns>False when the blob already existed, which is success for content-addressed names.</returns>
    private static async Task<bool> UploadOnceAsync(BlobClient blob, Stream content, string contentType, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        try
        {
            await blob.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = metadata,
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OptimizationSidecar))]
internal sealed partial class SidecarJson : JsonSerializerContext;
