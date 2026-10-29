#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.0
using PdfNetStack;

PdfDocument doc = PdfDocument.Open("report.pdf");
int number = 1;
foreach (PdfDocument single in doc.Split())   // one new document per page
{
    single.Save($"report-page-{number}.pdf");
    number++;
}
Console.WriteLine($"Wrote {number - 1} files");
