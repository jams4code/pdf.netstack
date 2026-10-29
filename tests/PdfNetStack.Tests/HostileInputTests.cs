using System.Diagnostics;
using System.Text;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Regression tests for crafted inputs found in review. Each one used to crash, hang or merge wrongly.</summary>
[TestClass]
public sealed class HostileInputTests
{
    private static readonly PdfOptimizerOptions Permissive = new() { MinSavingPercent = 0, AllowRepairedFiles = true, AllowIncrementalUpdates = true };

    private static void AssertSafe(byte[] input, (byte[] Output, PdfOptimizationResult Result) run)
    {
        if (run.Result.Disposition == OptimizationDisposition.Optimized)
        {
            Assert.IsNull(PdfEquivalence.FindDifference(input, run.Output), run.Result.Reason);
        }
        else
        {
            CollectionAssert.AreEqual(input, run.Output);
        }
    }

    [TestMethod]
    public void LongChainOfIndirectStreamLengthsDoesNotOverflowTheStack()
    {
        const int chain = 5000;
        var b = PdfBuilder.Simple("x");
        int first = b.NextNumber;
        for (int i = 0; i < chain; i++)
        {
            int number = first + i;
            string length = i == chain - 1 ? "3" : $"{number + 1} 0 R";
            b.Set(number, $"<</Length {length}>>\nstream\nabc\nendstream");
        }

        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/X {first} 0 R>>");
        byte[] input = b.Build();

        AssertSafe(input, PdfOptimizer.Optimize(input, Permissive));
        AssertSafe(input, PdfOptimizer.Optimize(input));
    }

    [TestMethod]
    public void AnnotationsReachedThroughAnIndirectArrayAreNotMerged()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Annots 4 0 R/Contents 7 0 R>>");
        b.Set(4, "[5 0 R 6 0 R]");
        b.Set(5, "<</Subtype/Link/Border[0 0 0]/A<</S/URI/URI(https://example.com)>>>>");
        b.Set(6, "<</Subtype/Link/Border[0 0 0]/A<</S/URI/URI(https://example.com)>>>>");
        b.SetStream(7, "", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0 0 m 1 1 l S\n", 300))));
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, Permissive);

        Assert.AreEqual(OptimizationDisposition.Optimized, result.Disposition, result.Reason);
        Assert.AreEqual(0, result.Statistics.ObjectsDeduplicated);
        PdfFile file = PdfFile.Open(output, 64 << 20);
        var annots = (PdfArray)file.Resolve(file.ResolveDictionary(((PdfArray)file.ResolveDictionary(file.ResolveDictionary(file.Trailer["Root"])!["Pages"])!["Kids"]!)[0])!["Annots"])!;
        Assert.AreNotEqual(((PdfReference)annots[0]).Number, ((PdfReference)annots[1]).Number);
    }

    [TestMethod]
    public void VerifierRejectsTwoAnnotationsCollapsedIntoOne()
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Annots[4 0 R 5 0 R]>>");
        b.Set(4, "<</Subtype/Link/Rect[0 0 10 10]>>");
        b.Set(5, "<</Subtype/Link/Rect[0 0 10 10]>>");
        byte[] original = b.Build();

        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Annots[4 0 R 4 0 R]>>");
        byte[] collapsed = b.Build();

        StringAssert.Contains(PdfEquivalence.FindDifference(original, collapsed), "merged");
    }

    [TestMethod]
    public void HugePredictorRowOnATinyStreamDoesNotAllocateTheRow()
    {
        var b = PdfBuilder.Simple("x");
        byte[] tiny = PdfBuilder.Deflate([2, 1, 2, 3]);
        int s = b.AddStream("/Filter/FlateDecode/DecodeParms<</Predictor 15/Colors 32/BitsPerComponent 16/Columns 16777216>>", tiny);
        b.Set(1, $"<</Type/Catalog/Pages 2 0 R/X {s} 0 R>>");
        byte[] input = b.Build();

#if NET8_0_OR_GREATER
        long before = GC.GetTotalAllocatedBytes(precise: true);
#endif
        AssertSafe(input, PdfOptimizer.Optimize(input, Permissive with { MaxDegreeOfParallelism = 1 }));
#if NET8_0_OR_GREATER
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.IsLessThan(64L << 20, allocated, $"allocated {allocated:N0} bytes");
#endif
    }

    [TestMethod]
    [DoNotParallelize] // timing: other tests' allocations would land in the measurement
    public void ManyUnterminatedObjectsReconstructInLinearTime()
    {
        // Compare scaling, not wall time: eight times the input should take about eight times as
        // long, where quadratic work would take sixty four. Each size keeps its fastest of three
        // interleaved runs, so a pause or a burst of other work on a busy machine lands in a run
        // that does not count. Both sizes take long enough for the clock to resolve them.
        static double Run(int size)
        {
            var sb = new StringBuilder("%PDF-1.4\n");
            for (int k = 1; sb.Length < size; k++) sb.Append(k).Append(" 0 obj (\n");
            byte[] input = Compat.Latin1.GetBytes(sb.ToString());
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var clock = Stopwatch.StartNew();
            AssertSafe(input, PdfOptimizer.Optimize(input, Permissive));
            return Math.Max(1, clock.Elapsed.TotalMilliseconds);
        }

        Run(100_000);
        double small = double.MaxValue, large = double.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            small = Math.Min(small, Run(100_000));
            large = Math.Min(large, Run(800_000));
        }

        Assert.IsLessThan(32.0, large / small, $"100 KB took {small:F0} ms, 800 KB took {large:F0} ms");
        Assert.IsLessThan(60_000, large);
    }

    [TestMethod]
    public void GenerationMismatchIsTreatedAsDamage()
    {
        var b = PdfBuilder.Simple("x");
        b.SetStream(4, "", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0 0 m 1 1 l S\n", 300))));
        b.Set(5, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 2 R>>>>/Contents 4 0 R>>");
        byte[] input = b.Build();

        (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, new PdfOptimizerOptions { MinSavingPercent = 0 });
        Assert.AreEqual(OptimizationDisposition.UnsupportedRetained, result.Disposition);
        CollectionAssert.AreEqual(input, output);
    }
}
