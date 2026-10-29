using System.Diagnostics;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Text;

namespace PdfNetStack.Tests;

/// <summary>
/// Unicode normalization in the shaper and the Indic shaper, with the synthetic Test-Indic.ttf of
/// tools/make_font_fixtures.py: Devanagari and Bengali, and Latin a with combining grave, acute,
/// dot above and dot below, precomposed a acute and a dot below, but no a grave. Glyph ids: space
/// 1, dotted circle 2, ka 5, ra 11, i 18, ka.half 24, ra.reph 28, ka_ssa 30, i.alt 32, a 41,
/// aacute 46, adotbelow 47. The glyph by glyph comparison with HarfBuzz is in <see cref="HarfBuzzComparisonTests"/>.
/// </summary>
[TestClass]
public sealed class NormalizationAndIndicTests
{
    private static readonly PdfOpenTypeFont Font = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Indic.ttf"));

    private static List<GlyphInfo> Shape(string text, string script = "latn") => new Shaper(Font.Tables).Shape(text, 0, text.Length, false, script);

    private static int[] Glyphs(string text, string script = "latn") => [.. Shape(text, script).Select(g => g.Glyph)];

    [TestMethod]
    public void FontsCoverTextThroughNormalization()
    {
        Assert.IsTrue(Font.CanRender("\u00e0"), "a and combining grave draw \u00e0");
        Assert.IsTrue(Font.CanRender("a\u0301"), "a and combining acute compose to \u00e1");
        Assert.IsTrue(Font.CanRender("\u1ea1\u0307 a\u0307\u0323"));
        Assert.IsTrue(Font.CanRender("a\u2003a\u2009a\u202fa"), "spaces without glyphs fall back to the space");
        Assert.IsFalse(Font.HasGlyph(0xE0));
        Assert.IsFalse(Font.CanRender("\u00e4"), "no diaeresis");
        Assert.IsFalse(Font.CanRender("\u01d6"), "no u");
        Assert.IsTrue(Font.CanRender("\u0958"), "qa decomposes to ka and nukta");
    }

    [TestMethod]
    public void CanonicallyEquivalentTextShapesAlike()
    {
        CollectionAssert.AreEqual(new[] { 46 }, Glyphs("\u00e1"));
        CollectionAssert.AreEqual(new[] { 46 }, Glyphs("a\u0301"), "composed when the font has the composite");
        CollectionAssert.AreEqual(Glyphs("\u1ea1\u0307"), Glyphs("a\u0323\u0307"));
        CollectionAssert.AreEqual(Glyphs("\u1ea1\u0307"), Glyphs("a\u0307\u0323"), "marks sort by combining class");
        Assert.HasCount(2, Glyphs("\u00e0"), "decomposed when the font lacks the composite");

        // A composed glyph keeps the text it came from, for copying.
        GlyphInfo composed = Shape("a\u0301").Single();
        Assert.AreEqual("a\u0301", composed.Text);
        Assert.AreEqual(0, composed.Cluster);
    }

    [TestMethod]
    public void SpacesWithoutGlyphsTakeTheirOwnWidth()
    {
        // 1000 units per em; the space is 250.
        Assert.AreEqual(1000, Shape("\u2003").Single().XAdvance);
        Assert.AreEqual(200, Shape("\u2009").Single().XAdvance);
        Assert.AreEqual(125, Shape("\u202f").Single().XAdvance);
        Assert.AreEqual(1, Shape("\u2003").Single().Glyph);
    }

    [TestMethod]
    public void IndicSyllablesReorderAndFormConjuncts()
    {
        CollectionAssert.AreEqual(new[] { 32, 5 }, Glyphs("\u0915\u093f", "deva"), "the i matra goes before ka and takes its alternate");
        CollectionAssert.AreEqual(new[] { 30 }, Glyphs("\u0915\u094d\u0937", "deva"), "ka virama ssa is the akhand ligature");
        CollectionAssert.AreEqual(new[] { 24, 12 }, Glyphs("\u0915\u094d\u200d\u0937", "deva").Where(g => g != 1).ToArray(), "ZWJ asks for the half form");
        CollectionAssert.AreEqual(new[] { 5, 28 }, Glyphs("\u0930\u094d\u0915", "deva"), "reph moves after the base");

        // One cluster for the reordered syllable, so it is selected and copied whole.
        Assert.IsTrue(Shape("\u0915\u093f", "deva").All(g => g.Cluster == 0));
    }

