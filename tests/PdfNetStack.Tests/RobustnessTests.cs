using System.Text;

namespace PdfNetStack.Tests;

/// <summary>
/// Mutation fuzzing. Whatever the input, the optimizer must not throw, and its output must be
/// either the input bytes or a rewrite that an independent check proves equivalent.
/// </summary>
[TestClass]
public sealed class RobustnessTests
{
    private static byte[] Seed()
    {
        PdfBuilder b = PdfBuilder.Simple("alpha", "beta");
        b.SetStream(4, "/Filter/FlateDecode", PdfBuilder.Deflate(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0 0 m 50 50 l S\n", 50)))));
        b.Add("<</Type/Annot/Subtype/Text/Contents(note \\(nested\\))/Rect[1 2 3 4]>>");
        return b.Build();
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, true)]
    [DataRow(4, true)]
    public void MutatedInputNeverEscapesTheSafetyNet(int seed, bool objectStreams)
    {
        byte[] baseline = objectStreams ? PdfBuilder.Simple("alpha", "beta").BuildWithXrefStream(new HashSet<int> { 1, 2, 3, 5 }) : Seed();
        var random = new Random(seed);
        var options = new PdfOptimizerOptions { MinSavingPercent = 0, AllowRepairedFiles = true, AllowIncrementalUpdates = true, MaxDegreeOfParallelism = 1 };

        // CI runs a short pass; set PDFNETSTACK_FUZZ_ITERATIONS for a long local soak.
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("PDFNETSTACK_FUZZ_ITERATIONS"), out int n) ? n : 150;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            byte[] input = Mutate(baseline, random);
            (byte[] output, PdfOptimizationResult result) = PdfOptimizer.Optimize(input, options);

            if (result.Disposition == OptimizationDisposition.Optimized)
            {
                Assert.IsNull(PdfEquivalence.FindDifference(input, output), $"iteration {iteration}: {result.Reason}");
            }
            else
            {
                CollectionAssert.AreEqual(input, output, $"iteration {iteration}: retained output must be the input");
            }
        }
    }

    private static byte[] Mutate(byte[] source, Random random)
    {
        var data = new List<byte>(source);
        int edits = random.Next(1, 6);
        for (int e = 0; e < edits && data.Count > 2; e++)
        {
            int at = random.Next(data.Count);
            switch (random.Next(6))
            {
                case 0:
                    data[at] = (byte)random.Next(256);
                    break;
                case 1:
                    data.RemoveRange(at, Math.Min(random.Next(1, 16), data.Count - at));
                    break;
                case 2:
                    data.InsertRange(at, Encoding.ASCII.GetBytes(Tokens[random.Next(Tokens.Length)]));
                    break;
                case 3:
                    data.RemoveRange(at, data.Count - at);
                    break;
                case 4:
                    data[at] = (byte)"()<>[]/%\n "[random.Next(10)];
                    break;
                default:
                    int from = random.Next(data.Count);
                    int len = Math.Min(random.Next(1, 64), data.Count - from);
                    data.InsertRange(at, data.GetRange(from, len));
                    break;
            }
        }

        return data.ToArray();
    }

    private static readonly string[] Tokens =
    [
        "<<", ">>", "[", "]", "(", ")", "endobj", "stream", "endstream", "1 0 R", "999999 0 R",
        "/Length 99999999", "/Length -5", "/Filter/FlateDecode", "/Filter/LZWDecode", "/Type/Page",
        "/Kids[2 0 R]", "/Prev 0", "xref", "trailer", "startxref", "%%EOF", "obj", "/DecodeParms<</Predictor 15/Columns 0>>",
    ];
}
