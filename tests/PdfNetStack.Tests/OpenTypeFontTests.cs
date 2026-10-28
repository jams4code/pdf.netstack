using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// Embedded fonts against the synthetic fixtures of tools/make_font_fixtures.py. Glyph ids:
/// .notdef 0, space 1, A 2, V 3, T 4, o 5, H 6, x 7, acute 8, Aacute 9 (A + acute), smile 10
/// (U+1F600), check 11. Advances in thousandths: A and V 600, T 550, o 500; the TrueType font has
/// 2048 units per em, so A is 1229 units, 600.1 thousandths.
/// </summary>
[TestClass]
public sealed class OpenTypeFontTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name));

    private static PdfOpenTypeFont Load(string name, int index = 0) => PdfOpenTypeFont.Load(Fixture(name), index);

    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(Plain);
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    private static PdfDocument Draw(PdfFont font, string text)
    {
        PdfDocument doc = PdfDocument.Create();
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawString(text, font, 12, PdfColor.Black, 72, 100);
        return doc;
    }

    private static string Content(PdfDocument doc)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[0].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    /// <summary>The Type 0 font, its descendant, the descriptor and the decoded font program.</summary>
    private static (PdfDictionary Type0, PdfDictionary Cid, PdfDictionary Descriptor, PdfStream Program) Parts(PdfDocument doc)
    {
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        var type0 = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(resources["Font"])!)["F1"])!;
        var cid = (PdfDictionary)doc.Resolve(((PdfArray)doc.Resolve(type0["DescendantFonts"])!)[0])!;
        var descriptor = (PdfDictionary)doc.Resolve(cid["FontDescriptor"])!;
        var program = (PdfStream)doc.Resolve(descriptor["FontFile2"] ?? descriptor["FontFile3"])!;
        return (type0, cid, descriptor, program);
    }

    private static string Name(PdfObject? o) => ((PdfName)o!).Value;

    private static double Number(PdfObject? o) => ((PdfNumber)o!).ToDouble();

    [TestMethod]
    public void ReadsNamesAndMetrics()
    {
        PdfOpenTypeFont f = Load("Test-Regular.ttf");
        Assert.AreEqual("PdfNetStackTest-Regular", f.Name);
        Assert.AreEqual("PdfNetStack Test", f.FamilyName);
        Assert.AreEqual("Regular", f.StyleName);
        Assert.AreEqual(400, f.Weight);
        Assert.IsFalse(f.IsBold);
        Assert.IsFalse(f.IsItalic);
        Assert.AreEqual(12, f.GlyphCount);
        Assert.IsTrue(f.AllowsEmbedding);

        // No USE_TYPO_METRICS: hhea 1900, -500, 0 of 2048.
        Assert.AreEqual(1900 / 2048.0, f.Ascent, 1e-12);
        Assert.AreEqual(500 / 2048.0, f.Descent, 1e-12);
        Assert.AreEqual(0, f.LineGap);
        Assert.AreEqual(1434 / 2048.0, f.CapHeight, 1e-12);
        Assert.AreEqual(0.5, f.XHeight, 1e-12);

        // post: underline top at -205, 102 thick; OS/2: strikeout top at 614, 102 thick.
        Assert.AreEqual((205 + 51) / 2048.0, f.UnderlinePosition, 1e-12);
        Assert.AreEqual(102 / 2048.0, f.UnderlineThickness, 1e-12);
        Assert.AreEqual((614 - 51) / 2048.0, f.StrikeoutPosition, 1e-12);
    }

    [TestMethod]
    public void TypoMetricsWhenTheFontAsksForThem()
    {
        Assert.AreEqual(2, PdfOpenTypeFont.CountFonts(Fixture("Test.ttc")));
        PdfOpenTypeFont bold = Load("Test.ttc", 1);
        Assert.AreEqual("PdfNetStackTest-Bold", bold.Name);
        Assert.AreEqual(700, bold.Weight);
        Assert.IsTrue(bold.IsBold);
        Assert.AreEqual(1600 / 2048.0, bold.Ascent, 1e-12);
        Assert.AreEqual(400 / 2048.0, bold.Descent, 1e-12);
        Assert.AreEqual(200 / 2048.0, bold.LineGap, 1e-12);
        Assert.AreEqual("PdfNetStackTest-Regular", Load("Test.ttc", 0).Name);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Load("Test.ttc", 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Load("Test-Regular.ttf", 1));
    }

    [TestMethod]
    public void MeasuresWithGposKerning()
    {
        PdfOpenTypeFont f = Load("Test-Regular.ttf");

        // Glyph pair (format 1): A V -164 units. Class pair (format 2, second subtable): T o, T x -123.
        Assert.AreEqual(600.1 - 80.08 + 600.1, f.MeasureString("AV", 1000), 1e-9);
        Assert.AreEqual(600.1 + 600.1, f.MeasureString("VA", 1000), 1e-9);
        Assert.AreEqual(549.8 - 60.06 + 500, f.MeasureString("To", 1000), 1e-9);
        Assert.AreEqual(549.8 - 60.06 + 479.98, f.MeasureString("Tx", 1000), 1e-9);
        Assert.AreEqual(549.8 + 600.1, f.MeasureString("TA", 1000), 1e-9);
    }

    [TestMethod]
    public void MeasuresWithTheLegacyKernTable()
    {
        PdfOpenTypeFont f = Load("Test-Regular.otf");
        Assert.AreEqual(1120, f.MeasureString("AV", 1000), 1e-9);
        Assert.AreEqual(990, f.MeasureString("To", 1000), 1e-9);
        Assert.AreEqual(1030, f.MeasureString("Tx", 1000), 1e-9);
    }

    [TestMethod]
    public void SupplementaryCharactersUseTheFullCmap()
    {
        PdfOpenTypeFont f = Load("Test-Regular.ttf");
        Assert.IsTrue(f.HasGlyph(0x1F600));
        Assert.IsTrue(f.CanRender("Á😀✓"));
        Assert.IsFalse(f.HasGlyph('B'));
        Assert.AreEqual(799.8, f.MeasureString("😀", 1000), 1e-9);
    }

    [TestMethod]
    public void TrueTypeEmbedsAsCidFontType2()
    {
        PdfDocument doc = Reopen(Draw(Load("Test-Regular.ttf"), "AV Á😀"));
        StringAssert.Contains(Content(doc), "BT\n/F1 12 Tf\n1 0 0 -1 72 100 Tm\n[<0002>80.08<000300010009000A>]TJ\nET\n");

        (PdfDictionary type0, PdfDictionary cid, PdfDictionary descriptor, PdfStream program) = Parts(doc);
        Assert.AreEqual("Type0", Name(type0["Subtype"]));
        Assert.AreEqual("Identity-H", Name(type0["Encoding"]));
        StringAssert.Matches(Name(type0["BaseFont"]), new Regex(@"^[A-Z]{6}\+PdfNetStackTest-Regular$"));
        Assert.AreEqual("CIDFontType2", Name(cid["Subtype"]));
        Assert.AreEqual("Identity", Name(cid["CIDToGIDMap"]));
        Assert.AreEqual("[1 [250 600.1 600.1] 9 [600.1 799.8]]", Flatten(cid["W"]));
        Assert.AreEqual(1900 / 2048.0 * 1000, Number(descriptor["Ascent"]), 0.01);
        Assert.AreEqual(4, Number(descriptor["Flags"]));

        byte[] data = doc.GetStreamData(program);
        Assert.AreEqual(data.Length, Number(program.Dictionary["Length1"]));

        // The subset ends at the last glyph used, keeps the composite's components and drops the rest.
        OpenTypeFile subset = OpenTypeFile.ParseSubset(data);
        Assert.AreEqual(11, subset.GlyphCount);
        foreach (int g in new[] { 0, 2, 3, 8, 9, 10 }) // space (1) has no outline in the original either
        {
            (int s, int e) = subset.GlyphRange(g);
            Assert.IsGreaterThan(s, e, $"glyph {g} kept");
        }

        foreach (int g in new[] { 4, 5, 6, 7 })
        {
            (int s, int e) = subset.GlyphRange(g);
            Assert.AreEqual(s, e, $"glyph {g} dropped");
            Assert.AreEqual(0, subset.Advance(g));
        }

        Assert.AreEqual(1229, subset.Advance(2));
        Assert.AreEqual(0xB1B0AFBA, FontData.Checksum(data));

        string toUnicode = Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(type0["ToUnicode"])!));
        StringAssert.Contains(toUnicode, "<0002> <0041>");
        StringAssert.Contains(toUnicode, "<0009> <00C1>");
        StringAssert.Contains(toUnicode, "<000A> <D83DDE00>");
        StringAssert.Contains(toUnicode, "5 beginbfchar");
    }

    [TestMethod]
    public void NameKeyedCffEmbedsAsCidFontType0()
    {
        byte[] original = Load("Test-Regular.otf").File.Table("CFF ").ToArray();
        PdfDocument doc = Reopen(Draw(Load("Test-Regular.otf"), "AV"));
        StringAssert.Contains(Content(doc), "[<0002>80<0003>]TJ");

        (_, PdfDictionary cid, PdfDictionary descriptor, PdfStream program) = Parts(doc);
        Assert.AreEqual("CIDFontType0", Name(cid["Subtype"]));
        Assert.IsNull(cid["CIDToGIDMap"]);
        Assert.IsNotNull(descriptor["FontFile3"]);
        Assert.AreEqual("CIDFontType0C", Name(program.Dictionary["Subtype"]));
        Assert.AreEqual("[2 [600 600]]", Flatten(cid["W"]));

        byte[] data = doc.GetStreamData(program);
        var subset = new CffFont(data);
        Assert.IsFalse(subset.IsCidKeyed);
        Assert.AreEqual(12, subset.GlyphCount);
        Assert.IsLessThan(original.Length, data.Length);
    }

    [TestMethod]
    public void CidKeyedCffUsesTheCharsetCids()
    {
        PdfOpenTypeFont font = Load("Test-CID.otf");
        PdfDocument doc = Reopen(Draw(font, "AV"));

        // A is glyph 2, CID 106; V is glyph 3, CID 109.
        StringAssert.Contains(Content(doc), "[<006A>80<006D>]TJ");
        (PdfDictionary type0, PdfDictionary cid, _, PdfStream program) = Parts(doc);
        Assert.AreEqual("[106 [600] 109 [600]]", Flatten(cid["W"]));
        StringAssert.Contains(Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(type0["ToUnicode"])!)), "<006A> <0041>");

        var subset = new CffFont(doc.GetStreamData(program));
        Assert.IsTrue(subset.IsCidKeyed);
        Assert.AreEqual(106, subset.GlyphToCid[2]);
        Assert.AreEqual(109, subset.GlyphToCid[3]);
    }

    [TestMethod]
    public void WoffLoadsLikeTheTtf()
    {
        PdfOpenTypeFont ttf = Load("Test-Regular.ttf"), woff = Load("Test-Regular.woff");
        Assert.AreEqual(ttf.Name, woff.Name);
        Assert.AreEqual(ttf.Ascent, woff.Ascent);
        Assert.AreEqual(ttf.MeasureString("AV To", 12), woff.MeasureString("AV To", 12));
        Reopen(Draw(woff, "AV"));
    }

    [TestMethod]
    public void RestrictedFontsMeasureButDoNotEmbed()
    {
        PdfOpenTypeFont f = Load("Test-Restricted.ttf");
        Assert.IsFalse(f.AllowsEmbedding);
        Assert.AreEqual(1120.12, f.MeasureString("AV", 1000), 1e-9);
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => Draw(f, "AV"));
        StringAssert.Contains(e.Message, "does not allow embedding");
    }

    [TestMethod]
    public void SubsetGrowsAcrossSaves()
    {
        PdfOpenTypeFont font = Load("Test-Regular.ttf");
        PdfDocument doc = Draw(font, "A");
        byte[] first = doc.ToArray(Plain);
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("To", font, 12, PdfColor.Black, 72, 72);
        PdfDocument reopened = Reopen(doc);

        (PdfDictionary type0, PdfDictionary cid, _, _) = Parts(reopened);
        Assert.AreEqual("[2 [600.1] 4 [549.8 500]]", Flatten(cid["W"]));
        Assert.AreNotEqual(Name(type0["BaseFont"]), Name(Parts(PdfDocument.Open(first)).Type0["BaseFont"]));

        // Same glyphs, same subset name: separate documents and saves give identical fonts.
        Assert.AreEqual(Name(Parts(Reopen(Draw(font, "AV"))).Type0["BaseFont"]), Name(Parts(Reopen(Draw(font, "VA"))).Type0["BaseFont"]));
    }

    [TestMethod]
    public void BadFontsRaiseFontFormatException()
    {
        byte[] ttf = Fixture("Test-Regular.ttf");
        Assert.ThrowsExactly<FontFormatException>(() => PdfOpenTypeFont.Load(new byte[40]));
        Assert.ThrowsExactly<FontFormatException>(() => PdfOpenTypeFont.Load(ttf.AsSpan(0, 100).ToArray()));
        Assert.ThrowsExactly<FontFormatException>(() => PdfOpenTypeFont.Load("%PDF-1.7 not a font....."u8.ToArray()));
        byte[] woff2 = [.. "wOF2"u8, .. new byte[60]];
        StringAssert.Contains(Assert.ThrowsExactly<FontFormatException>(() => PdfOpenTypeFont.Load(woff2)).Message, "WOFF2");
    }

    /// <summary>Thousands of corrupted fonts: each loads, measures and embeds, or fails with a font error.</summary>
    [TestMethod]
    public void CorruptedFontsFailCleanly()
    {
        var random = new Random(20261028);
        foreach (string name in new[] { "Test-Regular.ttf", "Test-Regular.otf", "Test-CID.otf", "Test-Regular.woff" })
        {
            byte[] original = Fixture(name);
            for (int round = 0; round < 1500; round++)
            {
                byte[] data = (byte[])original.Clone();
                int flips = 1 + random.Next(8);
                for (int k = 0; k < flips; k++) data[random.Next(data.Length)] = (byte)random.Next(256);
                try
                {
                    PdfOpenTypeFont font = PdfOpenTypeFont.Load(data);
                    string text = font.CanRender("AVTo Á") ? "AVTo Á" : "";
                    font.MeasureString(text, 12);
                    if (text.Length > 0 && font.AllowsEmbedding) Draw(font, text).ToArray(Plain);
                }
                catch (FontFormatException)
                {
                }
                catch (Exception e)
                {
                    Assert.Fail($"{name} round {round}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                }
            }
        }
    }

    [TestMethod]
    public void TrueTypeAndStandardFontsShareAPage()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            g.DrawString("AV", Load("Test-Regular.ttf"), 12, PdfColor.Black, 72, 100);
            g.DrawString("Hello", PdfStandardFont.Helvetica, 12, PdfColor.Black, 72, 120);
        }

        string content = Content(Reopen(doc));
        StringAssert.Contains(content, "/F1 12 Tf");
        StringAssert.Contains(content, "/F2 12 Tf\n1 0 0 -1 72 120 Tm\n(Hello)Tj");
    }

    private static string Flatten(PdfObject? o) => o switch
    {
        PdfArray a => "[" + string.Join(" ", a.Select(Flatten)) + "]",
        PdfNumber n => n.ToString(),
        _ => o?.ToString() ?? "null",
    };
}
