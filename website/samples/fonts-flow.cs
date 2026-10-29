#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

string text = string.Join(" ", Enumerable.Repeat(
    "Long text continues on the next page when it does not fit: lay it out once, then draw it page by page.", 120));

#region flow
var style = new PdfTextStyle(PdfStandardFont.TimesRoman, 12);
var paragraph = new PdfParagraph { Alignment = PdfTextAlignment.Justify }.Add(text, style);
PdfTextLayout layout = PdfTextLayout.Create(paragraph, 595 - 2 * 72);   // measure before drawing

PdfDocument doc = PdfDocument.Create();
const double top = 72, bottom = 842 - 72;
int next = 0;
while (next < layout.Lines.Count)
{
    // As many lines as fit between the margins, measured from the first line of this page.
    double pageTop = layout.Lines[next].Top;
    int count = 0;
    while (next + count < layout.Lines.Count
        && layout.Lines[next + count].Top + layout.Lines[next + count].Height - pageTop <= bottom - top)
    {
        count++;
    }

    PdfPage page = doc.Pages.Add(PdfRectangle.A4);
    using PdfGraphics g = page.CreateGraphics();
    g.DrawText(layout, 72, top, next, count);
    next += count;
}
doc.Save("flow.pdf");
Console.WriteLine($"{layout.Lines.Count} lines on {doc.Pages.Count} pages");
#endregion
