#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;

#region progressive
using (var source = File.OpenRead("manual.pdf"))
using (var output = File.Create("manual-rotated.pdf"))
{
    PdfDocument doc = PdfDocument.OpenOnDemand(source);
    doc.Pages[0].Rotate(90);
    await doc.SaveAsync(output, new PdfSaveOptions { Optimize = false });  // object by object, asynchronous writes
}
Console.WriteLine($"{new FileInfo("manual-rotated.pdf").Length:N0} bytes written progressively");
#endregion

#region streaming
var options = new PdfOptimizerOptions { Streaming = true, MaxInputBytes = 16L << 30 };
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("manual-rotated.pdf", "manual-small.pdf", options);
Console.WriteLine($"{result.Disposition}, streamed {result.Statistics.Streamed}: {result.OriginalBytes:N0} to {result.OutputBytes:N0} bytes");
Console.WriteLine(result.Reason);
#endregion
