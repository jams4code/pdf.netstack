#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: hello hello.pdf 0.3
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

#region short
PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())          // origin top left, y down, points
{
    g.DrawRectangle(null, PdfColor.FromHex("#1E3A8A"), 0, 0, page.Width, 120);
    g.DrawString("Hello, PDF", PdfStandardFont.HelveticaBold, 32, PdfColor.White, 40, 75);
    g.DrawString("Made with PdfNetStack.", PdfStandardFont.Helvetica, 12, PdfColor.Black, 40, 160);
}
doc.Save("hello.pdf");
#endregion
