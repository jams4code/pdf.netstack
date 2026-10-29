#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

Directory.CreateDirectory("optimized");
foreach (string path in Directory.GetFiles(".", "*.pdf"))
{
    string output = Path.Combine("optimized", Path.GetFileName(path));
    PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync(path, output);
    Console.WriteLine($"{Path.GetFileName(path)}: {result.Disposition}, {result.SavingPercent:F1}% saved");
}
