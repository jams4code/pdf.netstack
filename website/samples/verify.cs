#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

(byte[] optimized, _) = PdfOptimizer.Optimize(File.ReadAllBytes("manual.pdf"));
File.WriteAllBytes("manual.checked.pdf", optimized);

#region short
byte[] original = File.ReadAllBytes("manual.pdf");
byte[] candidate = File.ReadAllBytes("manual.checked.pdf");   // written by any tool
string? difference = PdfEquivalence.FindDifference(original, candidate);
Console.WriteLine(difference ?? "Equivalent: same objects, same decoded content.");
#endregion
