#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

#region units
// One point is 1/72 inch. Convert once, draw in points.
static double Mm(double millimetres) => millimetres * 72 / 25.4;
static double Inches(double inches) => inches * 72;

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);                 // 595 x 842 points, about 210 x 297 mm
using (PdfGraphics g = page.CreateGraphics())
{
    // A box 20 mm from the left and 30 mm from the top: y grows downward on the canvas.
    g.DrawRectangle(PdfPen.Black, null, Mm(20), Mm(30), Mm(80), Mm(15));
    g.DrawString("Hello", PdfStandardFont.Helvetica, 12, PdfColor.Black, Mm(20) + 6, Mm(30) + 26);
    g.DrawLine(PdfPen.Black, Inches(1), Inches(2), Inches(3), Inches(2));
}
doc.Save("coordinates.pdf");
#endregion

#region readback
// Extraction reports default user space: origin bottom left, y up.
PdfWord word = PdfDocument.Open("coordinates.pdf").Pages[0].GetWords()[0];
Console.WriteLine($"{word.Text}: left {word.Bounds.Left:F1}, bottom {word.Bounds.Bottom:F1}, top {word.Bounds.Top:F1}");

// Back to canvas coordinates: y from the top is the page height minus y from the bottom.
double canvasTop = page.MediaBox.Top - word.Bounds.Top;
Console.WriteLine($"On the canvas the word's top is {canvasTop:F1} points from the top");
#endregion
