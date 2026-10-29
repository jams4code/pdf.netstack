#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: fonts fonts.pdf 0.35
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using PdfGraphics g = page.CreateGraphics();

#region standard
// The 14 standard fonts need no embedding: every PDF viewer has them.
PdfFont bold = PdfStandardFont.HelveticaBold;
g.DrawString("Invoice 2026-114", bold, 24, PdfColor.Black, 40, 60);   // x, then the baseline y

// Measure to align: right edge at 555 points.
double width = bold.MeasureString("Total: €1,250.00", 14);
g.DrawString("Total: €1,250.00", bold, 14, PdfColor.Black, 555 - width, 60);
#endregion

#region embed
// Any TrueType or OpenType font: .ttf, .otf, .ttc, .woff or .woff2. Only the glyphs drawn are embedded.
PdfFont inter = PdfOpenTypeFont.Load("fonts/Inter-Regular.woff2");
g.DrawString("Inter, embedded as a subset, kerned and searchable.", inter, 14, PdfColor.FromHex("#1E3A8A"), 40, 100);
#endregion

#region collection
var fonts = new PdfFontCollection();
fonts.AddDirectory("fonts");                                   // reads names and styles only
PdfFont? found = fonts.Find("Inter", weight: 700);          // null when the family is unknown
PdfFont interBold = found ?? PdfStandardFont.HelveticaBold;
PdfFont arabic = fonts.Find("Noto Sans Arabic") ?? throw new InvalidOperationException("Font missing");
#endregion

#region paragraph
var body = new PdfTextStyle(inter, 11);
var paragraph = new PdfParagraph { Alignment = PdfTextAlignment.Justify, LineHeight = 1.5 }
    .Add("Payment is due within ", body)
    .Add("30 days", body with { Font = interBold })
    .Add(" of the invoice date. Late payments carry interest at the legal rate, and reminders are sent ", body)
    .Add("by email", body with { Underline = true, Brush = PdfColor.FromHex("#2563EB") })
    .Add(". Thank you for your business.", body);
PdfTextLayout layout = g.DrawText(paragraph, 40, 130, 300);   // top left corner and width
#endregion

#region rtl
var arabicStyle = new PdfTextStyle(arabic, 16) { Fallbacks = [inter] };   // the full stop comes from Inter
var rtl = new PdfParagraph { Direction = PdfTextDirection.RightToLeft }
    .Add("مرحبا بالعالم، هذا نص عربي يقرأ من اليمين إلى اليسار.", arabicStyle);
g.DrawText(rtl, 40, 140 + layout.Height + 20, 300);          // start aligned: on the right
#endregion

#region metrics
double size = 11;
Console.WriteLine($"Paragraph: {layout.Lines.Count} lines, {layout.Height:F1} points tall");
Console.WriteLine($"Inter at {size} pt: ascent {inter.Ascent * size:F2}, descent {inter.Descent * size:F2}, line height {inter.GetLineHeight(size):F2}");
#endregion

doc.Save("fonts.pdf");
