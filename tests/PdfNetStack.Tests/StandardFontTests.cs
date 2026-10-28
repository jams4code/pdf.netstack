using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class StandardFontTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };
    private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");

    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(Plain);
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    /// <summary>All content streams of a page, decoded byte for byte.</summary>
    private static string Content(PdfDocument doc, int page = 0)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Latin1.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    private static PdfDictionary Font(PdfDocument doc, string name, int page = 0)
    {
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[page].Dictionary["Resources"])!;
        var fonts = (PdfDictionary)doc.Resolve(resources["Font"])!;
        return (PdfDictionary)doc.Resolve(fonts[name])!;
    }

    private static PdfDocument Draw(PdfFont font, params string[] lines)
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using PdfGraphics g = page.CreateGraphics();
        for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], font, 12, PdfColor.Black, 72, 100 + 20 * i);
        return doc;
    }

    [TestMethod]
    public void MeasuresWithTheAdobeWidths()
    {
        // H 722, e 556, l 222, l 222, o 556 in Helvetica; Courier is 600 for every glyph.
        Assert.AreEqual(22.78, PdfStandardFont.Helvetica.MeasureString("Hello", 10), 1e-9);
        Assert.AreEqual(30, PdfStandardFont.Courier.MeasureString("Hello", 10), 1e-9);
        Assert.AreEqual(0, PdfStandardFont.TimesRoman.MeasureString("", 10));
        Assert.IsTrue(PdfStandardFont.CourierBold.IsFixedPitch);
        Assert.IsFalse(PdfStandardFont.TimesBold.IsFixedPitch);
    }

    [TestMethod]
    public void MetricsAndNames()
    {
        PdfStandardFont h = PdfStandardFont.HelveticaBoldOblique;
        Assert.AreEqual("Helvetica-BoldOblique", h.Name);
        Assert.AreEqual("Helvetica", h.FamilyName);
        Assert.IsTrue(h.IsBold);
        Assert.IsTrue(h.IsItalic);
        Assert.AreEqual(-12, h.ItalicAngle);
        Assert.AreEqual(0.718, h.CapHeight, 1e-9);
        Assert.AreEqual(0.1, h.UnderlinePosition, 1e-9);
        Assert.AreEqual(1.15, h.GetLineHeight(1), 1e-9);
        Assert.HasCount(14, PdfStandardFont.All);
        Assert.AreSame(PdfStandardFont.TimesItalic, PdfStandardFont.FromName("Times-Italic"));
        Assert.IsNull(PdfStandardFont.FromName("Arial"));
        Assert.IsFalse(PdfStandardFont.TimesRoman.IsItalic);
    }

    [TestMethod]
    public void DrawStringWritesOneTextObject()
    {
        PdfDocument doc = Reopen(Draw(PdfStandardFont.Helvetica, "Hello", "World"));
        string content = Content(doc);
        StringAssert.Contains(content, "BT\n/F1 12 Tf\n1 0 0 -1 72 100 Tm\n(Hello)Tj\nET\n");

        // Same font and size: no second Tf.
        StringAssert.Contains(content, "BT\n1 0 0 -1 72 120 Tm\n(World)Tj\nET\n");

        PdfDictionary font = Font(doc, "F1");
        Assert.AreEqual("Type1", ((PdfName)font["Subtype"]!).Value);
        Assert.AreEqual("Helvetica", ((PdfName)font["BaseFont"]!).Value);
        Assert.AreEqual("WinAnsiEncoding", ((PdfName)font["Encoding"]!).Value);
        Assert.AreEqual(72, ((PdfNumber)font["FirstChar"]!).ToDouble()); // H
        Assert.AreEqual(114, ((PdfNumber)font["LastChar"]!).ToDouble()); // r
        var widths = (PdfArray)font["Widths"]!;
        Assert.AreEqual(114 - 72 + 1, widths.Count);
        Assert.AreEqual(722, ((PdfNumber)widths[0]).ToDouble());
        Assert.AreEqual(0, ((PdfNumber)widths[1]).ToDouble()); // I is not used
    }

    [TestMethod]
    public void WindowsCharactersUseWinAnsiCodes()
    {
        string content = Content(Draw(PdfStandardFont.TimesRoman, "€ “quoted” – café"));
        StringAssert.Contains(content, "(\u0080 \u0093quoted\u0094 \u0096 café)Tj");
    }

    [TestMethod]
    public void OtherLatinCharactersGetDifferences()
    {
        PdfDocument doc = Reopen(Draw(PdfStandardFont.Helvetica, "Łódź − Łó"));
        StringAssert.Contains(Content(doc), "(\u007Fód\u0081 \u008D \u007Fó)Tj");

        var encoding = (PdfDictionary)Font(doc, "F1")["Encoding"]!;
        Assert.AreEqual("WinAnsiEncoding", ((PdfName)encoding["BaseEncoding"]!).Value);
        var differences = (PdfArray)encoding["Differences"]!;
        string[] expected = ["127", "Lslash", "129", "zacute", "141", "minus"];
        CollectionAssert.AreEqual(expected, differences.Select(o => o is PdfName n ? n.Value : o.ToString()).ToArray());
    }

    [TestMethod]
    public void FreeCodesRunOutWithAClearError()
    {
        int[] extra = Enumerable.Range(0x100, 0x2200)
            .Where(c => PdfStandardFont.Helvetica.HasGlyph(c) && StandardFontResource.WinAnsiCode(c) < 0)
            .ToArray();
        Assert.IsGreaterThan(37, extra.Length);

        string fits = string.Concat(extra.Take(37).Select(char.ConvertFromUtf32));
        Draw(PdfStandardFont.Helvetica, fits).ToArray(Plain);

        string tooMany = string.Concat(extra.Take(38).Select(char.ConvertFromUtf32));
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => Draw(PdfStandardFont.Helvetica, tooMany));
        StringAssert.Contains(e.Message, "Embed a font");
    }

    [TestMethod]
    public void StringDelimitersAreEscaped()
    {
        StringAssert.Contains(Content(Draw(PdfStandardFont.Courier, @"a(b)c\")), @"(a\(b\)c\\)Tj");
    }

    [TestMethod]
    public void MissingGlyphsAreReported()
    {
        PdfStandardFont h = PdfStandardFont.Helvetica;
        Assert.IsFalse(h.HasGlyph('中'));
        Assert.IsFalse(h.CanRender("Price: 中"));
        Assert.IsTrue(h.CanRender("Zürich\t​"));
        var e = Assert.ThrowsExactly<ArgumentException>(() => Draw(h, "Price: 中"));
        StringAssert.Contains(e.Message, "U+4E2D");
        Assert.ThrowsExactly<ArgumentException>(() => h.MeasureString("😀", 10));
    }

    [TestMethod]
    public void ControlAndFormatCharactersAreSkipped()
    {
        PdfStandardFont h = PdfStandardFont.Helvetica;
        Assert.AreEqual(h.MeasureString("AB", 10), h.MeasureString("A\tB​­\n", 10), 1e-9);
        StringAssert.Contains(Content(Draw(h, "A‍B")), "(AB)Tj");
        string nothing = Content(Draw(h, "​"));
        Assert.DoesNotContain("BT", nothing);
    }

    [TestMethod]
    public void NoBreakSpaceAndHyphensUseLatinGlyphs()
    {
        StringAssert.Contains(Content(Draw(PdfStandardFont.Helvetica, "a b‑c")), "(a b-c)Tj");
    }

    [TestMethod]
    public void SymbolFontsUseTheirBuiltInEncoding()
    {
        PdfDocument doc = Reopen(Draw(PdfStandardFont.ZapfDingbats, "✓✗"));
        StringAssert.Contains(Content(doc), "(37)Tj");
        PdfDictionary font = Font(doc, "F1");
        Assert.IsNull(font["Encoding"]);
        Assert.AreEqual("ZapfDingbats", ((PdfName)font["BaseFont"]!).Value);

        StringAssert.Contains(Content(Draw(PdfStandardFont.Symbol, "αβ≤∞")), "(ab£¥)Tj");
        Assert.IsFalse(PdfStandardFont.Symbol.HasGlyph('A')); // its A is Greek Alpha, U+0391
    }

    [TestMethod]
    public void FontIsSharedAcrossPages()
    {
        PdfDocument doc = PdfDocument.Create();
        for (int i = 0; i < 2; i++)
        {
            using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
            g.DrawString("Page", PdfStandardFont.Helvetica, 12, PdfColor.Black, 72, 72);
            g.DrawString("Bold", PdfStandardFont.HelveticaBold, 12, PdfColor.Black, 72, 90);
        }

        PdfDocument reopened = Reopen(doc);
        var refs = Enumerable.Range(0, 2).Select(p =>
        {
            var resources = (PdfDictionary)reopened.Resolve(reopened.Pages[p].Dictionary["Resources"])!;
            var fonts = (PdfDictionary)reopened.Resolve(resources["Font"])!;
            return ((PdfReference)fonts["F1"]!).Number;
        }).ToArray();
        Assert.AreEqual(refs[0], refs[1]);
        Assert.AreEqual("Helvetica-Bold", ((PdfName)Font(reopened, "F2")["BaseFont"]!).Value);
    }

    [TestMethod]
    public void GradientTextClipsToTheGlyphs()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            var brush = new PdfLinearGradientBrush(new PdfPoint(72, 0), new PdfPoint(300, 0), PdfColor.Rgb(1, 0, 0), PdfColor.Rgb(0, 0, 1));
            g.DrawString("Gradient", PdfStandardFont.HelveticaBold, 36, brush, 72, 100);
            g.DrawString("Plain", PdfStandardFont.HelveticaBold, 36, PdfColor.Black, 72, 150);
        }

        string content = Content(doc);
        StringAssert.Contains(content, "q\nBT\n/F1 36 Tf\n7 Tr\n1 0 0 -1 72 100 Tm\n(Gradient)Tj\nET\n/Sh1 sh\nQ\n");

        // The restore put back render mode 0 and dropped the font; both are written again.
        StringAssert.Contains(content, "BT\n/F1 36 Tf\n1 0 0 -1 72 150 Tm\n(Plain)Tj\nET\n");
        Assert.AreEqual(1, content.Split(new[] { " Tr\n" }, StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public void OutlinedText()
    {
        string content;
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            g.DrawString("Outline", PdfStandardFont.TimesBold, 40, new PdfPen(PdfColor.Rgb(0, 0, 1), 1), null, 72, 100);
            g.DrawString("Both", PdfStandardFont.TimesBold, 40, new PdfPen(PdfColor.Rgb(0, 0, 1), 1), PdfColor.Rgb(1, 1, 0), 72, 160);
        }

        content = Content(doc);
        StringAssert.Contains(content, "1 Tr\n1 0 0 -1 72 100 Tm\n(Outline)Tj");
        StringAssert.Contains(content, "2 Tr\n1 0 0 -1 72 160 Tm\n(Both)Tj");
    }

    [TestMethod]
    public void SavingTwiceKeepsEveryGlyph()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics()) g.DrawString("AB", PdfStandardFont.Helvetica, 12, PdfColor.Black, 72, 72);
        doc.ToArray(Plain);
        using (PdfGraphics g = page.CreateGraphics()) g.DrawString("Łz", PdfStandardFont.Helvetica, 12, PdfColor.Black, 72, 90);

        PdfDocument reopened = Reopen(doc);
        PdfDictionary font = Font(reopened, "F1");
        Assert.AreEqual(65, ((PdfNumber)font["FirstChar"]!).ToDouble());
        Assert.AreEqual(127, ((PdfNumber)font["LastChar"]!).ToDouble()); // Ł took the first free code
        Assert.IsInstanceOfType<PdfDictionary>(font["Encoding"]);

        // The second canvas found the font already in the page resources and reused its name.
        var resources = (PdfDictionary)reopened.Resolve(reopened.Pages[0].Dictionary["Resources"])!;
        Assert.AreEqual(1, ((PdfDictionary)reopened.Resolve(resources["Font"])!).Count);
        StringAssert.Contains(Content(reopened), "/F1 12 Tf\n1 0 0 -1 72 90 Tm");
    }

    [TestMethod]
    public void ArgumentsAreValidated()
    {
        using PdfGraphics g = PdfDocument.Create().Pages.Add(PdfRectangle.A4).CreateGraphics();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => g.DrawString("x", PdfStandardFont.Helvetica, 0, PdfColor.Black, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => g.DrawString("x", PdfStandardFont.Helvetica, double.NaN, PdfColor.Black, 0, 0));
        Assert.ThrowsExactly<ArgumentNullException>(() => g.DrawString(null!, PdfStandardFont.Helvetica, 12, PdfColor.Black, 0, 0));
        Assert.ThrowsExactly<ArgumentNullException>(() => g.DrawString("x", null!, 12, PdfColor.Black, 0, 0));
    }
}
