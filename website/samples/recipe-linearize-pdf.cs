#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;

var options = new PdfOptimizerOptions { Linearize = true };
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("report.pdf", "report.web.pdf", options);
Console.WriteLine($"{result.Disposition}, linearized: {result.Statistics.Linearized}");
Console.WriteLine(result.Reason);
