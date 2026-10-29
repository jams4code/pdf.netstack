#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

// A page with two columns of prose whose lines share their baselines.
PdfDocument made = PdfDocument.Create();
PdfPage madePage = made.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = madePage.CreateGraphics())
{
    string[] left = ["Left column, line one of", "the text that reads first,", "before the right column", "that shares its rows."];
    string[] right = ["Right column, line one of", "the text that reads after", "the left column is done,", "from top to bottom."];
    for (int i = 0; i < left.Length; i++)
    {
        g.DrawString(left[i], PdfStandardFont.TimesRoman, 11, PdfColor.Black, 72, 100 + i * 14);
        g.DrawString(right[i], PdfStandardFont.TimesRoman, 11, PdfColor.Black, 320, 100 + i * 14);
    }
}
made.Save("columns.pdf");

#region columns
PdfDocument doc = PdfDocument.Open("columns.pdf");
Console.WriteLine(doc.Pages[0].ExtractText());
#endregion
