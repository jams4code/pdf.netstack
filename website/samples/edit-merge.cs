#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

#region merge
PdfDocument merged = PdfDocument.Merge([
    PdfDocument.Open("a.pdf"),
    PdfDocument.Open("b.pdf"),
    PdfDocument.Open("c.pdf"),
]);
merged.Save("merged.pdf");
#endregion

#region split
int number = 0;
foreach (PdfDocument single in PdfDocument.Open("report.pdf").Split())
{
    single.Save($"report-page-{++number}.pdf");
}
#endregion

#region range
PdfDocument source = PdfDocument.Open("manual.pdf");
PdfDocument excerpt = PdfDocument.Create();
excerpt.Pages.Import([source.Pages[4], source.Pages[0], source.Pages[1]]);   // any order
excerpt.Save("manual-excerpt.pdf");
#endregion

Console.WriteLine($"merged: {merged.Pages.Count} pages, split: {number} files, excerpt: {excerpt.Pages.Count} pages");
