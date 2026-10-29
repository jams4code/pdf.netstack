using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Paragraph layout. Courier is 600 thousandths wide for every glyph: 6 points at size 10.</summary>
[TestClass]
public sealed class TextLayoutTests
{
    private const char SoftHyphen = (char)0xAD;
    private const char Beh = (char)0x628;

    private static readonly PdfTextStyle Mono = new(PdfStandardFont.Courier, 10);

    private static PdfTextLayout Lay(string text, double width, PdfTextAlignment alignment = PdfTextAlignment.Start, PdfTextStyle? style = null) =>
        PdfTextLayout.Create(new PdfParagraph { Alignment = alignment }.Add(text, style ?? Mono), width);

    private static string[] LineTexts(PdfTextLayout layout) =>
        [.. layout.Lines.Select(l => layout.Paragraph.Text.Substring(l.Start, l.End - l.Start))];

    [TestMethod]
    public void WrapsAtSpacesAndIgnoresTrailingSpaces()
    {
        CollectionAssert.AreEqual(new[] { "aaa ", "bbb ", "ccc" }, LineTexts(Lay("aaa bbb ccc", 40)));
        PdfTextLayout layout = Lay("aaa bbb ccc", 45);
        CollectionAssert.AreEqual(new[] { "aaa bbb ", "ccc" }, LineTexts(layout));
        Assert.AreEqual(42, layout.Lines[0].Width, 1e-9);
        Assert.AreEqual(42, layout.Width, 1e-9);
    }

    [TestMethod]
    public void LineFeedsBreakLines()
    {
        CollectionAssert.AreEqual(new[] { "a\n", "b" }, LineTexts(Lay("a\nb", 100)));
        CollectionAssert.AreEqual(new[] { "a\r\n", "b" }, LineTexts(Lay("a\r\nb", 100)));
        PdfTextLayout trailing = Lay("a\n", 100);
        Assert.HasCount(2, trailing.Lines);
        Assert.AreEqual(0, trailing.Lines[1].Width);
        Assert.AreEqual(trailing.Lines[0].Height, trailing.Lines[1].Height, 1e-9);
    }

    [TestMethod]
    public void Aligns()
    {
        Assert.AreEqual(0, Lay("abc", 100).Lines[0].Left, 1e-9);
        Assert.AreEqual(82, Lay("abc", 100, PdfTextAlignment.Right).Lines[0].Left, 1e-9);
        Assert.AreEqual(82, Lay("abc", 100, PdfTextAlignment.End).Lines[0].Left, 1e-9);
        Assert.AreEqual(41, Lay("abc", 100, PdfTextAlignment.Center).Lines[0].Left, 1e-9);
    }

    [TestMethod]
    public void JustifiesAllButTheLastLine()
    {
        PdfTextLayout layout = Lay("aa bb cc dd", 60, PdfTextAlignment.Justify);
        CollectionAssert.AreEqual(new[] { "aa bb cc ", "dd" }, LineTexts(layout));
        Assert.AreEqual(60, layout.Lines[0].Width, 1e-9);
        Assert.AreEqual(12, layout.Lines[1].Width, 1e-9);
        Assert.AreEqual(0, layout.Lines[1].Left, 1e-9);
    }

    [TestMethod]
    public void BreaksWordsTooWideForTheLine()
    {
        CollectionAssert.AreEqual(new[] { "abcd", "efgh", "ij" }, LineTexts(Lay("abcdefghij", 25)));

        // Even one character wider than the line goes on a line of its own.
        CollectionAssert.AreEqual(new[] { "a", "b" }, LineTexts(Lay("ab", 2)));
    }

    [TestMethod]
    public void LineHeightFollowsCss()
    {
        var helvetica = new PdfTextStyle(PdfStandardFont.Helvetica, 10);
        PdfTextLayout normal = Lay("one\ntwo", 100, style: helvetica);
        Assert.AreEqual(11.5, normal.Lines[0].Height, 1e-9);
        Assert.AreEqual(9.05 + 0.33 / 2, normal.Lines[0].Baseline, 1e-9); // half the line gap goes above, as in CSS
        Assert.AreEqual(23, normal.Height, 1e-9);

        // line-height 2: 20 points per line, the 8.83 points of leading split above and below.
        PdfTextLayout spaced = PdfTextLayout.Create(new PdfParagraph { LineHeight = 2 }.Add("one\ntwo", helvetica), 100);
        Assert.AreEqual(20, spaced.Lines[0].Height, 1e-9);
        Assert.AreEqual(9.05 + (20 - 11.17) / 2, spaced.Lines[0].Baseline, 1e-9);
        Assert.AreEqual(20, spaced.Lines[1].Top, 1e-9);
    }