    [TestMethod]
    public void BrokenClustersStandOnADottedCircle()
    {
        CollectionAssert.AreEqual(new[] { 18, 2 }, Glyphs("\u093f", "deva"));
        Assert.AreEqual(2, Glyphs("\u094d", "deva")[0]);
    }

    [TestMethod]
    public void IndicTextDrawsAndCopiesInLogicalOrder()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            g.DrawString("\u0915\u093f \u0930\u094d\u0915 \u0915\u094d\u0937", Font, 12, PdfColor.Black, 72, 100);
        }

        string text = PdfDocument.Open(doc.ToArray()).Pages[0].ExtractText();
        StringAssert.Contains(text, "\u0915\u093f \u0930\u094d\u0915 \u0915\u094d\u0937");
    }

    [TestMethod]
    public void HostileRunsShapeInBoundedTime()
    {
        string[] inputs =
        [
            string.Concat(Enumerable.Repeat("\u0915\u094d", 10_000)) + "\u0915", // one syllable of 20,000 characters
            new string('\u093f', 20_000), // broken clusters, each on a dotted circle
            string.Concat(Enumerable.Repeat("\u0915\u094d\u200c", 7_000)), // joiners after every halant
            "\u0930\u094d" + new string('\u0902', 20_000), // reph and endless signs
            "a" + string.Concat(Enumerable.Repeat("\u0301\u0323\u0307", 7_000)), // marks to sort and compose
            string.Concat(Enumerable.Repeat("\u0995\u09cb\u09cd\u09af", 5_000)), // split vowels, post base forms
        ];
        foreach (string input in inputs)
        {
            var clock = Stopwatch.StartNew();
            List<GlyphInfo> glyphs = new Shaper(Font.Tables).Shape(input, 0, input.Length, false, input[0] is >= '\u0980' and <= '\u09ff' ? "beng" : input[0] == 'a' ? "latn" : "deva");
            Assert.IsNotEmpty(glyphs);
            Assert.IsLessThan(10_000, clock.ElapsedMilliseconds, $"input starting {(int)input[0]:X4} took {clock.ElapsedMilliseconds} ms");
        }
    }

    [TestMethod]
    public void SyllablesFollowTheGrammar()
    {
        // A consonant syllable with two matras, a space outside any syllable, a broken cluster (a
        // lone matra) and a vowel syllable.
        var buffer = "\u0915\u094d\u0937\u093f\u093f \u093f\u0905\u0902".Select(c => new GlyphInfo { IndicCategory = (byte)(ShapingData.Indic(c) & 0xFF) }).ToList();
        IndicSyllables.Find(buffer);
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1, 2, 3, 4, 4 }, buffer.Select(g => g.Syllable >> 4).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0, 5, 4, 1, 1 }, buffer.Select(g => g.Syllable & 0x0F).ToArray());
    }

    [TestMethod]
    public void ShapingDataMatchesTheUnicodeCharacterDatabase()
    {
        Assert.IsTrue(ShapingData.Decompose(0x00E1, out int a, out int b));
        Assert.AreEqual((0x61, 0x301), (a, b));
        Assert.IsTrue(ShapingData.Decompose(0xAC01, out a, out b), "Hangul LVT");
        Assert.AreEqual((0xAC00, 0x11A8), (a, b));
        Assert.IsTrue(ShapingData.Compose(0x61, 0x301, out int ab));
        Assert.AreEqual(0xE1, ab);
        Assert.IsFalse(ShapingData.Compose(0x915, 0x93C, out _), "qa is excluded from composition");
        Assert.AreEqual(230, ShapingData.CombiningClass(0x301));
        Assert.AreEqual(220, ShapingData.CombiningClass(0x323));
        Assert.AreEqual(0, ShapingData.CombiningClass(0x61));
    }
}
