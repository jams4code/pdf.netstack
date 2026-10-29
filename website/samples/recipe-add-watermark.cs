#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: recipe-watermark contract-draft.pdf 0.6
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfDocument doc = PdfDocument.Open("contract.pdf");
PdfFont font = PdfStandardFont.HelveticaBold;
double size = 110;
double width = font.MeasureString("DRAFT", size);
foreach (PdfPage page in doc.Pages)
{
    using PdfGraphics g = page.CreateGraphics();
    g.RotateAtTransform(-45, page.Width / 2, page.Height / 2);   // positive angles turn clockwise
    g.DrawString("DRAFT", font, size, PdfColor.FromHex("#DC2626").WithAlpha(0.25),
        (page.Width - width) / 2, page.Height / 2 + size * 0.35);
}
doc.Save("contract-draft.pdf");
