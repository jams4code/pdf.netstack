using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Model;
using PdfNetStack;

namespace PdfLambda;

/// <summary>
/// Optimizes each PDF uploaded under incoming/ and writes it under optimized/ in the same bucket.
/// The optimizer runs in streaming mode: it copies the download to /tmp, the only writable
/// directory in Lambda, reads it on demand and writes the verified rewrite to /tmp, from where it
/// is uploaded. Memory stays flat whatever the file size; /tmp needs about three times the file.
/// </summary>
internal static class OptimizeHandler
{
    private const string InputPrefix = "incoming/";
    private const string OutputPrefix = "optimized/";

    // Created once per instance and reused by every invocation it serves.
    private static readonly AmazonS3Client S3 = new();

    /// <summary>The largest file optimized; anything bigger is skipped. Streaming keeps memory flat; /tmp holds the input copy, the rewrite and the output, about three times the file size.</summary>
    private static readonly long MaxInputBytes = long.TryParse(Environment.GetEnvironmentVariable("PDF_MAX_INPUT_MB"), out long mb) ? mb << 20 : 600L << 20;

    public static async Task HandleAsync(S3Event s3Event, ILambdaContext context)
    {
        // Stop a few seconds before Lambda would, so the instance can clean up /tmp.
        using var timeout = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(5));
        foreach (S3Event.S3EventNotificationRecord record in s3Event.Records ?? [])
        {
            string bucket = record.S3.Bucket.Name;

            // Keys arrive URL encoded, with spaces as plus signs.
            string key = Uri.UnescapeDataString(record.S3.Object.Key.Replace('+', ' '));
            if (!key.StartsWith(InputPrefix, StringComparison.Ordinal) || !key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            if (record.S3.Object.Size > MaxInputBytes)
            {
                context.Logger.LogWarning($"Skipped {key}: {record.S3.Object.Size} bytes is over the limit of {MaxInputBytes}.");
                continue;
            }

            await OptimizeAsync(bucket, key, OutputPrefix + key[InputPrefix.Length..], context, timeout.Token);
        }
    }

    private static async Task OptimizeAsync(string bucket, string key, string outputKey, ILambdaContext context, CancellationToken cancellationToken)
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        PdfOptimizationResult result;
        using (GetObjectResponse source = await S3.GetObjectAsync(bucket, key, cancellationToken))
        {
            // Copies the input to /tmp with asynchronous reads, then writes the verified rewrite, or
            // the input unchanged.
            var options = new PdfOptimizerOptions { Streaming = true, MaxInputBytes = MaxInputBytes };
            result = await PdfOptimizer.OptimizeAsync(source.ResponseStream, output, options, cancellationToken);
        }

        output.Position = 0;
        await S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = outputKey,
            InputStream = output,
            AutoCloseStream = false,
            ContentType = "application/pdf",
        }, cancellationToken);

        context.Logger.LogInformation($"{key}: {result.Disposition}, {result.OriginalBytes} to {result.OutputBytes} bytes. {result.Reason}");
    }
}
