using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

namespace PdfNetStack.Tests;

/// <summary>
/// Kashida justification of Arabic lines with the synthetic Test-Arabic.ttf, whose tatweel is 200
/// units wide (2 points at size 10).
/// </summary>
[TestClass]
public sealed class KashidaTests
{
    private const string Beh = "\u0628", Lam = "\u0644", Meem = "\u0645";

    private static readonly PdfOpenTypeFont Arabic = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Arabic.ttf"));

    private static readonly PdfTextStyle Style = new(Arabic, 10);

    private static int Tatweel => Arabic.TryGetGlyph(0x0640, out int g) ? g : -1;

    private static PdfTextLayout Lay(string text, double width, bool kashida) =>
        PdfTextLayout.Create(new PdfParagraph { Alignment = PdfTextAlignment.Justify, Kashida = kashida }.Add(text, Style), width);

    /// <summary>The width two words and a space take, plus <paramref name="extra"/>: the third word does not fit.</summary>
    private static double TwoWords(string word, double extra) =>
        PdfTextLayout.Create(new PdfParagraph().Add(word + " " + word, Style), 1000).Lines[0].Width + extra;

    [TestMethod]
    public void ElongatesEachWordBeforeWideningSpaces()
    {
        string word = Beh + Beh + Beh;
        string text = string.Join(" ", Enumerable.Repeat(word, 4));
        double width = TwoWords(word, 3);
        PdfTextLayout plain = Lay(text, width, kashida: false);
        PdfTextLayout layout = Lay(text, width, kashida: true);

        PdfTextLine line = layout.Lines[0];
        Assert.AreEqual(width, line.Width, 1e-9);
        List<ShapedGlyph> glyphs = [.. line.Runs.SelectMany(r => r.Glyphs)];
        List<ShapedGlyph> tatweels = [.. glyphs.Where(g => g.Glyph == Tatweel)];
        Assert.IsNotEmpty(tatweels);
        Assert.AreEqual(3, tatweels.Sum(g => g.Advance) * 10 / 1000, 1e-9, "the whole slack goes to kashidas");
        Assert.HasCount(2, tatweels.Select(g => g.Cluster).Distinct(), "one kashida per word");
        Assert.IsTrue(tatweels.All(g => g.Text == ""), "kashidas carry no text");

        // Spaces keep their width; without kashidas they take the slack.
        double space = glyphs.Single(g => layout.Paragraph.Text[g.Cluster] == ' ').Advance;
        double plainSpace = plain.Lines[0].Runs.SelectMany(r => r.Glyphs).Single(g => plain.Paragraph.Text[g.Cluster] == ' ').Advance;
        Assert.AreEqual(Arabic.GetAdvance(Arabic.TryGetGlyph(' ', out int s) ? s : 0), space, 1e-9);
        Assert.AreEqual(space + 300, plainSpace, 1e-9);
        Assert.IsFalse(plain.Lines[0].Runs.SelectMany(r => r.Glyphs).Any(g => g.Glyph == Tatweel));
    }

    [TestMethod]
    public void PrefersTheJoinBeforeAFinalLetterAndSkipsLamAlef()
    {
        // beh-lam-meem: the join before the final meem; lam-alef is a ligature and never elongates.
        string word = Beh + Lam + Meem;
        string text = string.Join(" ", Enumerable.Repeat(word, 3)) + " " + Beh + Lam + "\u0627";
        PdfTextLayout layout = Lay(text, TwoWords(word, 2), kashida: true);
        foreach (LineRun run in layout.Lines[0].Runs)
        {
            for (int i = 0; i < run.Glyphs.Count; i++)
            {
                if (run.Glyphs[i].Glyph != Tatweel) continue;
                Assert.AreEqual(Lam, layout.Paragraph.Text.Substring(run.Glyphs[i].Cluster, 1));

                // In visual order the tatweels sit just left of the lam.
                int next = i;
                while (run.Glyphs[next].Glyph == Tatweel) next++;
                Assert.AreEqual(run.Glyphs[i].Cluster, run.Glyphs[next].Cluster);
            }
        }

        Assert.IsTrue(layout.Lines[0].Runs.SelectMany(r => r.Glyphs).Any(g => g.Glyph == Tatweel));
    }

    [TestMethod]
    public void LargeSlackIsCappedPerWordWhileSpacesCanTakeIt()
    {
        string word = Beh + Beh + Beh;
        string text = string.Join(" ", Enumerable.Repeat(word, 4));
        // 15 points of slack: 7.5 per word, capped at three tatweels (6 points); a third word needs 19.2.
        PdfTextLayout layout = Lay(text, TwoWords(word, 15), kashida: true);
        PdfTextLine line = layout.Lines[0];
        Assert.AreEqual(TwoWords(word, 15), line.Width, 1e-9);
        List<ShapedGlyph> glyphs = [.. line.Runs.SelectMany(r => r.Glyphs)];
        double kashida = glyphs.Where(g => g.Glyph == Tatweel).Sum(g => g.Advance) * 10 / 1000;
        Assert.AreEqual(2 * 3 * 2, kashida, 1e-9, "two words, three tatweels of 2 points each");
        double space = glyphs.Single(g => layout.Paragraph.Text[g.Cluster] == ' ').Advance * 10 / 1000;
        Assert.AreEqual(Arabic.MeasureString(" ", 10) + 15 - 12, space, 1e-9);
    }

    [TestMethod]
    public void WithoutSpacesKashidasTakeAllTheSlack()
    {
        // One word per line (the second needs 19.2 points more): the first line has no space to widen.
        string word = Beh + Beh + Beh + Beh;
        double natural = PdfTextLayout.Create(new PdfParagraph().Add(word, Style), 1000).Lines[0].Width;
        PdfTextLayout layout = Lay(word + " " + word, natural + 10, kashida: true);
        Assert.HasCount(2, layout.Lines);
        Assert.AreEqual(natural + 10, layout.Lines[0].Width, 1e-9);
        Assert.AreEqual(natural, layout.Lines[1].Width, 1e-9, "the last line is not justified");

        layout = Lay(word + " " + word, natural + 10, kashida: false);
        Assert.AreEqual(natural, layout.Lines[0].Width, 1e-9, "without kashidas there is nothing to widen");
    }

    [TestMethod]
    public void LatinAndFontsWithoutTatweelJustifyWithSpaces()
    {
        var courier = new PdfTextStyle(PdfStandardFont.Courier, 10);
        PdfTextLayout layout = PdfTextLayout.Create(new PdfParagraph { Alignment = PdfTextAlignment.Justify, Kashida = true }.Add("aa bb cc dd", courier), 60);
        Assert.AreEqual(60, layout.Lines[0].Width, 1e-9);
        Assert.HasCount(2, layout.Lines);
    }

    [TestMethod]
    public void KashidasDrawAndCopyAsTheOriginalText()
    {
        string word = Beh + Beh + Beh;
        string text = string.Join(" ", Enumerable.Repeat(word, 4));
        PdfTextLayout layout = Lay(text, TwoWords(word, 3), kashida: true);
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawText(layout, 72, 100);
        PdfDocument reopened = PdfDocument.Open(doc.ToArray());
        string extracted = reopened.Pages[0].ExtractText();
        StringAssert.Contains(extracted.Replace("\r", "").Replace("\n", " "), word + " " + word);
        Assert.DoesNotContain("\u0640", extracted, "tatweels are not part of the text");
    }
}
