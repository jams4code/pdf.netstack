#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

PdfDocument source = PdfDocument.Open("manual.pdf");
int[] keep = [1, 2, 5];   // page numbers, from 1, in the order you want them

PdfDocument extract = PdfDocument.Create();
extract.Pages.Import(keep.Select(n => source.Pages[n - 1]));
extract.Save("manual-extract.pdf");
Console.WriteLine($"manual-extract.pdf: {extract.Pages.Count} pages");
