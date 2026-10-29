#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfDocument doc = PdfDocument.Open("report.pdf");
PdfFont font = PdfStandardFont.Helvetica;
for (int i = 0; i < doc.Pages.Count; i++)
{
    PdfPage page = doc.Pages[i];
    string label = $"Page {i + 1} of {doc.Pages.Count}";
    double width = font.MeasureString(label, 9);
    using PdfGraphics g = page.CreateGraphics();   // origin top left, y down, points
    g.DrawString(label, font, 9, PdfColor.Gray(0.35), (page.Width - width) / 2, page.Height - 30);
}
doc.Save("report-numbered.pdf");
