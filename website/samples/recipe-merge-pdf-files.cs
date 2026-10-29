#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

string[] files = ["a.pdf", "b.pdf", "c.pdf"];
PdfDocument merged = PdfDocument.Merge(files.Select(file => PdfDocument.Open(file)));
merged.Save("merged.pdf");   // shared fonts and images are written once
Console.WriteLine($"merged.pdf: {merged.Pages.Count} pages");
