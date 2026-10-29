#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: recipe-arabic arabic.pdf 0.2
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfFont arabic = PdfOpenTypeFont.Load("fonts/NotoSansArabic-Regular.woff2");   // embedded as a subset
var style = new PdfTextStyle(arabic, 20);

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())
{
    var paragraph = new PdfParagraph { Direction = PdfTextDirection.RightToLeft, LineHeight = 1.6 }
        .Add("يستحق المبلغ خلال ثلاثين يوما من تاريخ الفاتورة", style);
    g.DrawText(paragraph, 60, 60, page.Width - 120);   // top left corner and width
}
doc.Save("arabic.pdf");

Console.WriteLine(PdfDocument.Open("arabic.pdf").Pages[0].ExtractText());   // logical order
