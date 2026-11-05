#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// run: build-only (a function handler calls it with storage streams; samples/serverless has complete apps)
using PdfNetStack;

await using FileStream input = File.OpenRead("report.pdf");
await using FileStream upload = File.Create("report-optimized.pdf");
PdfOptimizationResult result = await Optimizer.OptimizeAsync(input, upload, CancellationToken.None);
Console.WriteLine($"{result.Disposition}: {result.OriginalBytes} to {result.OutputBytes} bytes");

#region optimize
internal static class Optimizer
{
    public static async Task<PdfOptimizationResult> OptimizeAsync(Stream input, Stream destination, CancellationToken cancellationToken)
    {
        // /tmp on AWS Lambda, the instance's local storage on Azure Functions; deleted on close.
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        var options = new PdfOptimizerOptions
        {
            Streaming = true,                                    // flat memory; about 3x the file in the temp folder
            MaxInputBytes = 600L << 20,                          // larger inputs pass through unchanged
            MaxDegreeOfParallelism = Environment.ProcessorCount, // the default; vCPUs grow with memory on Lambda
        };
        PdfOptimizationResult result = await PdfOptimizer.OptimizeAsync(input, output, options, cancellationToken);

        output.Position = 0;
        await output.CopyToAsync(destination, cancellationToken);   // or hand it to the storage SDK's upload
        return result;
    }
}
#endregion
