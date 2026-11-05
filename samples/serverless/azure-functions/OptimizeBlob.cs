using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PdfNetStack;

namespace PdfFunctions;

/// <summary>
/// Optimizes each PDF written to the incoming container into the optimized container. The
/// optimizer runs in streaming mode: it copies the download to the temporary directory, reads it
/// on demand and writes the verified rewrite there, from where it is uploaded. Memory stays flat
/// whatever the file size; the temporary directory needs about three times the file.
/// </summary>
/// <remarks>
/// The trigger uses Event Grid, the only blob trigger source Flex Consumption supports and the
/// recommended one on the Consumption plan: README.md shows the event subscription it needs.
/// </remarks>
public sealed partial class OptimizeBlob(ILogger<OptimizeBlob> logger)
{
    /// <summary>The largest file optimized; anything bigger is skipped. Keep it well below the instance's temporary storage, which holds about three times the file.</summary>
    private static readonly long MaxInputBytes = long.TryParse(Environment.GetEnvironmentVariable("PDF_MAX_INPUT_MB"), out long mb) ? mb << 20 : 100L << 20;

    [Function(nameof(OptimizeBlob))]
    public async Task RunAsync(
        [BlobTrigger("incoming/{name}", Source = BlobTriggerSource.EventGrid, Connection = "PdfStorage")] BlobClient source,
        string name,
        [BlobInput("optimized", Connection = "PdfStorage")] BlobContainerClient optimized,
        CancellationToken cancellationToken)
    {
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        BlobProperties properties = await source.GetPropertiesAsync(cancellationToken: cancellationToken);
        if (properties.ContentLength > MaxInputBytes)
        {
            Skipped(logger, name, properties.ContentLength, MaxInputBytes);
            return;
        }

        // Path.GetTempPath() is the instance's local temporary storage on every plan.
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        PdfOptimizationResult result;
        await using (Stream input = await source.OpenReadAsync(cancellationToken: cancellationToken))
        {
            // Copies the input to the temporary directory with asynchronous reads, then writes the
            // verified rewrite, or the input unchanged.
            var options = new PdfOptimizerOptions { Streaming = true, MaxInputBytes = MaxInputBytes };
            result = await PdfOptimizer.OptimizeAsync(input, output, options, cancellationToken);
        }

        output.Position = 0;
        var upload = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" } };
        await optimized.GetBlobClient(name).UploadAsync(output, upload, cancellationToken);
        Optimized(logger, name, result.Disposition, result.OriginalBytes, result.OutputBytes, result.Reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped {Name}: {Bytes} bytes is over the limit of {Limit}.")]
    private static partial void Skipped(ILogger logger, string name, long bytes, long limit);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name}: {Disposition}, {Original} to {Output} bytes. {Reason}")]
    private static partial void Optimized(ILogger logger, string name, OptimizationDisposition disposition, long original, long output, string reason);
}
