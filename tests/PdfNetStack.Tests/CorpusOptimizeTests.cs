namespace PdfNetStack.Tests;

/// <summary>
/// Opt-in: set PDFNETSTACK_CORPUS to a folder of real PDFs. Every file is optimized with every
/// safety rule relaxed, on whichever runtime runs the tests, and no rewrite may fail verification
/// or throw. Runs on .NET Framework too, which exercises the .NET Standard 2.0 build.
/// </summary>
[TestClass]
public sealed class CorpusOptimizeTests
{
    [TestMethod]
    public void EveryCorpusDocumentOptimizesOrIsRefusedCleanly()
    {
        string? folder = Environment.GetEnvironmentVariable("PDFNETSTACK_CORPUS");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Inconclusive("Set PDFNETSTACK_CORPUS to run.");
            return;
        }

        var options = new PdfOptimizerOptions { AllowIncrementalUpdates = true, AllowRepairedFiles = true };
        var failures = new List<string>();
        int optimized = 0, total = 0;
        long before = 0, after = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories))
        {
            byte[] input = File.ReadAllBytes(path);
            (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, options);
            total++;
            before += input.Length;
            after += output.Length;
            if (result.Disposition == OptimizationDisposition.Optimized) optimized++;
            if (result.Reason.StartsWith("Verification failed", StringComparison.Ordinal) || result.Reason.StartsWith("Rewrite failed", StringComparison.Ordinal))
            {
                failures.Add(Path.GetFileName(path) + ": " + result.Reason);
            }
        }

        Console.WriteLine($"{total} documents, {optimized} optimized, {before:N0} -> {after:N0} bytes");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures.Take(20)));
    }
}
