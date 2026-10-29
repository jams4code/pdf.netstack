#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.1
using PdfNetStack;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

// Two letters set in Inter, merged and saved without the optimizer. The second page's resources
// also list the first page's font, which its content never uses, as shared resource
// dictionaries in merged files often do.
PdfDocument Letter(string text)
{
    PdfDocument doc = PdfDocument.Create();
    PdfPage page = doc.Pages.Add(PdfRectangle.A4);
    using PdfGraphics g = page.CreateGraphics();
    g.DrawString(text, PdfOpenTypeFont.Load("fonts/Inter-Regular.woff2"), 14, PdfColor.Black, 72, 100);
    return doc;
}
PdfDocument merged = PdfDocument.Merge([Letter("First letter, set in Inter."), Letter("Second letter, also in Inter.")]);
PdfDictionary FontsOf(PdfPage page) =>
    (PdfDictionary)merged.Resolve(((PdfDictionary)merged.Resolve(page.Dictionary["Resources"])!)["Font"])!;
foreach (string name in FontsOf(merged.Pages[0]).Keys.ToList())
{
    FontsOf(merged.Pages[1]).Set("Unused" + name, FontsOf(merged.Pages[0])[name]!);
}
merged.Save("merged.pdf", new PdfSaveOptions { Optimize = false });

#region structure
PdfOptimizationResult result = await PdfOptimizer.OptimizeFileAsync("merged.pdf", "merged.optimized.pdf");
OptimizationStatistics stats = result.Statistics;
Console.WriteLine($"{result.Disposition}, saved {result.SavingPercent:F1}%");
Console.WriteLine($"Resources pruned: {stats.ResourcesPruned}");
Console.WriteLine($"Content streams compacted: {stats.ContentStreamsCompacted}");
#endregion

#region linearize
var webView = new PdfOptimizerOptions { Linearize = true };
PdfOptimizationResult linear = await PdfOptimizer.OptimizeFileAsync("manual.pdf", "manual.web.pdf", webView);
Console.WriteLine($"Linearized: {linear.Statistics.Linearized}, {linear.OriginalBytes:N0} to {linear.OutputBytes:N0} bytes");
#endregion
