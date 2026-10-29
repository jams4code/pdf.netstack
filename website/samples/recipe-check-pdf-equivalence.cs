#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

// Two candidates: an optimized copy, and an edited copy with one page removed.
(byte[] optimized, _) = PdfOptimizer.Optimize(File.ReadAllBytes("report.pdf"));
File.WriteAllBytes("report-optimized.pdf", optimized);
PdfDocument edited = PdfDocument.Open("report.pdf");
edited.Pages.RemoveAt(2);
edited.Save("report-edited.pdf");

#region short
byte[] original = File.ReadAllBytes("report.pdf");
foreach (string candidate in new[] { "report-optimized.pdf", "report-edited.pdf" })
{
    string? difference = PdfEquivalence.FindDifference(original, File.ReadAllBytes(candidate));
    Console.WriteLine($"{candidate}: {difference ?? "equivalent"}");
}
#endregion
