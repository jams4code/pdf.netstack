using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class TextExtractionTests
{
    private const double Tolerance = 1e-6;

    private static string FontFile(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name);

    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToArray());

    /// <summary>
    /// A hand written one page file: page 3 draws <paramref name="content"/> (object 4) with the
    /// given resources; further objects start at 10.
    /// </summary>
    private static PdfPage Page(string content, string resources, params (int Number, string Body)[] objects) =>
        Page(Compat.Latin1.GetBytes(content), resources, objects);

    private static PdfPage Page(byte[] content, string resources, params (int Number, string Body)[] objects)
    {
        var b = new PdfBuilder();
        b.Set(1, "<</Type/Catalog/Pages 2 0 R>>");
        b.Set(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>");
        b.Set(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<{resources}>>/Contents 4 0 R>>");
        b.SetStream(4, "", content);
        foreach ((int number, string body) in objects)
        {
            // A body starting with "stream:" is a stream: dictionary entries, a bar, then the data.
            if (body.StartsWith("stream:"))
            {
                int bar = body.IndexOf('|');
                b.SetStream(number, body[7..bar], Compat.Latin1.GetBytes(body[(bar + 1)..]));
            }
            else
            {
                b.Set(number, body);
            }
        }

        return PdfDocument.Open(b.Build()).Pages[0];
    }

    private const string Helvetica = "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>";

    private static string CMap(string body) =>
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" + body + "\nendcmap CMapName currentdict /CMap defineresource pop end end";

    // Round trips through the library's own writer.

    [TestMethod]
    public void StandardFontTextRoundTrips()
    {
        const string text = "Grüße, déjà vu: 20 € (Œuvre) Łódź";
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString(text, PdfStandardFont.TimesRoman, 12, PdfColor.Black, 72, 100);
        }

        PdfPage read = Reopen(doc).Pages[0];
        Assert.AreEqual(text, read.ExtractText());
        CollectionAssert.AreEqual(new[] { "Grüße,", "déjà", "vu:", "20", "€", "(Œuvre)", "Łódź" }, read.GetWords().Select(w => w.Text).ToArray());
        Assert.HasCount(text.Length, read.GetLetters());
        Assert.IsTrue(read.GetLetters().All(l => l.FontName == "Times-Roman" && Math.Abs(l.FontSize - 12) < Tolerance));
    }

    [TestMethod]
    public void LetterBoundsAndBaselinesMatchTheWriter()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString("HA", PdfStandardFont.Helvetica, 12, PdfColor.Black, 72, 100);
        }

        IReadOnlyList<PdfLetter> letters = Reopen(doc).Pages[0].GetLetters();
        Assert.HasCount(2, letters);

        // The canvas measures from the top left; letters are in PDF space, from the bottom left.
        // Helvetica: H is 722 wide, A 667, ascender 718, descender -207.
        PdfLetter h = letters[0], a = letters[1];
        Assert.AreEqual("H", h.Text);
        Assert.AreEqual(72, h.BaselineStart.X, Tolerance);
        Assert.AreEqual(842 - 100, h.BaselineStart.Y, Tolerance);
        Assert.AreEqual(72 + 8.664, h.BaselineEnd.X, Tolerance);
        Assert.AreEqual(842 - 100, h.BaselineEnd.Y, Tolerance);
        Assert.AreEqual(72, h.Bounds.Left, Tolerance);
        Assert.AreEqual(72 + 8.664, h.Bounds.Right, Tolerance);
        Assert.AreEqual(742 - 0.207 * 12, h.Bounds.Bottom, Tolerance);
        Assert.AreEqual(742 + 0.718 * 12, h.Bounds.Top, Tolerance);
        Assert.AreEqual(72 + 8.664, a.BaselineStart.X, Tolerance);
        Assert.AreEqual(72 + 8.664 + 8.004, a.Bounds.Right, Tolerance);
        Assert.AreEqual(0, h.Angle, Tolerance);
        Assert.AreEqual(0, h.RenderMode);
        Assert.AreEqual("Helvetica", h.FontName);
    }

    [TestMethod]
    public void ParagraphsReadLineByLine()
    {
        const string first = "The quick brown fox jumps over the lazy dog and keeps running across the field until night falls.";
        const string second = "A second paragraph follows it.";
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfTextLayout layout;
        using (PdfGraphics g = page.CreateGraphics())
        {
            var style = new PdfTextStyle(PdfStandardFont.Helvetica, 11);
            layout = g.DrawText(first, style, 72, 72, 200);
            g.DrawText(second, new PdfTextStyle(PdfStandardFont.HelveticaBold, 14), 72, 72 + layout.Height + 20, 400);
        }

        string text = Reopen(doc).Pages[0].ExtractText();
        string[] lines = text.Split('\n');
        Assert.HasCount(layout.Lines.Count + 1, lines);
        Assert.AreEqual(first, string.Join(" ", lines.Take(layout.Lines.Count)));
        Assert.AreEqual(second, lines[^1]);
    }

    [TestMethod]
    public void EmbeddedTrueTypeTextRoundTrips()
    {
        // Test-Regular has A V T o H x, a composite Á, a check mark and an emoji outside the BMP.
        const string text = "AVoT HxÁ ✓\U0001F600";
        PdfOpenTypeFont font = PdfOpenTypeFont.Load(FontFile("Test-Regular.ttf"));
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString(text, font, 20, PdfColor.Black, 50, 50);
        }

        PdfPage read = Reopen(doc).Pages[0];
        Assert.AreEqual(text, read.ExtractText());
        PdfLetter smile = read.GetLetters()[^1];
        Assert.AreEqual("\U0001F600", smile.Text, "a surrogate pair is one letter");
        Assert.AreEqual(0.8 * 20, smile.BaselineEnd.X - smile.BaselineStart.X, 0.01);
        Assert.AreEqual(font.Name, read.GetLetters()[0].FontName, "the subset tag is removed");
    }

    [TestMethod]
    [DataRow("Test-Regular.otf")]
    [DataRow("Test-CID.otf")]
    public void EmbeddedCffTextRoundTrips(string file)
    {
        const string text = "HAVoc AT xo";
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString(text.Replace("c", ""), PdfOpenTypeFont.Load(FontFile(file)), 16, PdfColor.Black, 50, 50);
        }

        Assert.AreEqual(text.Replace("c", ""), Reopen(doc).Pages[0].ExtractText());
    }

    [TestMethod]
    public void ArabicReadsInLogicalOrder()
    {
        // beh meem meem, then lam alef (a ligature) and beh lam alef: the writer draws glyphs in
        // visual order and marks the run with ActualText.
        string text = "\u0628\u0645\u0645 \u0644\u0627 \u0628\u0644\u0627";
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString(text, PdfOpenTypeFont.Load(FontFile("Test-Arabic.ttf")), 20, PdfColor.Black, 50, 50);
        }

        PdfPage read = Reopen(doc).Pages[0];
        Assert.AreEqual(text, read.ExtractText());
        CollectionAssert.AreEqual(text.Split(' '), read.GetWords().Select(w => w.Text).ToArray());
    }

    [TestMethod]
    public void HtmlArabicAndLatinParagraphsReadInLogicalOrder()
    {
        var fonts = new PdfFontCollection();
        fonts.AddFile(FontFile("Test-Arabic.ttf"));
        string family = fonts.Families.Single();
        string arabic = "\u0628\u0645\u0645 \u0644\u0627 \u0628\u0644\u0627 \u0645\u0628\u0628";
        string html = $"<body style=\"font-family: '{family}'; font-size: 20px\"><p dir=rtl>{arabic}</p><p>To fifo</p><p>To {arabic} fifo</p></body>";
        PdfDocument doc = HtmlConverter.ConvertToDocument(html, new HtmlConverterOptions { Fonts = fonts, UseSystemFonts = false });

        string[] lines = Reopen(doc).Pages[0].ExtractText().Split('\n');
        CollectionAssert.AreEqual(new[] { arabic, "To fifo", $"To {arabic} fifo" }, lines);
    }

    [TestMethod]
    public void RotatedTextReadsAlongItsBaseline()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.RotateAtTransform(90, 300, 300);
            g.DrawString("Turned text", PdfStandardFont.Helvetica, 10, PdfColor.Black, 300, 300);
            g.DrawString("next line", PdfStandardFont.Helvetica, 10, PdfColor.Black, 300, 312);
        }

        PdfPage read = Reopen(doc).Pages[0];
        Assert.AreEqual("Turned text\nnext line", read.ExtractText());

        // A quarter turn clockwise on the canvas runs down the page: -90 degrees in PDF space.
        PdfLetter t = read.GetLetters()[0];
        Assert.AreEqual(-90, t.Angle, 1e-9);
        Assert.AreEqual(300, t.BaselineStart.X, 1e-9);
        Assert.AreEqual(842 - 300, t.BaselineStart.Y, 1e-9);
        Assert.AreEqual(842 - 300 - (0.611 * 10), t.BaselineEnd.Y, 1e-9);
    }

    [TestMethod]
    public void StrokedTextReportsItsRenderMode()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawString("Out", PdfStandardFont.Helvetica, 30, new PdfPen(PdfColor.Black, 1), null, 50, 50);
        }

        Assert.IsTrue(Reopen(doc).Pages[0].GetLetters().All(l => l.RenderMode == 1));
    }

    // Hand written content streams.

    [TestMethod]
    public void TjNumbersKernAndLargeOnesMakeSpaces()
    {
        PdfPage page = Page("BT /F1 10 Tf 72 700 Td [(Hel) 20 (lo) -300 (World) -40 (!)] TJ ET", Helvetica);
        Assert.AreEqual("Hello World!", page.ExtractText());
        IReadOnlyList<PdfLetter> letters = page.GetLetters();

        // H e l, then 20 thousandths back, then l o: Helvetica H 722, e 556, l 222.
        Assert.AreEqual(72 + 7.22 + 5.56 + 2.22 - 0.2, letters[3].BaselineStart.X, Tolerance);
        Assert.AreEqual(letters[4].BaselineEnd.X + 3, letters[5].BaselineStart.X, Tolerance);
        Assert.HasCount(2, page.GetWords());
    }

    [TestMethod]
    public void TextStateParametersMoveGlyphs()
    {
        // Tc adds after every glyph, Tw after code 32 only, Tz scales horizontally, Ts raises.
        // They are text state, so they last from one text object to the next.
        PdfPage page = Page(
            "BT /F1 10 Tf 1 Tc 72 700 Td (AB) Tj ET " +
            "BT /F1 10 Tf 0 Tc 5 Tw 72 650 Td (A B) Tj ET " +
            "BT /F1 10 Tf 50 Tz 72 600 Td (AB) Tj ET " +
            "BT /F1 10 Tf 100 Tz 3 Ts 72 550 Td (A) Tj ET",
            Helvetica);
        IReadOnlyList<PdfLetter> l = page.GetLetters();
        Assert.AreEqual(72 + 6.67 + 1, l[1].BaselineStart.X, Tolerance);
        Assert.AreEqual(72 + 6.67 + 2.78 + 5, l[4].BaselineStart.X, Tolerance);
        Assert.AreEqual(72 + 3.335, l[6].BaselineStart.X, Tolerance);
        Assert.AreEqual(3.335, l[5].Bounds.Width, Tolerance);
        Assert.AreEqual(553, l[7].BaselineStart.Y, Tolerance);
        Assert.AreEqual(553 - 2.07, l[7].Bounds.Bottom, Tolerance);
        Assert.AreEqual("AB\nA B\nAB\nA", page.ExtractText());
    }

    [TestMethod]
    public void LineOperatorsMoveToNewLines()
    {
        PdfPage page = Page(
            "BT /F1 10 Tf 14 TL 72 700 Td (One) Tj T* (Two) Tj (Three) ' 0 -20 TD (Four) Tj 1 0.5 (Five) \" T* (Six) Tj ET",
            Helvetica);
        Assert.AreEqual("One\nTwo\nThree\nFour\nFive\nSix", page.ExtractText());
        IReadOnlyList<PdfWord> words = page.GetWords();
        double[] baselines = words.Select(w => w.Letters[0].BaselineStart.Y).ToArray();

        // TD sets the leading to 20, which ' and " and T* then use; " also sets Tw and Tc.
        CollectionAssert.AreEqual(new double[] { 700, 686, 672, 652, 632, 612 }, baselines);
        IReadOnlyList<PdfLetter> five = words[4].Letters;
        Assert.AreEqual(five[0].BaselineEnd.X + 0.5, five[1].BaselineStart.X, Tolerance);
    }

    [TestMethod]
    public void TextInAFormUsesTheFormMatrix()
    {
        PdfPage page = Page(
            "q 2 0 0 2 0 0 cm /X1 Do Q",
            "/XObject<</X1 10 0 R>>",
            (10, "stream:/Type/XObject/Subtype/Form/BBox[0 0 500 500]/Matrix[1 0 0 1 100 50]/Resources<<" + Helvetica + ">>|BT /F1 10 Tf 10 20 Td (Hi) Tj ET"));
        Assert.AreEqual("Hi", page.ExtractText());
        PdfLetter h = page.GetLetters()[0];
        Assert.AreEqual(2 * (100 + 10), h.BaselineStart.X, Tolerance);
        Assert.AreEqual(2 * (50 + 20), h.BaselineStart.Y, Tolerance);
        Assert.AreEqual(20, h.FontSize, Tolerance);
        Assert.AreEqual(2 * 7.22, h.Bounds.Width, Tolerance);
    }

    [TestMethod]
    public void Type3WidthsGoThroughTheFontMatrix()
    {
        // Glyph space is 100 units to the em: a width of 50 is half the font size.
        PdfPage page = Page(
            "BT /T3 10 Tf 72 700 Td (ABA) Tj ET",
            "/Font<</T3 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type3/FontBBox[0 -20 100 80]/FontMatrix[0.01 0 0 0.01 0 0]/FirstChar 65/LastChar 66/Widths[50 30]" +
                 "/Encoding<</Differences[65/A/B]>>/CharProcs<</A 11 0 R/B 11 0 R>>/Resources<<>>>>"),
            (11, "stream:|50 0 d0"));
        Assert.AreEqual("ABA", page.ExtractText());
        IReadOnlyList<PdfLetter> l = page.GetLetters();
        Assert.AreEqual(72 + 5, l[1].BaselineStart.X, Tolerance);
        Assert.AreEqual(72 + 5 + 3, l[2].BaselineStart.X, Tolerance);
        Assert.AreEqual(700 - 2, l[0].Bounds.Bottom, Tolerance);
        Assert.AreEqual(700 + 8, l[0].Bounds.Top, Tolerance);
    }

    [TestMethod]
    public void DifferencesNamesMapThroughTheGlyphList()
    {
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td <01020304050607080941> Tj ET",
            "/Font<</F1 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type1/BaseFont/Custom/Encoding<</BaseEncoding/WinAnsiEncoding" +
                 "/Differences[1/uni20AC/afii10017/f_f_i/uni0041030A/u1F600/eacute.sc/g65/Lslash/uni00E9 65/Aring]>>>>"));
        Assert.AreEqual("\u20AC\u0410ffi" + "A\u030A" + "\U0001F600\u00E9A\u0141\u00E9\u00C5", page.ExtractText());
        Assert.AreEqual("ffi", page.GetLetters()[2].Text, "a ligature name is one letter");
    }

    [TestMethod]
    public void PredefinedEncodingsAndBuiltInSymbolSets()
    {
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td <8E27> Tj /F2 10 Tf <D0> Tj /F3 10 Tf <61> Tj /F4 10 Tf <34> Tj ET",
            "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Times-Roman/Encoding/MacRomanEncoding>>" +
            "/F2<</Type/Font/Subtype/Type1/BaseFont/Times-Roman>>" +
            "/F3<</Type/Font/Subtype/Type1/BaseFont/Symbol>>/F4<</Type/Font/Subtype/Type1/BaseFont/ZapfDingbats>>>>");

        // MacRoman 8E is e acute and 27 a straight quote; StandardEncoding D0 is an em dash.
        Assert.AreEqual("\u00E9'\u2014\u03B1\u2714", page.ExtractText().Replace(" ", ""));
    }

    [TestMethod]
    public void ToUnicodeRangesArraysAndLigatures()
    {
        string cmap = CMap(
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "2 beginbfchar <01> <00660069> <02> <D83DDE00> endbfchar\n" +
            "2 beginbfrange <10> <12> <0041> <20> <22> [<0058> <0059> <005A00480049>] endbfrange");
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td <0102101112202122> Tj ET",
            "/Font<</F1 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/ToUnicode 11 0 R>>"),
            (11, "stream:|" + cmap));
        IReadOnlyList<PdfLetter> letters = page.GetLetters();
        CollectionAssert.AreEqual(new[] { "fi", "\U0001F600", "A", "B", "C", "X", "Y", "ZHI" }, letters.Select(l => l.Text).ToArray());
        Assert.AreEqual("fi\U0001F600ABCXYZHI", page.ExtractText());
    }

    [TestMethod]
    public void CompositeIdentityFontUsesItsWidths()
    {
        string cmap = CMap(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "1 beginbfchar <0001> <0041> endbfchar 1 beginbfrange <0002> <0040> <0042> endbfrange");
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td <0001000200140030> Tj ET",
            "/Font<</F1 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type0/BaseFont/ABCDEF+Test-Identity-H/Encoding/Identity-H/DescendantFonts[12 0 R]/ToUnicode 11 0 R>>"),
            (11, "stream:|" + cmap),
            (12, "<</Type/Font/Subtype/CIDFontType2/BaseFont/ABCDEF+Test/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>" +
                 "/DW 900/W[1 [500 600] 10 20 700]/FontDescriptor 13 0 R>>"),
            (13, "<</Type/FontDescriptor/FontName/ABCDEF+Test/Flags 4/Ascent 800/Descent -200/FontBBox[0 -200 1000 800]>>"));
        IReadOnlyList<PdfLetter> l = page.GetLetters();
        Assert.AreEqual("ABTp", page.ExtractText());
        Assert.AreEqual("Test", l[0].FontName);

        // CID 1 is 500 wide, 2 is 600, 20 falls in the 10 to 20 range at 700, and 48 takes /DW.
        Assert.AreEqual(72 + 5, l[1].BaselineStart.X, Tolerance);
        Assert.AreEqual(72 + 5 + 6, l[2].BaselineStart.X, Tolerance);
        Assert.AreEqual(72 + 5 + 6 + 7, l[3].BaselineStart.X, Tolerance);
        Assert.AreEqual(9, l[3].Bounds.Width, Tolerance);
        Assert.AreEqual(698, l[0].Bounds.Bottom, Tolerance);
        Assert.AreEqual(708, l[0].Bounds.Top, Tolerance);
    }

    [TestMethod]
    public void EmbeddedCMapInheritsThroughUseCMap()
    {
        // The encoding has no code space of its own: usecmap brings Identity-H's two byte codes,
        // and its CID range sends codes 0x4141 to 0x4142 to CIDs 7 and 8.
        string encoding = CMap("/CMapName /Test-H def /Identity-H usecmap\n1 begincidrange <4141> <4142> 7 endcidrange");

        // The ToUnicode map is chained too: its /UseCMap stream maps the codes.
        string parent = CMap("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <4141> <4142> <0078> endbfrange");
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td <414141420009> Tj ET",
            "/Font<</F1 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type0/BaseFont/Test/Encoding 11 0 R/DescendantFonts[12 0 R]/ToUnicode 14 0 R>>"),
            (11, "stream:/Type/CMap/CMapName/Test-H|" + encoding),
            (12, "<</Type/Font/Subtype/CIDFontType0/BaseFont/Test/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/W[7 [100 200] 9 [300]]>>"),
            (14, "stream:|" + CMap("/Test-UCS usecmap 1 beginbfchar <0009> <0021> endbfchar")));
        IReadOnlyList<PdfLetter> l = page.GetLetters();
        Assert.HasCount(3, l);
        Assert.AreEqual(72 + 1, l[1].BaselineStart.X, Tolerance);
        Assert.AreEqual(72 + 1 + 2, l[2].BaselineStart.X, Tolerance);
        Assert.AreEqual(3, l[2].Bounds.Width, Tolerance);
        Assert.AreEqual("!", l[2].Text);

        // An encoding stream whose /UseCMap is another stream.
        PdfPage chained = Page(
            "BT /F1 10 Tf 72 700 Td <0102> Tj ET",
            "/Font<</F1 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type0/BaseFont/Test/Encoding 11 0 R/DescendantFonts[12 0 R]/ToUnicode 14 0 R>>"),
            (11, "stream:/Type/CMap/UseCMap 13 0 R|" + CMap("1 begincidchar <02> 9 endcidchar")),
            (12, "<</Type/Font/Subtype/CIDFontType0/BaseFont/Test/W[1 [100] 9 [300]]>>"),
            (13, "stream:/Type/CMap|" + CMap("1 begincodespacerange <00> <FF> endcodespacerange 1 begincidrange <00> <FF> 0 endcidrange")),
            (14, "stream:|" + parent.Replace("<0000> <FFFF>", "<00> <FF>").Replace("<4141> <4142>", "<01> <02>")));
        IReadOnlyList<PdfLetter> c = chained.GetLetters();
        Assert.AreEqual("xy", chained.ExtractText());
        Assert.AreEqual(1, c[0].Bounds.Width, Tolerance);
        Assert.AreEqual(3, c[1].Bounds.Width, Tolerance);
    }

    [TestMethod]
    public void VerticalWritingRunsDown()
    {
        string cmap = CMap("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0001> <0003> <4E00> endbfrange");
        PdfPage page = Page(
            "BT /F1 10 Tf 300 700 Td <000100020003> Tj ET",
            "/Font<</F1 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type0/BaseFont/Test/Encoding/Identity-V/DescendantFonts[12 0 R]/ToUnicode 11 0 R>>"),
            (11, "stream:|" + cmap),
            (12, "<</Type/Font/Subtype/CIDFontType0/BaseFont/Test/DW 1000/W2[3 [-500 500 880]]>>"));
        IReadOnlyList<PdfLetter> l = page.GetLetters();
        Assert.AreEqual("\u4E00\u4E01\u4E02", page.ExtractText());
        Assert.AreEqual(-90, l[0].Angle, 1e-9);

        // The default advance is -1000; the glyph hangs to the left of the vertical origin by half its width.
        Assert.AreEqual(690, l[1].BaselineStart.Y, Tolerance);
        Assert.AreEqual(680, l[2].BaselineStart.Y, Tolerance);
        Assert.AreEqual(675, l[2].BaselineEnd.Y, Tolerance, "W2 gives the third glyph an advance of -500");
        Assert.AreEqual(295, l[0].Bounds.Left, Tolerance);
        Assert.AreEqual(305, l[0].Bounds.Right, Tolerance);
    }

    [TestMethod]
    public void ActualTextReplacesItsSpan()
    {
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td (A) Tj /Span <</ActualText (fi)>> BDC (XY) Tj /Span <</ActualText (inner)>> BDC (Z) Tj EMC EMC " +
            "/Span /P1 BDC (Q) Tj EMC /Artifact BMC (B) Tj EMC /Span <</ActualText ()>> BDC (-) Tj EMC ET",
            Helvetica + "/Properties<</P1<</ActualText<FEFF00E9>>>>>");
        IReadOnlyList<PdfLetter> l = page.GetLetters();
        CollectionAssert.AreEqual(new[] { "A", "fi", "é", "B" }, l.Select(x => x.Text).ToArray());

        // The replacement covers the glyphs it replaces.
        Assert.AreEqual(72 + 6.67, l[1].Bounds.Left, Tolerance);
        Assert.AreEqual(72 + 6.67 + 6.67 + 6.67 + 6.11, l[1].Bounds.Right, Tolerance);
        Assert.AreEqual(72 + 6.67, l[1].BaselineStart.X, Tolerance);
        Assert.AreEqual(l[1].Bounds.Right, l[1].BaselineEnd.X, Tolerance);
        Assert.AreEqual("AfiéB", page.ExtractText());
    }

    [TestMethod]
    public void RightToLeftGlyphsInVisualOrderReadLogically()
    {
        // Codes 1 to 3 are shin, lamed and final mem, drawn left to right as they appear: mem first.
        string cmap = CMap("1 begincodespacerange <00> <FF> endcodespacerange 3 beginbfchar <01> <05E9> <02> <05DC> <03> <05DD> endbfchar");
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td (Hi ) Tj <030201> Tj ( 42) Tj ET",
            "/Font<</F1 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/ToUnicode 11 0 R>>"),
            (11, "stream:|" + cmap));
        Assert.AreEqual("Hi \u05E9\u05DC\u05DD 42", page.ExtractText());
    }

    [TestMethod]
    public void RightToLeftLineEndingInALatinWordReadsLogically()
    {
        // Shin lamed mem, then "To.": the period closing the right to left paragraph is drawn at
        // its left end, then the Latin word, then the Hebrew, mem first.
        string cmap = CMap("1 begincodespacerange <00> <FF> endcodespacerange 3 beginbfchar <01> <05E9> <02> <05DC> <03> <05DD> endbfchar");
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td (.To ) Tj <030201> Tj ET",
            "/Font<</F1 10 0 R>>",
            (10, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/ToUnicode 11 0 R>>"),
            (11, "stream:|" + cmap));
        Assert.AreEqual("\u05E9\u05DC\u05DD To.", page.ExtractText());
    }

    [TestMethod]
    public void HtmlRightToLeftParagraphsWithLatinWordsReadInLogicalOrder()
    {
        var fonts = new PdfFontCollection();
        fonts.AddFile(FontFile("Test-Arabic.ttf"));
        string family = fonts.Families.Single();

        // A right to left paragraph that ends in a Latin word, one that starts with one, and the
        // same text in left to right paragraphs. Punctuation next to the Latin word stays with it.
        string[] texts = ["\u0628\u0628 \u0628\u064E\u0628. To.", "To. \u0628\u064E\u0628 \u0628\u0628."];
        string html = $"<body style=\"font-family: '{family}'; font-size: 20px\">"
            + string.Concat(texts.Select(t => $"<p dir=rtl>{t}</p><p>{t}</p>")) + "</body>";
        PdfDocument doc = HtmlConverter.ConvertToDocument(html, new HtmlConverterOptions { Fonts = fonts, UseSystemFonts = false });

        string[] lines = Reopen(doc).Pages[0].ExtractText().Split('\n');
        CollectionAssert.AreEqual(new[] { texts[0], texts[0], texts[1], texts[1] }, lines);
    }

    [TestMethod]
    public void SpacesAreNotDoubledAndGapsSplitWords()
    {
        PdfPage page = Page("BT /F1 10 Tf 72 700 Td (Hello ) Tj ( World) Tj [-3000 (again)] TJ ET", Helvetica);
        Assert.AreEqual("Hello World again", page.ExtractText());
        Assert.HasCount(17, page.GetLetters(), "space glyphs are letters too");
    }

    [TestMethod]
    public void SuperscriptsStayOnTheirLineAndSizesDoNotMerge()
    {
        PdfPage page = Page(
            "BT /F1 30 Tf 72 700 Td (Title) Tj ET BT /F1 10 Tf 72 688 Td (x) Tj /F1 7 Tf 4 Ts (2) Tj 0 Ts /F1 10 Tf ( + y) Tj ET",
            Helvetica);
        Assert.AreEqual("Title\nx2 + y", page.ExtractText());
    }

    [TestMethod]
    public void FakeBoldCountsOnce()
    {
        PdfPage page = Page("BT /F1 10 Tf 72 700 Td (Bold) Tj ET BT /F1 10 Tf 72.3 700 Td (Bold) Tj ET", Helvetica);
        Assert.HasCount(8, page.GetLetters());
        Assert.AreEqual("Bold", page.ExtractText());
    }

    [TestMethod]
    public void RotatedPagesKeepDefaultUserSpace()
    {
        PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple("Turn").Build());
        doc.Pages[0].Rotation = 90;
        PdfPage page = Reopen(doc).Pages[0];
        Assert.AreEqual("Turn", page.ExtractText());
        Assert.AreEqual(72, page.GetLetters()[0].BaselineStart.X, Tolerance);
        Assert.AreEqual(700, page.GetLetters()[0].BaselineStart.Y, Tolerance);
    }

    [TestMethod]
    public void SymbolicTrueTypeTextComesBackFromThePrivateUseArea()
    {
        string cmap = CMap("1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <F041> endbfchar");
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td (AB) Tj /F2 10 Tf <B7> Tj ET",
            "/Font<</F1 10 0 R/F2 12 0 R>>",
            (10, "<</Type/Font/Subtype/TrueType/BaseFont/Wingdings/FirstChar 65/LastChar 66/Widths[500 500]/FontDescriptor 13 0 R/ToUnicode 11 0 R>>"),
            (11, "stream:|" + cmap),
            (12, "<</Type/Font/Subtype/TrueType/BaseFont/SymbolMT/FontDescriptor 13 0 R>>"),
            (13, "<</Type/FontDescriptor/FontName/Wingdings/Flags 4/Ascent 900/Descent -200>>"));

        // The private use value and the bare code read as the code; a Symbol font's code as its symbol.
        Assert.AreEqual("AB\u2022", page.ExtractText());
    }

    [TestMethod]
    public void UnmappedCodesReadAsReplacementCharacters()
    {
        PdfPage page = Page(
            "BT /F1 10 Tf 72 700 Td <00410042> Tj /F9 10 Tf (ok) Tj ET",
            "/Font<</F1<</Type/Font/Subtype/Type0/BaseFont/X/Encoding/Identity-H/DescendantFonts[<</Subtype/CIDFontType2/BaseFont/X>>]>>>>");

        // No ToUnicode and no font program; F9 is not in the resources and reads as StandardEncoding.
        Assert.AreEqual("\uFFFD\uFFFDok", page.ExtractText());
        Assert.AreEqual("F9", page.GetLetters()[^1].FontName);
    }

    [TestMethod]
    public void ImagesStillExtractAlongsideMarkedContent()
    {
        PdfPage page = Page(
            "/Figure <</ActualText (picture)>> BDC q 10 0 0 10 0 0 cm /Im1 Do Q EMC",
            "/XObject<</Im1 10 0 R>>",
            (10, "stream:/Type/XObject/Subtype/Image/Width 1/Height 1/BitsPerComponent 8/ColorSpace/DeviceGray|\u0080"));
        Assert.HasCount(1, page.GetImages());
        Assert.HasCount(0, page.GetLetters(), "an ActualText span without glyphs has nothing to replace");
    }

    // Hostile input: content, fonts and CMaps come from untrusted files.

    private const string HostileFonts =
        "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>/F2 10 0 R/F3 13 0 R>>/XObject<</X1 15 0 R>>";

    private static (int, string)[] HostileObjects() =>
    [
        (10, "<</Type/Font/Subtype/Type0/BaseFont/X/Encoding 11 0 R/DescendantFonts[12 0 R]/ToUnicode 11 0 R>>"),
        (11, "stream:|" + CMap("2 begincodespacerange <00> <7F> <8000> <FFFF> endcodespacerange 1 beginbfrange <00> <7F> <0041> endbfrange 1 begincidrange <8000> <FFFF> 1 endcidrange")),
        (12, "<</Type/Font/Subtype/CIDFontType0/BaseFont/X/W[1 [500] 2 9000 700]/W2[1 2 -900 400 800]>>"),
        (13, "<</Type/Font/Subtype/Type3/FontBBox[0 0 1 1]/FontMatrix[0.001 0 0 0.001 0 0]/FirstChar 0/LastChar 1/Widths[1 2]/Encoding<</Differences[0/a/b]>>/CharProcs<<>>>>"),
        (15, "stream:/Subtype/Form/BBox[0 0 10 10]/Resources<<" + HostileFonts + ">>|BT /F2 5 Tf (\u0080\u0001ab) Tj ET /X1 Do"),
    ];

    [TestMethod]
    public void RandomContentNeverThrows()
    {
        string[] pieces =
        [
            "BT ", "ET ", "/F1 12 Tf ", "/F2 9 Tf ", "/F3 20 Tf ", "/F9 3 Tf ", "(abc) Tj ", "<8001FF> Tj ", "[(a) -500 (b) 1e308 <00>] TJ ",
            "1 2 Td ", "3 TL T* ", "(x) ' ", "1 2 (y) \" ", "2 0 0 2 5 5 Tm ", "0 0 0 0 0 0 Tm ", "1e300 0 0 1e300 0 0 cm ", "q ", "Q ",
            "/Span <</ActualText (z)>> BDC ", "/P BMC ", "EMC ", "/X1 Do ", "50 Tz ", "-3 Ts ", "7 Tr ", "(", "<", "[", "]", ">>", "<<",
            "1 0 obj ", "NaN Tc ", "999999999999 Tw ",
        ];
        var random = new Random(1234);
        for (int run = 0; run < 300; run++)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 40; i++)
            {
                if (random.Next(4) == 0)
                {
                    for (int k = random.Next(1, 12); k > 0; k--) sb.Append((char)random.Next(256));
                }
                else
                {
                    sb.Append(pieces[random.Next(pieces.Length)]);
                }
            }

            PdfPage page = Page(sb.ToString(), HostileFonts, HostileObjects());
            _ = page.ExtractText();
            _ = page.GetWords();
        }
    }

    [TestMethod]
    public void MalformedCMapsAreReadAsFarAsTheyGo()
    {
        string[] damaged =
        [
            "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <0058> endbfchar 1 beginbfrange <01> [",
            "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <0058> endbfchar 1 beginbfchar <42> <00",
            "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <0058> endbfchar 2 beginbfrange <50> <40> <0041> <0102030405> <0102030406> <0041> endbfrange",
            "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <0058> endbfchar 1 beginbfrange <42> <43> [[<00>] /x 7] endbfrange",
            "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <0058> <42> endbfchar 1 beginbfchar <43> /space endbfchar",
            "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <0058> endbfchar ) } { 1 beginbfchar <41> <D800> <42> <0000> endbfchar",
        ];
        foreach (string body in damaged)
        {
            PdfPage page = Page(
                "BT /F1 10 Tf 72 700 Td (A) Tj ET",
                "/Font<</F1 10 0 R>>",
                (10, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/ToUnicode 11 0 R>>"),
                (11, "stream:|" + CMap(body)));
            string text = page.ExtractText();
            Assert.IsTrue(text is "X" or "\uFFFD", body);
        }

        // Random bytes as a ToUnicode map and as an encoding CMap.
        var random = new Random(99);
        for (int run = 0; run < 100; run++)
        {
            var noise = new char[random.Next(1, 400)];
            for (int i = 0; i < noise.Length; i++) noise[i] = (char)random.Next(256);
            PdfPage page = Page(
                "BT /F1 10 Tf 72 700 Td (ABC) Tj /F2 10 Tf (ABCD) Tj ET",
                "/Font<</F1 10 0 R/F2 12 0 R>>",
                (10, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/ToUnicode 11 0 R>>"),
                (11, "stream:|" + new string(noise)),
                (12, "<</Type/Font/Subtype/Type0/BaseFont/X/Encoding 11 0 R/ToUnicode 11 0 R/DescendantFonts[13 0 R]>>"),
                (13, "<</Type/Font/Subtype/CIDFontType2/BaseFont/X/FontDescriptor<</FontFile2 11 0 R>>>>"));
            Assert.IsGreaterThanOrEqualTo(3, page.GetLetters().Count);
        }
    }

    [TestMethod]
    public void HugeRangesAreNotExpanded()
    {
        // A four byte range over every code, and an array far longer than its range.
        string values = string.Concat(Enumerable.Repeat("<0041> ", 50_000));
        var cmap = Text.Extraction.CMap.Parse(Compat.Latin1.GetBytes(CMap(
            "1 begincodespacerange <00000000> <FFFFFFFF> endcodespacerange\n" +
            "2 beginbfrange <00000000> <FFFFFFFF> <0041> <00000010> <00000012> [" + values + "] endbfrange")));
        Assert.AreEqual("F", cmap.Unicode(5, 4));
        Assert.AreEqual("A", cmap.Unicode(0x11, 4), "the array covers its own range only");
        Assert.AreEqual("T", cmap.Unicode(0x13, 4), "past the array, the wide range applies");

        // Mappings beyond the cap are dropped, not stored.
        var many = new StringBuilder("1 begincodespacerange <000000> <FFFFFF> endcodespacerange\n");
        for (int i = 0; i < Text.Extraction.CMap.MaxEntries + 100; i++) many.Append("1 beginbfchar <").Append(i.ToString("X6", System.Globalization.CultureInfo.InvariantCulture)).Append("> <0042> endbfchar\n");
        var capped = Text.Extraction.CMap.Parse(Compat.Latin1.GetBytes(many.ToString()));
        Assert.AreEqual("B", capped.Unicode(10, 3));
        Assert.IsNull(capped.Unicode((uint)Text.Extraction.CMap.MaxEntries + 50, 3));
    }

    [TestMethod]
    public void UseCMapChainsAndCyclesEnd()
    {
        // A chain of 40 encoding CMaps, each naming the next as its parent, and a cycle of two.
        var objects = new List<(int, string)>
        {
            (10, "<</Type/Font/Subtype/Type0/BaseFont/X/Encoding 20 0 R/DescendantFonts[<</Subtype/CIDFontType0/BaseFont/X>>]>>"),
            (11, "<</Type/Font/Subtype/Type0/BaseFont/X/Encoding 100 0 R/DescendantFonts[<</Subtype/CIDFontType0/BaseFont/X>>]>>"),
            (100, "stream:/UseCMap 101 0 R|" + CMap("/Identity-H usecmap")),
            (101, "stream:/UseCMap 100 0 R|" + CMap(string.Concat(Enumerable.Repeat("/Self usecmap ", 1000)))),
        };
        for (int i = 0; i < 40; i++) objects.Add((20 + i, $"stream:/UseCMap {21 + i} 0 R|" + CMap("/Next usecmap 1 begincidchar <01> 5 endcidchar")));
        objects.Add((60, "stream:|" + CMap("1 begincodespacerange <00> <FF> endcodespacerange")));
        PdfPage page = Page("BT /F1 10 Tf (ab) Tj /F2 10 Tf (abcd) Tj ET", "/Font<</F1 10 0 R/F2 11 0 R>>", [.. objects]);
        Assert.IsGreaterThanOrEqualTo(3, page.GetLetters().Count);
    }

    [TestMethod]
    public void SelfDrawingFormsAndDamagedFontsDoNotStopExtraction()
    {
        PdfPage page = Page(
            "/X1 Do BT /F1 10 Tf (ab) Tj /F2 10 Tf <00010002> Tj /F3 10 Tf (cd) Tj /F4 10 Tf (ef) Tj /F5 10 Tf (eh) Tj ET",
            "/XObject<</X1 15 0 R>>/Font<</F1 10 0 R/F2 11 0 R/F3 12 0 R/F4 13 0 R/F5 14 0 R>>",
            (10, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/FirstChar 1e30/Widths(no)/Encoding 42/FontDescriptor<</Ascent 1e300/Descent 5/Flags -1>>>>"),
            (11, "<</Type/Font/Subtype/Type0/Encoding/Identity-H/DescendantFonts[null]/ToUnicode/Identity-H>>"),
            (12, "<</Type/Font/Subtype/Type0/Encoding/90ms-RKSJ-H/DescendantFonts[<</W[1 [2 3] 5 0 4294967295 500 [[1]]]/DW2[1]/W2 7>>]>>"),
            (13, "<</Type/Font/Subtype/Type3/FontMatrix[0 0 0 0 0 0]/FontBBox[1 2]/Widths[1e308 -5]/FirstChar 101/Encoding<</Differences[-5/a 300/b 3.5/c 101/zzz/G4/cid99999999]>>>>"),
            (14, "<</Type/Font/Subtype/Type1/BaseFont/Junk/FontDescriptor<</FontFile 16 0 R>>>>"),
            (15, "stream:/Subtype/Form/BBox[0 0 10 10]/Resources<</XObject<</X1 15 0 R>>/Font<</F1 10 0 R>>>>|BT /F1 9 Tf (in) Tj ET /X1 Do"),
            (16, "stream:/Length1 99999|%!PS-AdobeFont-1.0 /Encoding 256 array 0 1 255 {1 index exch /.notdef put} for dup 101 /e put dup 999 /x put dup put readonly def eexec \u0001\u0002"));
        IReadOnlyList<PdfLetter> letters = page.GetLetters();
        // The form draws once inside itself; 90ms-RKSJ-H reads (cd) as two one byte codes.
        Assert.HasCount(2 + 2 + 2 + 2 + 2 + 2, letters);
        Assert.AreEqual("in", letters[0].Text + letters[1].Text);
        Assert.AreEqual("e\uFFFD", letters[^2].Text + letters[^1].Text, "the program's built-in encoding names code 101 only");
        _ = page.ExtractText();
    }

    [TestMethod]
    public void LettersPerPageAreCapped()
    {
        var content = new StringBuilder("BT /F1 1 Tf (");
        content.Append('A', Text.Extraction.TextExtractor.MaxLetters + 1000);
        content.Append(") Tj ET");
        Assert.HasCount(Text.Extraction.TextExtractor.MaxLetters, Page(content.ToString(), Helvetica).GetLetters());
    }
}
