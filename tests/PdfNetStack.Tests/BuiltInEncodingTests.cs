using PdfNetStack.Fonts.OpenType;

namespace PdfNetStack.Tests;

/// <summary>
/// Simple fonts without /Encoding read their codes through the built-in encoding of the embedded
/// program: the encoding and charset of a CFF font (FontFile3), synthetic fixtures made by
/// tools/make_cff_fixtures.py.
/// </summary>
[TestClass]
public sealed class BuiltInEncodingTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name));

    /// <summary>A page drawing <paramref name="codes"/> (a hex string) in a Type 1 font whose FontFile3 holds <paramref name="program"/>.</summary>
    private static PdfPage Page(string codes, byte[] program, string subtype = "Type1C", string fontExtra = "")
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 10 0 R>>>>/Contents 4 0 R>>");
        b.SetStream(4, "", Compat.Latin1.GetBytes($"BT /F1 10 Tf 72 700 Td <{codes}> Tj ET"));
        b.Set(10, $"<</Type/Font/Subtype/Type1/BaseFont/ABCDEF+Test/FirstChar 0/LastChar 255/Widths[{string.Join(" ", Enumerable.Repeat("500", 256))}]/FontDescriptor 11 0 R{fontExtra}>>");
        b.Set(11, "<</Type/FontDescriptor/FontName/ABCDEF+Test/Flags 4/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile3 12 0 R>>");
        b.SetStream(12, $"/Subtype/{subtype}", program);
        return PdfDocument.Open(b.Build()).Pages[0];
    }

    private static string[] Letters(PdfPage page) => [.. page.GetLetters().Select(l => l.Text)];

    [TestMethod]
    public void CodeRangesAndSupplementsNameGlyphsThroughTheCharset()
    {
        PdfPage page = Page("41 42 43 80 81 30 61 90 44", Fixture("Cff-Ranges.cff"));
        CollectionAssert.AreEqual(new[] { "A", "B", "C", "中", "€", "ﬁ", "A", "€", "�" }, Letters(page));
    }

    [TestMethod]
    public void CodeListsAndCharsetListsNameGlyphs()
    {
        // StandardEncoding would read 41 as A; the font's own encoding leaves it unmapped.
        CollectionAssert.AreEqual(new[] { "H", "e", "γ", "�" }, Letters(Page("01 02 F0 41", Fixture("Cff-Codes.cff"))));
    }

    [TestMethod]
    public void PredefinedExpertEncodingAndCharsets()
    {
        // ff and fi are ligatures; oneoldstyle is in the Adobe Glyph List's private use range.
        CollectionAssert.AreEqual(new[] { "ﬀ", "ﬁ", ",", "" }, Letters(Page("56 57 2C 31", Fixture("Cff-Expert.cff"))));

        // ExpertSubset glyphs 8 and 9 (comma, hyphen) at codes 27 and 28.
        CollectionAssert.AreEqual(new[] { ",", "-" }, Letters(Page("27 28", Fixture("Cff-Subset.cff"))));
    }

    [TestMethod]
    public void NoEncodingOperatorMeansTheStandardEncoding()
    {
        // StandardEncoding has quoteright at 27, where WinAnsiEncoding has quotesingle.
        CollectionAssert.AreEqual(new[] { "’", "A" }, Letters(Page("27 41", Fixture("Cff-Standard.cff"))));
    }

    [TestMethod]
    public void OpenTypeProgramsUseTheirCffTable()
    {
        CollectionAssert.AreEqual(new[] { "A", "V", "中" }, Letters(Page("01 02 03", Fixture("Cff-Encoded.otf"), "OpenType")));
    }

    [TestMethod]
    public void TheFontDictionaryEncodingWins()
    {
        // A named encoding replaces the built-in one; Differences over no base apply to the built-in one.
        Assert.AreEqual("A", Letters(Page("41", Fixture("Cff-Codes.cff"), fontExtra: "/Encoding/WinAnsiEncoding"))[0]);
        CollectionAssert.AreEqual(new[] { "x", "e" }, Letters(Page("01 02", Fixture("Cff-Codes.cff"), fontExtra: "/Encoding<</Differences[1/x]>>")));
    }

    [TestMethod]
    public void CidKeyedProgramsHaveNoBuiltInEncoding()
    {
        OpenTypeFile file = OpenTypeFile.Parse(Fixture("Test-CID.otf"), 0);
        Assert.IsNull(new CffFont(file.Table("CFF ")).BuiltInEncoding());
    }

    [TestMethod]
    public void DamagedProgramsFallBackToTheStandardEncoding()
    {
        byte[] good = Fixture("Cff-Ranges.cff");
        var rng = new Random(7);
        for (int length = 0; length < good.Length; length += 3)
        {
            Assert.HasCount(2, Page("41 27", good.AsSpan(0, length).ToArray()).GetLetters());
        }

        for (int i = 0; i < 300; i++)
        {
            byte[] damaged = (byte[])good.Clone();
            damaged[rng.Next(damaged.Length)] = (byte)rng.Next(256);
            damaged[rng.Next(damaged.Length)] = (byte)rng.Next(256);
            Assert.HasCount(2, Page("41 27", damaged).GetLetters());
        }

        // A program cut short, and an encoding of an unknown format: StandardEncoding reads 27 as quoteright.
        CollectionAssert.AreEqual(new[] { "A", "’" }, Letters(Page("41 27", good.AsSpan(0, good.Length - 20).ToArray())));
        byte[] format5 = (byte[])good.Clone();
        format5[good.AsSpan().IndexOf(new byte[] { 0x81, 3, 0x41 })] = 5;
        CollectionAssert.AreEqual(new[] { "A", "’" }, Letters(Page("41 27", format5)));
    }
}
