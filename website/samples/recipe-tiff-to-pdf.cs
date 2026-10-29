#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;

PdfDocument doc = PdfDocument.Create();
int pages = PdfImage.GetPageCount("scan.tif");
for (int i = 0; i < pages; i++)
{
    PdfImage image = PdfImage.Load("scan.tif", i);
    // A page the size of the image at its recorded resolution, in points.
    PdfPage page = doc.Pages.Add(PdfRectangle.FromSize(image.Width, image.Height));
    using PdfGraphics g = page.CreateGraphics();
    g.DrawImage(image, 0, 0);
}
doc.Save("scan.pdf");
Console.WriteLine($"{pages} pages written");
