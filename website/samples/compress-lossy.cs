#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;

// A brochure page: a 1600 pixel photograph stored losslessly and drawn 200 points wide, which is
// 576 dpi, plus a JPEG photograph.
PdfDocument brochure = PdfDocument.Create();
PdfPage page = brochure.Pages.Add(PdfRectangle.A4);
using (PdfGraphics g = page.CreateGraphics())
{
    g.DrawImage(PdfImage.Load("photo-large.png"), 72, 72, 200, 125);
    g.DrawImage(PdfImage.Load("photo.jpg"), 72, 240, 320, 200);
}
brochure.Save("brochure.pdf", new PdfSaveOptions { Optimize = false });

#region lossy
var options = new PdfOptimizerOptions
{
    MaxImageDpi = 150,              // downsample images drawn above 225 dpi to 150 dpi
    ConvertImagesToJpeg = true,     // photographs stored raw become JPEG
    JpegQuality = 90,               // new JPEGs, and existing ones finer than this are requantized
    MinImageSsim = 0.95,            // keep any image a change would visibly hurt
    ProgressiveJpeg = true,         // lossless: progressive coding when smaller
};
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("brochure.pdf", "brochure-small.pdf", options);
#endregion

#region report
OptimizationStatistics stats = result.Statistics;
Console.WriteLine($"{result.OriginalBytes:N0} to {result.OutputBytes:N0} bytes ({result.SavingPercent:F1}% saved)");
Console.WriteLine($"Downsampled: {stats.ImagesDownsampled}, converted to JPEG: {stats.ImagesConvertedToJpeg}, " +
    $"requantized: {stats.JpegImagesRequantized}, kept by the quality gate: {stats.ImagesKeptByQualityGate}");
Console.WriteLine(result.Reason);
#endregion

#region exhaustive
// Lossless, for archives: a slower Deflate search, kept only where it beats zlib.
var archival = new PdfOptimizerOptions { Compression = CompressionEffort.Exhaustive };
PdfOptimizationResult archived = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual-archive.pdf", archival);
Console.WriteLine($"Exhaustive: {archived.OriginalBytes:N0} to {archived.OutputBytes:N0} bytes");
#endregion

#region check
// The same check on any tool's output: accept downsampled and converted images that keep SSIM 0.95.
string? difference = PdfEquivalence.FindDifference(
    File.ReadAllBytes("brochure.pdf"), File.ReadAllBytes("brochure-small.pdf"),
    maxDecodedStreamBytes: 256L << 20, allowJpegRequantization: true, minImageSsim: 0.95);
Console.WriteLine(difference ?? "Within the bounds of the lossy proof.");
#endregion
