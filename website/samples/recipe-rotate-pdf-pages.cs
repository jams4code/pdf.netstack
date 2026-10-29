#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

PdfDocument doc = PdfDocument.Open("report.pdf");
doc.Pages[0].Rotate(90);                      // clockwise, a multiple of 90
foreach (PdfPage page in doc.Pages.Skip(1))
{
    page.Rotate(180);
}
doc.Save("report-rotated.pdf");
Console.WriteLine(string.Join(", ", doc.Pages.Select(p => p.Rotation)));
