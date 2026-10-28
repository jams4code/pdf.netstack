using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>
/// Drawing shaped text with the synthetic Arabic fixture. Glyph ids: space 1, f 3, i 4, f_i 5,
/// T 6, o 7, x 8, beh 11, beh.init 12, beh.medi 14, beh.fina 15, lam_alef 24, fatha 26.
/// </summary>
[TestClass]
public sealed class ShapingTests
{
    private static PdfOpenTypeFont Arabic() =>
        PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Arabic.ttf"));

    private static string Content(PdfDocument doc)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[0].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    private static PdfDocument Draw(PdfFont font, string text)
    {
        PdfDocument doc = PdfDocument.Create();
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawString(text, font, 10, PdfColor.Black, 50, 100);
        return doc;
    }

    [TestMethod]
    public void RightToLeftRunsAreReversedInALeftToRightLine()
    {
        // "To ببب": a left to right paragraph; the Arabic word is drawn right to left, so visually
        // beh.fina comes first, then beh.medi, then beh.init.
        List<int> glyphs = [.. Arabic().Shape("To ببب").Select(g => g.Glyph)];
        CollectionAssert.AreEqual(new[] { 6, 7, 1, 15, 14, 12 }, glyphs);
    }

    [TestMethod]
    public void LigaturesMeasureAndCopyAsTheirCharacters()
    {
        PdfOpenTypeFont font = Arabic();
        Assert.AreEqual(520 + 500, font.MeasureString("fio", 1000), 1e-9);

        PdfDocument doc = Draw(font, "لا");
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        var type0 = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(resources["Font"])!)["F1"])!;
        doc.ToArray();
        string toUnicode = Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(type0["ToUnicode"])!));
        StringAssert.Contains(toUnicode, "<0018> <06440627>");
    }

    [TestMethod]
    public void ArabicCarriesActualTextAndRaisesMarks()
    {
        string content = Content(Draw(Arabic(), "بَب"));

        // FEFF, then beh fatha beh in UTF-16BE: the logical text, for copying and search.
        StringAssert.Contains(content, "/Span <</ActualText<FEFF0628064E0628>>> BDC\n");
        StringAssert.Contains(content, " Ts\n");
        StringAssert.Contains(content, "0 Ts\n");
        StringAssert.EndsWith(content.TrimEnd(), "EMC\nQ");
    }

    [TestMethod]
    public void PlainLatinTextHasNoActualText()
    {
        Assert.DoesNotContain("ActualText", Content(Draw(Arabic(), "To fo")));
        Assert.DoesNotContain("ActualText", Content(Draw(PdfStandardFont.Helvetica, "Plain text")));
    }

    [TestMethod]
    public void GlyphGrowthIsCapped()
    {
        // The fixture's ccmp doubles x twenty times. HarfBuzz runs out of memory on it; the shaper
        // stops at 64 glyphs for a one character input.
        List<ShapedGlyph> glyphs = Arabic().Shape("x");
        Assert.HasCount(64, glyphs);
        Assert.IsTrue(glyphs.All(g => g.Glyph == 8));
    }

    [TestMethod]
    public void LongCursiveChainsResolveWithoutRecursion()
    {
        // One joined word of 20,000 letters: a cursive chain as long as the text.
        string word = new('ب', 20_000);
        List<ShapedGlyph> glyphs = Arabic().Shape(word);
        Assert.HasCount(20_000, glyphs);

        // Visual order: the final form sits on the baseline, each earlier letter a step lower.
        Assert.AreEqual(0, glyphs[0].YOffset);
        Assert.IsLessThan(glyphs[1].YOffset, glyphs[2].YOffset);
    }

    [TestMethod]
    public void JoinersShapeButAreNotDrawn()
    {
        // ZWNJ stops beh from joining; ZWJ keeps the second beh in its initial form.
        List<int> glyphs = [.. Arabic().Shape("ب‌ب‍").Select(g => g.Glyph)];
        CollectionAssert.AreEqual(new[] { 12, 11 }, glyphs);
    }
}
