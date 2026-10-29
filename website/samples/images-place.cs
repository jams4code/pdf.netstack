#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
// preview: images images.pdf 0.48
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

File.WriteAllText("badge.svg", """
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 80">
      <defs>
        <linearGradient id="bg" x1="0" x2="1">
          <stop offset="0" stop-color="#0F766E"/><stop offset="1" stop-color="#14B8A6"/>
        </linearGradient>
      </defs>
      <rect width="200" height="80" rx="16" fill="url(#bg)"/>
      <circle cx="40" cy="40" r="22" fill="#FFFFFF" opacity="0.85"/>
      <text x="74" y="50" font-family="Helvetica" font-size="26" fill="#FFFFFF">Vector</text>
    </svg>
    """);

PdfDocument doc = PdfDocument.Create();
PdfPage page = doc.Pages.Add(PdfRectangle.A4);
using PdfGraphics g = page.CreateGraphics();

#region place
PdfImage photo = PdfImage.Load("photo.jpg");      // JPEG copied as is, EXIF orientation honored
PdfImage logo = PdfImage.Load("logo.png");        // PNG alpha becomes a soft mask
PdfSvg badge = PdfSvg.Load("badge.svg");          // SVG stays vector: sharp at any zoom

g.DrawImage(photo, 40, 40, 320, 200);             // stretched to the rectangle, in points
g.DrawImage(logo, 380, 40, 150, 50);
g.DrawSvg(badge, 380, 110, 150, 60);              // viewBox and preserveAspectRatio decide the fit
g.DrawImage(logo, 40, 290);                       // natural size: pixels at their recorded resolution, else 96 dpi
#endregion

#region size
Console.WriteLine($"photo.jpg: {photo.PixelWidth}x{photo.PixelHeight} pixels, {photo.Width:F0}x{photo.Height:F0} points at natural size");
#endregion

g.DrawString("JPEG, PNG with transparency, and SVG", PdfStandardFont.Helvetica, 12, PdfColor.Black, 40, 265);
doc.Save("images.pdf");
