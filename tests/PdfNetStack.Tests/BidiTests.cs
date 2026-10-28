using System.Globalization;
using PdfNetStack.Text;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class BidiTests
{
    /// <summary>
    /// Every case of the Unicode 16 BidiCharacterTest.txt: code points; paragraph direction (0 LTR,
    /// 1 RTL, 2 auto); resolved paragraph level; levels (x for characters X9 removes); visual order.
    /// </summary>
    [TestMethod]
    public void PassesTheUnicodeConformanceTest()
    {
        int cases = 0;
        var failures = new List<string>();
        foreach (string line in LineBreakerTests.UnicodeTestLines("BidiCharacterTest.txt"))
        {
            string[] f = line.Split(';');
            int[] cps = [.. f[0].Split(' ').Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
            var bidi = Bidi.Analyze(cps, int.Parse(f[1], CultureInfo.InvariantCulture));
            byte[] levels = bidi.LineLevels(0, cps.Length);

            string actualLevels = string.Join(" ", levels.Select((l, i) => bidi.Removed[i] ? "x" : l.ToString(CultureInfo.InvariantCulture)));
            string actualOrder = string.Join(" ", Bidi.Reorder(levels).Where(i => !bidi.Removed[i]));
            cases++;
            if ((bidi.ParagraphLevel.ToString(CultureInfo.InvariantCulture) != f[2] || actualLevels != f[3] || actualOrder != f[4]) && failures.Count < 15)
            {
                failures.Add($"{line}\n   got {bidi.ParagraphLevel};{actualLevels};{actualOrder}");
            }
        }

        Assert.IsGreaterThan(90000, cases);
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }

    [TestMethod]
    public void ArabicWithNumbersAndLatin()
    {
        // "قال 123 Hello." in a right to left paragraph: the Latin word and the number keep their order.
        int[] cps = [.. "قال 123 Hello.".Select(c => (int)c)];
        var bidi = Bidi.Analyze(cps, 2);
        Assert.AreEqual(1, bidi.ParagraphLevel);
        int[] order = Bidi.Reorder(bidi.LineLevels(0, cps.Length));
        string visual = new([.. order.Select(i => (char)cps[i])]);
        Assert.AreEqual(".Hello 123 لاق", visual);
    }
}