    [TestMethod]
    public void MixedSizesShareABaseline()
    {
        var small = new PdfTextStyle(PdfStandardFont.Helvetica, 10);
        var large = small with { Size = 20 };
        PdfTextLayout layout = PdfTextLayout.Create(new PdfParagraph().Add("small ", small).Add("LARGE", large), 300);
        Assert.HasCount(1, layout.Lines);
        Assert.AreEqual(18.1 + 0.66 / 2, layout.Lines[0].Baseline, 1e-9);
        Assert.AreEqual(23, layout.Lines[0].Height, 1e-9);
    }

    [TestMethod]
    public void IndentsTheFirstLine()
    {
        PdfTextLayout layout = PdfTextLayout.Create(new PdfParagraph { FirstLineIndent = 12 }.Add("aa bb cc", Mono), 40);
        CollectionAssert.AreEqual(new[] { "aa ", "bb cc" }, LineTexts(layout));
        Assert.AreEqual(12, layout.Lines[0].Left, 1e-9);
        Assert.AreEqual(0, layout.Lines[1].Left, 1e-9);
    }

    [TestMethod]
    public void SoftHyphensShowAHyphenWhenTheyBreak()
    {
        string word = "hyphen" + SoftHyphen + "ation";
        PdfTextLayout layout = Lay(word, 50);
        CollectionAssert.AreEqual(new[] { "hyphen" + SoftHyphen, "ation" }, LineTexts(layout));
        Assert.AreEqual(42, layout.Lines[0].Width, 1e-9);

        // Unbroken, the soft hyphen stays invisible.
        Assert.AreEqual(66, Lay(word, 100).Lines[0].Width, 1e-9);
    }

    [TestMethod]
    public void TabsAndSpacing()
    {
        Assert.AreEqual(36, Lay("a\tb", 100).Lines[0].Width, 1e-9);
        Assert.AreEqual(21, Lay("abc", 100, style: Mono with { LetterSpacing = 1 }).Lines[0].Width, 1e-9);
        Assert.AreEqual(34, Lay("a b c", 100, style: Mono with { WordSpacing = 2 }).Lines[0].Width, 1e-9);
    }

    [TestMethod]
    public void FallbackFontsCoverMissingCharacters()
    {
        var style = new PdfTextStyle(PdfStandardFont.Helvetica, 12) { Fallbacks = [PdfStandardFont.Symbol] };
        PdfTextLayout layout = Lay("x = " + (char)0x3B1 + " + " + (char)0x3B2, 300, style: style);
        Assert.AreEqual(2, layout.Lines[0].Runs.Count(r => r.Font == PdfStandardFont.Symbol));
        Assert.IsTrue(layout.Lines[0].Runs.Any(r => r.Font == PdfStandardFont.Helvetica));

        var e = Assert.ThrowsExactly<ArgumentException>(() => Lay("x " + (char)0x4E2D, 300, style: style));
        StringAssert.Contains(e.Message, "U+4E2D");
    }

    [TestMethod]
    public void RightToLeftParagraphsAlignRight()
    {
        PdfOpenTypeFont arabic = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Arabic.ttf"));
        var style = new PdfTextStyle(arabic, 10);
        PdfTextLayout layout = PdfTextLayout.Create(new PdfParagraph().Add(new string(Beh, 2) + " " + Beh, style), 200);
        PdfTextLine line = layout.Lines[0];
        Assert.AreEqual(200, line.Left + line.Width, 1e-9);

        // Visual order: the isolated beh, last in logical order, comes first.
        Assert.AreEqual(11, line.Runs[0].Glyphs[0].Glyph);
    }

    [TestMethod]
    public void DrawsLinesAndDecorations()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            PdfTextLayout layout = g.DrawText("aaa bbb", Mono with { Underline = true }, 50, 100, 30);
            Assert.HasCount(2, layout.Lines);
            g.DrawText(layout, 300, 100, 1, 1);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => g.DrawText(layout, 0, 0, 1, 2));
        }

        PdfObject? contents = doc.Resolve(doc.Pages[0].Dictionary["Contents"]);
        string content = Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(contents)!));
        StringAssert.Contains(content, "(aaa)Tj");
        StringAssert.Contains(content, "(bbb)Tj");
        StringAssert.Contains(content, " re\nf\n");

        // The second line drawn alone has its top at the given y.
        StringAssert.Contains(content, "1 0 0 -1 300 ");
    }

    [TestMethod]
    public void ValidatesArguments()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Lay("a", 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfParagraph { LineHeight = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfTextStyle(PdfStandardFont.Courier, -1));
        Assert.IsEmpty(PdfTextLayout.Create(new PdfParagraph(), 100).Lines);
    }
}
