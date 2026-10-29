#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: annotations annotated.pdf 0.45
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

#region add
PdfDocument doc = PdfDocument.Open("manual.pdf");
PdfPage page = doc.Pages[0];

// Same coordinates as drawing: points from the top left of the page, y down.
page.Annotations.AddLink(72, 95, 220, 20, new Uri("https://example.com/manual"));   // opens a web address
page.Annotations.AddLink(72, 160, 400, 14, doc.Pages[3], top: 100);                // goes to page 4
page.Annotations.AddTextMarkup(PdfTextMarkup.Highlight, 70, 178, 410, 16, comment: "Check this line");
page.Annotations.AddTextMarkup(PdfTextMarkup.StrikeOut, 70, 196, 410, 16);
page.Annotations.AddNote(500, 150, "Please review section 1.2", author: "Reviewer");
page.Annotations.AddFreeText(330, 230, 200, 40, "Updated for version 2.",
    new PdfTextStyle(PdfStandardFont.Helvetica, 11), border: PdfColor.FromHex("#2563EB"), fill: PdfColor.FromHex("#EFF6FF"));
#endregion

#region canvas
// A link drawn with the canvas follows its transform.
using (PdfGraphics g = page.CreateGraphics())
{
    g.TranslateTransform(470, 330);
    g.DrawString("Website", PdfStandardFont.Helvetica, 12, PdfColor.FromHex("#2563EB"), 0, 12);
    g.AddLink(0, 0, 50, 16, new Uri("https://example.com"));
}
doc.Save("annotated.pdf");
#endregion

#region read
foreach (PdfAnnotation annotation in PdfDocument.Open("annotated.pdf").Pages[0].Annotations)
{
    string target = annotation.Uri ?? (annotation.Destination is PdfPage p ? $"page {p.Index + 1}" : "");
    Console.WriteLine($"{annotation.Subtype,-10} {annotation.Rectangle} {annotation.Contents} {target}");
}
#endregion
