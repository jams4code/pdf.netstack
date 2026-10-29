#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: drawing-background report-letterhead.pdf 0.3
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

#region background
PdfDocument doc = PdfDocument.Open("report.pdf");
foreach (PdfPage page in doc.Pages)
{
    // Background: drawn underneath the page's existing content, which stays on top.
    using PdfGraphics g = page.CreateGraphics(PdfGraphicsLayer.Background);
    g.DrawRectangle(null, PdfColor.FromHex("#E0F2FE"), 0, 0, g.Width, 160);
    g.DrawLine(new PdfPen(PdfColor.FromHex("#0284C7"), 2), 40, 160, g.Width - 40, 160);
    g.DrawString("Example Company, 1 Main Street", PdfStandardFont.Helvetica, 9, PdfColor.FromHex("#0369A1"), 400, 24);
}
doc.Save("report-letterhead.pdf");
#endregion
