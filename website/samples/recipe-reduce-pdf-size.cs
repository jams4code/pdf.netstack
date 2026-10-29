#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;

// A page with an oversized photograph stored losslessly, as scanners and screenshots leave them.
PdfDocument input = PdfDocument.Create();
using (PdfGraphics g = input.Pages.Add(PdfRectangle.A4).CreateGraphics())
{
    g.DrawImage(PdfImage.Load("photo-large.png"), 72, 72, 451, 282);
}
input.Save("brochure.pdf", new PdfSaveOptions { Optimize = false });

#region short
var options = new PdfOptimizerOptions
{
    MaxImageDpi = 150,            // no image sharper than the page needs
    ConvertImagesToJpeg = true,   // photographs stored raw become JPEG
    JpegQuality = 80,             // new JPEGs, and existing JPEGs requantized down to it
};
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("brochure.pdf", "brochure-email.pdf", options);
Console.WriteLine($"{result.Disposition}: {result.OriginalBytes:N0} to {result.OutputBytes:N0} bytes, {result.SavingPercent:F1}% saved");
Console.WriteLine(result.Reason);
#endregion
