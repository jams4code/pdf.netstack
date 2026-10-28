using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfNetStack.Text;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class LineBreakerTests
{
    internal static IEnumerable<string> UnicodeTestLines(string name)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Unicode", name + ".gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is string line)
        {
            string data = line.Split('#')[0].Trim();
            if (data.Length > 0) yield return data;
        }
    }

    /// <summary>Every case of the Unicode 16 LineBreakTest.txt: "× 0041 ÷ 0020 ..." with ÷ where a break is allowed.</summary>
    [TestMethod]
    public void PassesTheUnicodeConformanceTest()
    {
        int cases = 0;
        var failures = new List<string>();
        foreach (string line in UnicodeTestLines("LineBreakTest.txt"))
        {
            string[] parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            var text = new StringBuilder();
            var expected = new List<int>();
            for (int i = 1; i < parts.Length; i += 2)
            {
                text.Append(char.ConvertFromUtf32(int.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                if (parts[i + 1] == "÷") expected.Add(text.Length);
            }

            List<int> actual = LineBreaker.Breaks(text.ToString()).Select(b => b.Position).ToList();
            cases++;
            if (!expected.SequenceEqual(actual) && failures.Count < 20) failures.Add($"{line}\n   got breaks at {string.Join(",", actual)}");
        }

        Assert.IsGreaterThan(15000, cases);
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }

    [TestMethod]
    public void MarksHardBreaks()
    {
        List<LineBreak> breaks = LineBreaker.Breaks("one two\r\nthree\nfour");
        CollectionAssert.AreEqual(
            new[] { new LineBreak(4, false), new LineBreak(9, true), new LineBreak(15, true), new LineBreak(19, true) },
            breaks.ToArray());
        Assert.IsEmpty(LineBreaker.Breaks(""));
    }

    [TestMethod]
    public void KeepsNumbersPunctuationAndHyphenatedWordsSensible()
    {
        static int[] At(string s) => [.. LineBreaker.Breaks(s).Select(b => b.Position)];

        CollectionAssert.AreEqual(new[] { 7, 16 }, At("Total: $1,234.50"));       // a price stays whole
        CollectionAssert.AreEqual(new[] { 6, 11 }, At("well- known"));
        CollectionAssert.AreEqual(new[] { 5, 10 }, At("well-known"));               // after the hyphen only
        CollectionAssert.AreEqual(new[] { 6, 13 }, At("(see) [note]."));            // not before ) ] .
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, At("日本語"));                   // ideographs break anywhere
        CollectionAssert.AreEqual(new[] { 4, 5 }, At("a\u00A0b c"));               // not at a no-break space
    }
}
