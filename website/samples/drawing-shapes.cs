#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: drawing drawing.pdf 0.6
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

#region canvas
PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);           // 595 x 842 points
using PdfGraphics g = page.CreateGraphics();            // origin top left, y down, points
#endregion

#region shapes
// Pen first (or null for no outline), then brush (or null for no fill).
g.DrawRectangle(null, PdfColor.FromHex("#1E3A8A"), 0, 0, g.Width, 90);
g.DrawRoundedRectangle(new PdfPen(PdfColor.FromHex("#CBD5E1")), PdfColor.White, 40, 120, 250, 150, 12);
g.DrawEllipse(null, PdfColor.FromHex("#14B8A6"), 70, 150, 90, 90);
g.DrawLine(new PdfPen(PdfColor.Black, 2) { DashPattern = [6, 3], LineCap = PdfLineCap.Round }, 40, 300, 290, 300);

var path = new PdfPath()
    .MoveTo(180, 240)
    .CurveTo(200, 150, 250, 150, 270, 240)
    .Close();
g.DrawPath(new PdfPen(PdfColor.FromHex("#7C3AED"), 3) { LineJoin = PdfLineJoin.Round }, null, path);
#endregion

#region colors
var gradient = new PdfLinearGradientBrush(new PdfPoint(310, 0), new PdfPoint(555, 0),
    PdfColor.FromHex("#2563EB"), PdfColor.FromHex("#DB2777"));
g.DrawRoundedRectangle(null, gradient, 310, 120, 245, 70, 12);

var glow = new PdfRadialGradientBrush(new PdfPoint(370, 245), 50, PdfColor.White, PdfColor.FromHex("#F59E0B"));
g.DrawEllipse(null, glow, 320, 195, 100, 100);

g.DrawEllipse(null, PdfColor.Cmyk(0, 0.9, 0.85, 0), 440, 200, 80, 80);              // CMYK, for print
using (g.Save())
{
    g.BlendMode = PdfBlendMode.Multiply;                                             // one of the 16 PDF blend modes
    g.DrawEllipse(null, PdfColor.Rgb(0.1, 0.6, 0.9, alpha: 0.8), 480, 220, 60, 60);   // 80% opaque
}
#endregion

#region transforms
using (g.Save())                                     // transform, clip and colors come back after the block
{
    g.TranslateTransform(165, 400);
    for (int i = 0; i < 12; i++)
    {
        g.RotateTransform(30);
        g.DrawRectangle(null, PdfColor.FromHex("#0EA5E9").WithAlpha(0.35), 0, -6, 90, 12);
    }
}

using (g.Save())
{
    g.IntersectClip(new PdfPath().AddCircle(430, 400, 70));   // nothing outside the circle shows
    for (double x = 300; x < 540; x += 14)
    {
        g.DrawLine(new PdfPen(PdfColor.FromHex("#1E3A8A"), 6), x, 320, x + 60, 480);
    }
}
#endregion

g.DrawString("Drawing with PdfNetStack", PdfStandardFont.HelveticaBold, 24, PdfColor.White, 40, 55);   // x, baseline y
doc.Save("drawing.pdf");                                 // writes the drawing into the page

