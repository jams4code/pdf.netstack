#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

#region short
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual.optimized.pdf");
Console.WriteLine($"{result.Disposition}: {result.OriginalBytes:N0} to {result.OutputBytes:N0} bytes, saved {result.SavingPercent:F1}%");
Console.WriteLine(result.Reason);
#endregion
