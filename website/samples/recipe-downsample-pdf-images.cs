#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;

// A page with a 1600 pixel wide photograph drawn 3 inches wide: about 530 dpi.
PdfDocument input = PdfDocument.Create();
using (PdfGraphics g = input.Pages.Add(PdfRectangle.A4).CreateGraphics())
{
    g.DrawImage(PdfImage.Load("photo-large.png"), 72, 72, 216, 135);
}
input.Save("oversampled.pdf", new PdfSaveOptions { Optimize = false });

#region short
var options = new PdfOptimizerOptions { MaxImageDpi = 150 };
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("oversampled.pdf", "downsampled.pdf", options);
Console.WriteLine($"{result.Statistics.ImagesDownsampled} image(s) downsampled, {result.SavingPercent:F1}% saved");
Console.WriteLine(result.Reason);
#endregion
