#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

var options = new PdfOptimizerOptions { JpegQuality = 60 };   // lossy, JPEG images only
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual-email.pdf", options);
Console.WriteLine($"{result.Disposition}: {result.OriginalBytes:N0} to {result.OutputBytes:N0} bytes, {result.SavingPercent:F1}% saved");
