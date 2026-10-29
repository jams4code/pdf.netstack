using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class InvisibleTextTests
{
    private static PdfDocument Draw(Action<PdfGraphics> draw, int pages = 1)
    {
        PdfDocument doc = PdfDocument.Create();
        for (int i = 0; i < pages; i++)
        {
            using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
            draw(g);
        }

        return PdfDocument.Open(doc.ToArray());
    }

    private static string Content(PdfDocument doc, int page = 0)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.GetEncoding("ISO-8859-1").GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    private static PdfDictionary Font(PdfDocument doc, int page = 0)
    {
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[page].Dictionary["Resources"])!;
        var fonts = (PdfDictionary)doc.Resolve(resources["Font"])!;
        return (PdfDictionary)doc.Resolve(fonts.Single().Value)!;
    }

    [TestMethod]
    public void BuildsAValidFontProgram()
    {
        byte[] program = GlyphlessFont.FontProgram;
        Assert.IsLessThan(1024, program.Length, "a few hundred bytes");
        OpenTypeFile font = OpenTypeFile.ParseSubset(program);
        Assert.AreEqual(2, font.GlyphCount);
        Assert.AreEqual(GlyphlessFont.Advance, font.Advance(1));

        // The whole file sums to the magic number once checkSumAdjustment is in (OpenType, head).
        uint sum = 0;
        for (int i = 0; i < program.Length; i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++) word = (word << 8) | (i + k < program.Length ? program[i + k] : 0u);
            sum += word;
        }

        Assert.AreEqual(0xB1B0AFBAu, sum);
    }

    [TestMethod]
    public void WritesInvisibleTextStretchedToItsBox()
    {
        PdfDocument doc = Draw(g => g.DrawInvisibleText("Hello", 100, 200, 80, 14));
        string content = Content(doc);
        StringAssert.Contains(content, "3 Tr");
        StringAssert.Contains(content, "<00480065006C006C006F>Tj");

        // Five glyphs of half an em at 14 pt are 35 pt wide; the box is 80.
        Match tz = Regex.Match(content, @"([0-9.]+) Tz");
        Assert.AreEqual(80 / 35.0 * 100, double.Parse(tz.Groups[1].Value, CultureInfo.InvariantCulture), 1e-3);
        Assert.DoesNotContain("BDC", content, "Latin text copies back from its codes");

        PdfDictionary font = Font(doc);
        Assert.AreEqual("Type0", font.GetName("Subtype"));
        Assert.AreEqual("Identity-H", font.GetName("Encoding"));
        var cid = (PdfDictionary)doc.Resolve(((PdfArray)doc.Resolve(font["DescendantFonts"])!)[0])!;
        Assert.AreEqual("CIDFontType2", cid.GetName("Subtype"));
        var descriptor = (PdfDictionary)doc.Resolve(cid["FontDescriptor"])!;
        Assert.IsNotNull(descriptor["FontFile2"]);
        byte[] map = doc.GetStreamData((PdfStream)doc.Resolve(cid["CIDToGIDMap"])!);
        Assert.HasCount(131072, map);
        Assert.IsTrue(map.Where((b, i) => i % 2 == 1).All(b => b == 1), "every CID draws glyph 1");

        // The ToUnicode map is the identity on every code unit but the surrogates.
        string toUnicode = Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(font["ToUnicode"])!));
        Assert.HasCount(248, Regex.Matches(toUnicode, @"<(?<h>[0-9A-F]{2})00> <\k<h>FF> <\k<h>00>"));
        Assert.DoesNotContain("<D800>", toUnicode);
    }

    [TestMethod]
    public void CarriesActualTextWhereCodesAreNotEnough()
    {
        // Arabic is right to left; a character outside the BMP becomes U+FFFD in the codes.
        string arabic = Content(Draw(g => g.DrawInvisibleText("مرحبا", 10, 10, 50, 12)));
        StringAssert.Contains(arabic, "/Span <</ActualText<FEFF06450631062D06280627>>> BDC");
        string emoji = Content(Draw(g => g.DrawInvisibleText("a\U0001F600b", 10, 10, 30, 12)));
        StringAssert.Contains(emoji, "<0061FFFD0062>Tj");
        StringAssert.Contains(emoji, "/ActualText<FEFF0061D83DDE000062>");

        // Lone surrogates and control characters never throw.
        string odd = Content(Draw(g => g.DrawInvisibleText("x\uD800\ty", 10, 10, 30, 12)));
        StringAssert.Contains(odd, "<0078FFFD0079>Tj");
    }

    [TestMethod]
    public void RotatesAboutTheTopLeftCorner()
    {
        string content = Content(Draw(g => g.DrawInvisibleText("Up", 100, 300, 40, 10, -90)));
        StringAssert.Contains(content, "3 Tr");

        // The rotation goes into the CTM; the text matrix stays the plain flip to y up.
        StringAssert.Contains(content, "1 0 0 -1 0 10 Tm");
        Assert.IsTrue(Regex.IsMatch(content, @"0 -1 1 0 0 0 cm|6\.1\d*e-17|0 1 -1 0 0 0 cm"), content);
    }

    [TestMethod]
    public void SharesOneFontAndIgnoresEmptyInput()
    {
        PdfDocument doc = Draw(g =>
        {
            g.DrawInvisibleText("one", 0, 0, 20, 10);
            g.DrawInvisibleText("two", 0, 20, 20, 10);
            g.DrawInvisibleText("", 0, 40, 20, 10);
            g.DrawInvisibleText("none", 0, 60, 0, 10);
            g.DrawInvisibleText("none", 0, 60, 20, double.NaN);
        }, pages: 2);
        Assert.AreSame(Font(doc, 0), Font(doc, 1), "one font per document");
        Assert.HasCount(2, Regex.Matches(Content(doc), ">Tj"));
        Assert.DoesNotContain("006E006F006E0065", Content(doc));
    }

    [TestMethod]
    public void SavesAsPdfA()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawInvisibleText("Searchable", 50, 50, 100, 12);
        using var output = new MemoryStream();
        doc.Save(output, new PdfSaveOptions { Conformance = PdfConformance.PdfA2U });
        Assert.IsGreaterThan(0, output.Length);
    }
}
