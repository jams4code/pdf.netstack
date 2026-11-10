using System.Diagnostics;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Text;

namespace PdfNetStack.Tests;

/// <summary>
/// The Thai and Lao, Khmer, Myanmar, Hangul and Universal Shaping Engine shapers, with the
/// synthetic fonts of tools/make_font_fixtures.py. Each test names the glyphs it expects; the
/// glyph by glyph comparison with HarfBuzz, offsets included, is in <see cref="HarfBuzzComparisonTests"/>.
/// </summary>
[TestClass]
public sealed class ComplexScriptShapingTests
{
    private static readonly Dictionary<string, PdfOpenTypeFont> Fonts = [];

    private static PdfOpenTypeFont Font(string name)
    {
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(name, out PdfOpenTypeFont? font))
            {
                Fonts[name] = font = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name + ".ttf"));
            }

            return font;
        }
    }

    private static List<GlyphInfo> Shape(string font, string text, string script, bool rtl = false) =>
        new Shaper(Font(font).Tables).Shape(text, 0, text.Length, rtl, script);

    private static int[] Glyphs(string font, string text, string script) => [.. Shape(font, text, script).Select(g => g.Glyph)];

    // Test-Thai.ttf: ko 3, po pla 4, yo ying 5, do chada 6, sara aa 8, sara u 12, mai ek 14,
    // nikhahit 16, mai ek shifted down and left 18, mai ek shifted left 19, mai tho shifted left 20,
    // sara i shifted left 22, sara u shifted down 24, yo ying without descender 25; Lao ko 26,
    // nikhahit 31, sara aa 27, ko and aa 32.
    [TestMethod]
    public void SaraAmSplitsAndItsNikhahitGoesBeforeToneMarks()
    {
        CollectionAssert.AreEqual(new[] { 3, 16, 8 }, Glyphs("Test-Thai", "กำ", "thai"));
        CollectionAssert.AreEqual(new[] { 3, 16, 14, 8 }, Glyphs("Test-Thai", "ก่ำ", "thai"), "the nikhahit moves back over mai ek");
        Assert.IsTrue(Shape("Test-Thai", "ก่ำ", "thai").All(g => g.Cluster == 0), "one cluster");
        CollectionAssert.AreEqual(new[] { 26, 31, 27 }, Glyphs("Test-Thai", "ກຳ", "lao "), "Lao too");
    }

    [TestMethod]
    public void ThaiMarksFallBackToPrivateUseGlyphs()
    {
        // The font has no Thai substitutions, so marks clear tall consonants and descenders with
        // the private use glyphs: the Windows layout, else the Macintosh one.
        CollectionAssert.AreEqual(new[] { 4, 18 }, Glyphs("Test-Thai", "ป่", "thai"), "mai ek down and left of po pla's ascender");
        CollectionAssert.AreEqual(new[] { 4, 22, 19 }, Glyphs("Test-Thai", "ปิ่", "thai"), "sara i left, mai ek left above it");
        CollectionAssert.AreEqual(new[] { 4, 22, 20 }, Glyphs("Test-Thai", "ปิ้", "thai"), "mai tho from the Macintosh layout");
        CollectionAssert.AreEqual(new[] { 25, 12 }, Glyphs("Test-Thai", "ญุ", "thai"), "yo ying drops its descender");
        CollectionAssert.AreEqual(new[] { 6, 24 }, Glyphs("Test-Thai", "ฎุ", "thai"), "sara u goes under do chada's descender");

        // Lao has substitutions in the font, and no private use fallback anyway.
        CollectionAssert.AreEqual(new[] { 32 }, Glyphs("Test-Thai", "ກາ", "lao "));
    }

    // Test-Khmer.ttf: dotted circle 2, ka 5, sa 9, aa 11, oe 14, e 15, o 16, ka below 21, ka below
    // after coeng ro 23, ro pre-base 24, ta and aa 25.
    [TestMethod]
    public void KhmerCoengRoMovesBeforeTheBase()
    {
        CollectionAssert.AreEqual(new[] { 24, 5 }, Glyphs("Test-Khmer", "ក្រ", "khmr"));
        CollectionAssert.AreEqual(new[] { 24, 5, 23 }, Glyphs("Test-Khmer", "ក្រ្ក", "khmr"), "the subscript after it takes cfar");
        Assert.IsTrue(Shape("Test-Khmer", "ក្រ", "khmr").All(g => g.Cluster == 0));
    }

    [TestMethod]
    public void KhmerSplitVowelsPutTheirFirstPartBeforeTheBase()
    {
        CollectionAssert.AreEqual(new[] { 15, 5, 14 }, Glyphs("Test-Khmer", "កើ", "khmr"));
        CollectionAssert.AreEqual(new[] { 15, 5, 16 }, Glyphs("Test-Khmer", "កោ", "khmr"));
        CollectionAssert.AreEqual(new[] { 15, 2, 16 }, Glyphs("Test-Khmer", "ោ", "khmr"), "on a dotted circle when alone");
    }

    [TestMethod]
    public void KhmerAppliesCligButNotLiga()
    {
        CollectionAssert.AreEqual(new[] { 25 }, Glyphs("Test-Khmer", "តា", "khmr"));
        CollectionAssert.AreEqual(new[] { 9, 11 }, Glyphs("Test-Khmer", "សា", "khmr"));
    }

    // Test-Myanmar.ttf: dotted circle 2, ka 5, e 15, e with variation selector 1 26, kinzi 27,
    // medial ra pre-base 30, e and ka 32.
    [TestMethod]
    public void MyanmarReordersKinziMedialRaAndVowelE()
    {
        CollectionAssert.AreEqual(new[] { 30, 5 }, Glyphs("Test-Myanmar", "ကြ", "mymr"), "medial ra before the base");
        CollectionAssert.AreEqual(new[] { 32 }, Glyphs("Test-Myanmar", "ကေ", "mymr"), "e before the base, then the font's ligature");
        CollectionAssert.AreEqual(new[] { 5, 27 }, Glyphs("Test-Myanmar", "င်္က", "mymr"), "kinzi after the base");
        CollectionAssert.AreEqual(new[] { 15, 2 }, Glyphs("Test-Myanmar", "ေ", "mymr"), "e alone on a dotted circle");
    }

    [TestMethod]
    public void VariationSequencesTakeTheFontsGlyph()
    {
        // The font maps e with variation selector 1 to its own glyph (cmap format 14).
        Assert.IsTrue(Font("Test-Myanmar").Tables.Font.TryGetVariationGlyph(0x1031, 0xFE00, out int glyph));
        Assert.AreEqual(26, glyph);
        Assert.IsFalse(Font("Test-Myanmar").Tables.Font.TryGetVariationGlyph(0x1031, 0xFE01, out _));
        List<GlyphInfo> shaped = Shape("Test-Myanmar", "ကေ︀", "mymr");
        CollectionAssert.AreEqual(new[] { 26, 5 }, shaped.Select(g => g.Glyph).ToArray());
        Assert.AreEqual("ေ︀", shaped[0].Text, "the variant carries the selector's text");
    }

    [TestMethod]
    public void DamagedVariationSequencesMapNothing()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Myanmar.ttf"));
        int format14 = Format14Offset(data);
        Assert.IsGreaterThan(0, format14);
        foreach ((int field, byte value) in new[] { (6, (byte)0xFF), (13, (byte)0xFF), (17, (byte)0x7F) })
        {
            byte[] damaged = (byte[])data.Clone();
            for (int k = 0; k < 4; k++) damaged[format14 + field + k] = value;
            PdfOpenTypeFont font = PdfOpenTypeFont.Load(damaged);
            Assert.IsFalse(font.Tables.Font.TryGetVariationGlyph(0x1031, 0xFE00, out _));
            List<GlyphInfo> shaped = new Shaper(font.Tables).Shape("ကေ︀", 0, 3, false, "mymr");
            CollectionAssert.DoesNotContain(shaped.Select(g => g.Glyph).ToArray(), 26, "e keeps its own glyph");
        }
    }

    // Test-Hangul.ttf: dotted circle 2, ga 3, gag 4, single dot tone 12, double dot tone 13 (no
    // width), kiyeok, hieuh initial forms 14, 15, a medial form 17, nieun final form 20.
    [TestMethod]
    public void HangulJamoComposeWhenTheFontHasTheSyllable()
    {
        CollectionAssert.AreEqual(new[] { 3 }, Glyphs("Test-Hangul", "가", "hang"));
        CollectionAssert.AreEqual(new[] { 4 }, Glyphs("Test-Hangul", "각", "hang"));
        CollectionAssert.AreEqual(new[] { 4 }, Glyphs("Test-Hangul", "각", "hang"), "a syllable and a trailing jamo");
        Assert.AreEqual("각", Shape("Test-Hangul", "각", "hang").Single().Text);
    }

    [TestMethod]
    public void HangulSyllablesTheFontLacksDecomposeIntoShapedJamo()
    {
        CollectionAssert.AreEqual(new[] { 15, 17, 20 }, Glyphs("Test-Hangul", "한", "hang"));
        CollectionAssert.AreEqual(new[] { 14, 17, 20 }, Glyphs("Test-Hangul", "간", "hang"), "ga and a final nieun the font cannot compose");
        Assert.IsTrue(Shape("Test-Hangul", "간", "hang").All(g => g.Cluster == 0));
    }

    [TestMethod]
    public void HangulToneMarksGoBeforeTheSyllableUnlessTheyOverstrike()
    {
        CollectionAssert.AreEqual(new[] { 12, 3 }, Glyphs("Test-Hangul", "가〮", "hang"));
        CollectionAssert.AreEqual(new[] { 3, 13 }, Glyphs("Test-Hangul", "가〯", "hang"), "zero width: it stays and overstrikes");
        CollectionAssert.AreEqual(new[] { 12, 2 }, Glyphs("Test-Hangul", "〮", "hang"), "alone, on a dotted circle");
        CollectionAssert.AreEqual(new[] { 2, 13 }, Glyphs("Test-Hangul", "〯", "hang"));
    }

    // Test-Use.ttf: dotted circle 2, ta 7, bisah 13, ulu 16, taling 18, ra repha 20, ra pre-base 21,
    // ka isolated, initial, medial, final 25 to 28; Mongolian ma final 39, na isolated 43, na
    // initial with variation selector 1 44; Sinhala a 47, ae 48, ka 49, yansaya 53.
    [TestMethod]
    public void UseRephaAndPreBaseFormsMove()
    {
        CollectionAssert.AreEqual(new[] { 25, 20 }, Glyphs("Test-Use", "ᬭ᭄ᬓ", "bali"), "repha after the base");
        CollectionAssert.AreEqual(new[] { 21, 25 }, Glyphs("Test-Use", "ᬓ᭄ᬭ", "bali"), "pre-base form before it");
        CollectionAssert.AreEqual(new[] { 18, 25 }, Glyphs("Test-Use", "ᬓᬾ", "bali"), "pre-base vowel before it");
        CollectionAssert.AreEqual(new[] { 18, 2 }, Glyphs("Test-Use", "ᬾ", "bali"), "a vowel alone on a dotted circle");
    }

    [TestMethod]
    public void UseClustersTakeJoiningForms()
    {
        // Clusters join as long as no non-cluster (here a word joiner) parts them; a space is a cluster too.
        CollectionAssert.AreEqual(new[] { 26, 28, 1, 25 }, Glyphs("Test-Use", "ᬓᬓ⁠ᬓ", "bali"));
        CollectionAssert.AreEqual(new[] { 26, 1, 27, 27, 28 }, Glyphs("Test-Use", "ᬓ ᬓᬓᬓ", "bali"));

        // Mongolian joins like Arabic; a free variation selector takes its letter's form.
        CollectionAssert.AreEqual(new[] { 44, 39 }, Glyphs("Test-Use", "ᠨ᠋ᠮ", "mong"));
        CollectionAssert.AreEqual(new[] { 43 }, Glyphs("Test-Use", "ᠨ", "mong"));
    }

    [TestMethod]
    public void UseKeepsJoinersWhereFontsSpellThemOut()
    {
        CollectionAssert.AreEqual(new[] { 49, 53 }, Glyphs("Test-Use", "ක්‍ය", "sinh"), "virama, ZWJ and ya form the yansaya");
    }

    [TestMethod]
    public void InvalidVowelSequencesGetADottedCircle()
    {
        // A letter a with the sign ae looks like the letter ae.
        CollectionAssert.AreEqual(new[] { 47, 2, 48 }, Glyphs("Test-Use", "අැ", "sinh"));
        Assert.AreEqual(2, ShapingData.InvalidVowelSequence([0x0905, 0x093E], 0), "Devanagari a and aa");
        Assert.AreEqual(0, ShapingData.InvalidVowelSequence([0x0915, 0x093E], 0));
    }

    [TestMethod]
    public void NestedLookupsThatAddGlyphsShiftTheSequence()
    {
        // ccmp turns ta into ta and cecek; its next record then applies to the new cecek, not to bisah.
        CollectionAssert.AreEqual(new[] { 7, 16, 13 }, Glyphs("Test-Use", "ᬢᬄ", "bali"));
    }

    [TestMethod]
    public void ScriptsPickTheirShapers()
    {
        // A script the font has only under DFLT gets the default shaper, as in HarfBuzz: shaped as
        // Javanese, which the Khmer font lists only under DFLT, a lone split vowel stays whole and
        // takes no dotted circle.
        CollectionAssert.AreEqual(new[] { 15, 2, 16 }, Glyphs("Test-Khmer", "ោ", "khmr"));
        CollectionAssert.AreEqual(new[] { 16 }, Glyphs("Test-Khmer", "ោ", "java"));
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void HostileComplexRunsShapeInBoundedTime()
    {
        (string Font, string Script, string Text)[] inputs =
        [
            ("Test-Thai", "thai", string.Concat(Enumerable.Repeat("ปิ่ำุ", 5_000))),
            ("Test-Khmer", "khmr", string.Concat(Enumerable.Repeat("ក្រ្", 5_000))),
            ("Test-Khmer", "khmr", new string('ោ', 10_000)),
            ("Test-Myanmar", "mymr", "င်္က" + string.Concat(Enumerable.Repeat("္က︀", 6_000))),
            ("Test-Myanmar", "mymr", new string('ေ', 10_000)),
            ("Test-Hangul", "hang", string.Concat(Enumerable.Repeat("간〮", 5_000))),
            ("Test-Use", "bali", "ᬭ᭄" + string.Concat(Enumerable.Repeat("ᬓ᭄", 8_000))),
            ("Test-Use", "bali", new string('ᬾ', 10_000)),
            ("Test-Use", "bali", string.Concat(Enumerable.Repeat("ᬓ‌͏", 6_000))),
            ("Test-Use", "mong", string.Concat(Enumerable.Repeat("ᠨ᠋", 8_000))),
        ];
        foreach ((string font, string script, string text) in inputs)
        {
            var clock = Stopwatch.StartNew();
            List<GlyphInfo> glyphs = Shape(font, text, script);
            Assert.IsNotEmpty(glyphs);
            Assert.IsLessThan(10_000, clock.ElapsedMilliseconds, $"{font} {script} took {clock.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>The offset in the file of the cmap's variation sequences subtable (platform 0, encoding 5).</summary>
    private static int Format14Offset(byte[] d)
    {
        int U16(int at) => d[at] << 8 | d[at + 1];
        int U32(int at) => d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3];
        for (int i = 0; i < U16(4); i++)
        {
            int record = 12 + 16 * i;
            if (System.Text.Encoding.ASCII.GetString(d, record, 4) != "cmap") continue;
            int cmap = U32(record + 8);
            for (int k = 0; k < U16(cmap + 2); k++)
            {
                int r = cmap + 4 + 8 * k;
                if (U16(r) == 0 && U16(r + 2) == 5) return cmap + U32(r + 4);
            }
        }

        return -1;
    }
}
