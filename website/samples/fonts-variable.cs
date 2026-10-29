#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: fonts-variable variable.pdf 0.33
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

// fonts/Variable.ttf is the repository's synthetic variable font (tools/make_variable_font_fixtures.py):
// a weight axis from 100 to 900, five named instances, and the glyphs of "HOT TO". Any variable
// TrueType or CFF2 font works the same way.

#region axes
var font = PdfOpenTypeFont.Load("fonts/Variable.ttf");
Console.WriteLine($"Variable: {font.IsVariable}");
foreach (PdfFontAxis axis in font.VariationAxes)
{
    Console.WriteLine($"{axis.Tag} ({axis.Name}): {axis.Minimum} to {axis.Maximum}, default {axis.Default}");
}
Console.WriteLine($"Named instances: {string.Join(", ", font.NamedInstances)}");
#endregion

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())
{
    #region instances
    PdfOpenTypeFont thin = font.GetInstance("Thin");
    PdfOpenTypeFont bold = font.GetInstance("Bold");
    PdfOpenTypeFont custom = font.GetInstance(new Dictionary<string, double> { ["wght"] = 550 });

    g.DrawString("HOT TO", thin, 40, PdfColor.Black, 40, 70);
    g.DrawString("HOT TO", font, 40, PdfColor.Black, 40, 130);      // the default instance
    g.DrawString("HOT TO", custom, 40, PdfColor.Black, 40, 190);
    g.DrawString("HOT TO", bold, 40, PdfColor.Black, 40, 250);
    #endregion
}
doc.Save("variable.pdf");
