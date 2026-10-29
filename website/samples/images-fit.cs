#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: images-fit fit.pdf 0.42
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())
{
    #region formats
    PdfImage webp = PdfImage.Load("photo.webp");     // lossy or lossless WebP, alpha as a soft mask
    PdfImage bmp = PdfImage.Load("logo.bmp");        // BMP, 1 to 32 bits per pixel
    PdfImage fax = PdfImage.Load("scan.tif", 1);     // page 2 of a multi-page TIFF, from 0
    Console.WriteLine($"{webp.Format} {webp.PixelWidth}x{webp.PixelHeight}, {bmp.Format}, {fax.Format}");
    Console.WriteLine($"scan.tif has {PdfImage.GetPageCount("scan.tif")} pages");
    #endregion

    PdfFont label = PdfStandardFont.Helvetica;
    var frame = new PdfPen(PdfColor.FromHex("#94A3B8"));
    double x = 40;
    #region fit
    // Each box is 120 by 120 points; the photo is 640 by 400 pixels.
    PdfImageFit[] fits = [PdfImageFit.Fill, PdfImageFit.Contain, PdfImageFit.Cover, PdfImageFit.None];
    foreach (PdfImageFit fit in fits)
    {
        g.DrawImage(webp, x, 60, 120, 120, fit, PdfImageAlignment.Center);
        g.DrawRectangle(frame, null, x, 60, 120, 120);
        g.DrawString(fit.ToString(), label, 10, PdfColor.Black, x, 196);
        x += 130;
    }

    // Aligned to the top left of its box, and faded to 40 percent.
    g.DrawImage(webp, 40, 220, 250, 120, PdfImageFit.Contain, PdfImageAlignment.TopLeft, opacity: 0.4);
    #endregion
    g.DrawRectangle(frame, null, 40, 220, 250, 120);
    g.DrawImage(bmp, 300, 220, 120, 40);
    g.DrawImage(fax, 440, 220, 85, 120, PdfImageFit.Contain);
    g.DrawRectangle(frame, null, 440, 220, 85, 120);
}
doc.Save("fit.pdf");
