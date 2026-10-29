#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

#region options
var options = new PdfOptimizerOptions
{
    Compression = CompressionEffort.Balanced,   // faster, slightly larger
    AllowIncrementalUpdates = true,             // flatten edit history into one revision
    MaxDegreeOfParallelism = 2,                 // cap CPU and memory per document
    JpegQuality = 60,                           // opt-in, lossy: requantize JPEG images
};
#endregion

#region memory
// In memory
(byte[] output, PdfOptimizationResult inMemory) = PdfOptimizer.Optimize(File.ReadAllBytes("manual.pdf"), options);
File.WriteAllBytes("manual.small.pdf", output);

// Streams
await using (FileStream input = File.OpenRead("manual.pdf"))
await using (FileStream destination = File.Create("manual.stream.pdf"))
{
    PdfOptimizationResult streamed = await PdfOptimizer.OptimizeAsync(input, destination);
    Console.WriteLine($"Streams: {streamed.Disposition}, {streamed.SavingPercent:F1}% saved");
}
#endregion

#region result
Console.WriteLine($"Disposition: {inMemory.Disposition}");
Console.WriteLine($"Size: {inMemory.OriginalBytes:N0} to {inMemory.OutputBytes:N0} bytes ({inMemory.SavingPercent:F1}%)");
Console.WriteLine($"Pages: {inMemory.PageCount}, output SHA-256: {inMemory.OutputSha256}");
Console.WriteLine($"Reason: {inMemory.Reason}");
Console.WriteLine($"JPEG images requantized: {inMemory.Statistics.JpegImagesRequantized}");
#endregion
