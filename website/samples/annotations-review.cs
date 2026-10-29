#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: annotations-review reviewed.pdf 0.62
using System.Text;
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfDocument doc = PdfDocument.Open("report.pdf");
PdfPage page = doc.Pages[0];

#region stamps
// One of the fourteen standard stamps, and a custom one drawn on a canvas of the stamp's size.
page.Annotations.AddStamp(PdfStampName.Approved, 400, 30, 150, 45, new PdfAnnotationOptions { Opacity = 0.85 });
page.Annotations.AddStamp(400, 85, 150, 40, g =>
{
    g.DrawRoundedRectangle(new PdfPen(PdfColor.FromHex("#7C3AED"), 2), null, 1, 1, 148, 38, 6);
    g.DrawString("RECEIVED", PdfStandardFont.HelveticaBold, 18, PdfColor.FromHex("#7C3AED"), 26, 26);
}, "Received");
#endregion

#region shapes
var red = new PdfAnnotationOptions { Color = PdfColor.FromHex("#DC2626"), BorderWidth = 2, Comment = "Wrong figure" };
page.Annotations.AddSquare(66, 196, 420, 22, red);
page.Annotations.AddCircle(66, 250, 140, 40, new PdfAnnotationOptions
{
    Color = PdfColor.FromHex("#2563EB"),
    InteriorColor = PdfColor.FromHex("#2563EB"),
    Opacity = 0.25,
});
page.Annotations.AddLine(new PdfPoint(240, 320), new PdfPoint(300, 285), new PdfAnnotationOptions
{
    BorderWidth = 2,
    EndEnding = PdfLineEnding.ClosedArrow,
    InteriorColor = PdfColor.FromHex("#DC2626"),
    Comment = "See this paragraph",
});
page.Annotations.AddPolygon([new PdfPoint(330, 330), new PdfPoint(420, 330), new PdfPoint(375, 380)],
    new PdfAnnotationOptions { Color = PdfColor.FromHex("#059669"), DashPattern = [4, 2] });
page.Annotations.AddPolyline([new PdfPoint(450, 380), new PdfPoint(480, 340), new PdfPoint(510, 370), new PdfPoint(540, 330)],
    new PdfAnnotationOptions { Color = PdfColor.FromHex("#059669"), StartEnding = PdfLineEnding.Circle });
#endregion

#region ink
// Freehand strokes, each a list of points from the page's top left corner.
var stroke = Enumerable.Range(0, 40).Select(i => new PdfPoint(80 + i * 8, 420 + 10 * Math.Sin(i / 3.0)));
page.Annotations.AddInk([stroke], new PdfAnnotationOptions { Color = PdfColor.FromHex("#EA580C"), BorderWidth = 2.5 });
#endregion

#region attachment
// A file embedded at one spot on the page, behind an icon.
byte[] csv = Encoding.UTF8.GetBytes("quarter,revenue\nQ1,1200\nQ2,1350\n");
page.Annotations.AddFileAttachment(520, 250, "figures.csv", csv, "text/csv", PdfAttachmentIcon.Paperclip,
    new PdfAnnotationOptions { Comment = "Source figures" });
doc.Save("reviewed.pdf");
#endregion

#region read
foreach (PdfAnnotation annotation in PdfDocument.Open("reviewed.pdf").Pages[0].Annotations)
{
    string file = annotation.Attachment is PdfAttachment a ? $"{a.FileName}, {a.GetData().Length} bytes" : "";
    Console.WriteLine($"{annotation.Subtype,-14} {annotation.Contents} {file}");
}
#endregion

#region flatten
// Every markup annotation becomes page content: it prints and shows the same everywhere,
// and can no longer be edited or moved. Links and form fields stay unless options ask for them.
PdfDocument flat = PdfDocument.Open("reviewed.pdf");
int count = flat.FlattenAnnotations();
flat.Save("reviewed-flat.pdf");
Console.WriteLine($"Flattened {count} annotations");
#endregion
