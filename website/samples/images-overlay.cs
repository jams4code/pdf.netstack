#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: images-nup handout.pdf 1
// preview: images-watermark report-watermarked.pdf 1
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

#region watermark
PdfDocument report = PdfDocument.Open("report.pdf");

// Draw the stamp once as a reusable form; every page then refers to the same object.
PdfFont font = PdfStandardFont.HelveticaBold;
PdfForm stamp = PdfForm.Create(report, font.MeasureString("CONFIDENTIAL", 56), 56);
using (PdfGraphics s = stamp.CreateGraphics())
{
    s.DrawString("CONFIDENTIAL", font, 56, PdfColor.FromHex("#DC2626").WithAlpha(0.25), 0, font.Ascent * 56);
}

foreach (PdfPage page in report.Pages)
{
    using PdfGraphics g = page.CreateGraphics();          // foreground: over the existing content
    g.TranslateTransform(page.Width / 2, page.Height / 2);    // origin at the page center
    g.RotateTransform(-45);                                    // counterclockwise
    g.DrawForm(stamp, -stamp.Width / 2, -stamp.Height / 2);
}
report.Save("report-watermarked.pdf");
#endregion

#region nup
// Four pages of the manual on one landscape sheet: a handout.
PdfDocument manual = PdfDocument.Open("manual.pdf");
PdfDocument handout = PdfDocument.Create();
PdfPage sheet = handout.Pages.Add(PdfRectangle.FromSize(842, 595));
using (PdfGraphics g = sheet.CreateGraphics())
{
    double w = sheet.Width / 2, h = sheet.Height / 2;
    for (int i = 0; i < 4; i++)
    {
        PdfForm miniature = PdfForm.FromPage(handout, manual.Pages[i]);   // fonts and images copied once
        double x = i % 2 * w, y = i / 2 * h;
        double scale = Math.Min((w - 20) / miniature.Width, (h - 20) / miniature.Height);
        double mw = miniature.Width * scale, mh = miniature.Height * scale;
        g.DrawForm(miniature, x + (w - mw) / 2, y + (h - mh) / 2, mw, mh);
        g.DrawRectangle(new PdfPen(PdfColor.Gray(0.7), 0.5), null, x + (w - mw) / 2, y + (h - mh) / 2, mw, mh);
    }
}
handout.Save("handout.pdf");
#endregion

#region letterhead
// A page of another PDF as the background of every page: a letterhead designed elsewhere.
PdfDocument letterhead = PdfDocument.Create();
PdfPage design = letterhead.Pages.Add(PdfRectangle.A4);
using (PdfGraphics d = design.CreateGraphics())
{
    d.DrawRectangle(null, PdfColor.FromHex("#0F766E"), 0, 0, design.Width, 24);
    d.DrawImage(PdfImage.Load("logo.png"), 455, 40, 100, 33);
}

PdfDocument contract = PdfDocument.Open("contract.pdf");
PdfForm background = PdfForm.FromPage(contract, letterhead.Pages[0]);
foreach (PdfPage page in contract.Pages)
{
    using PdfGraphics g = page.CreateGraphics(PdfGraphicsLayer.Background);
    g.DrawForm(background, 0, 0, page.Width, page.Height);
}
contract.Save("contract-letterhead.pdf");
#endregion
