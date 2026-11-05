using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;
using PdfNetStack.Hyphenation;

namespace PdfNetStack.Tests;

/// <summary>Hyphenation in both layouts. Courier is 6 points wide per glyph at size 10; the CSS here uses 10px, so 6 px.</summary>
[TestClass]
public sealed class HyphenatedLayoutTests
{
    private const char SoftHyphen = (char)0xAD;

    private static readonly PdfTextStyle Mono = new(PdfStandardFont.Courier, 10);

    private static PdfHyphenator English => PdfHyphenationPatterns.ForLanguage("en-us")!;

    private static PdfOpenTypeFont LigatureFont() =>
        PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hyphenation", "Test-Ligature.ttf"));

    private static PdfTextLayout Lay(string text, double width, PdfHyphenator? hyphenator = null, PdfTextAlignment alignment = PdfTextAlignment.Start, PdfTextStyle? style = null) =>
        PdfTextLayout.Create(new PdfParagraph { Hyphenator = hyphenator, Alignment = alignment }.Add(text, style ?? Mono), width);

    private static string[] LineTexts(PdfTextLayout layout) =>
        [.. layout.Lines.Select(l => layout.Paragraph.Text.Substring(l.Start, l.End - l.Start))];

    private static bool EndsWithHyphen(PdfTextLine line) => line.Runs[^1].Glyphs[^1].Text == SoftHyphen.ToString();

    [TestMethod]
    public void WithoutAHyphenatorNothingChanges()
    {
        PdfTextLayout layout = Lay("hyphenation", 50);
        CollectionAssert.AreEqual(new[] { "hyphenat", "ion" }, LineTexts(layout));
        Assert.IsFalse(EndsWithHyphen(layout.Lines[0]));
        Assert.AreEqual(48, layout.Lines[0].Width, 1e-9);
    }

    [TestMethod]
    public void BreaksAtTheLastPointThatFitsAndShowsAHyphen()
    {
        PdfTextLayout layout = Lay("hyphenation", 50, English);
        CollectionAssert.AreEqual(new[] { "hyphen", "ation" }, LineTexts(layout));
        Assert.AreEqual(42, layout.Lines[0].Width, 1e-9, "six letters and the hyphen");
        Assert.IsTrue(EndsWithHyphen(layout.Lines[0]));
        Assert.IsFalse(EndsWithHyphen(layout.Lines[1]));
        Assert.AreEqual(30, layout.Lines[1].Width, 1e-9);
        Assert.AreEqual("hyphenation", layout.Paragraph.Text, "the text keeps the word whole");

        // The hyphen must fit: at 41 points only the first point does.
        CollectionAssert.AreEqual(new[] { "hy", "phen", "ation" }, LineTexts(Lay("hyphenation", 41, English)));

        // With room for the word, there is no reason to break it.
        PdfTextLayout wide = Lay("hyphenation", 66, English);
        CollectionAssert.AreEqual(new[] { "hyphenation" }, LineTexts(wide));
        Assert.IsFalse(EndsWithHyphen(wide.Lines[0]));
    }

    [TestMethod]
    public void FillsLinesThroughAParagraph()
    {
        PdfTextLayout layout = Lay("the hyphenation of information", 78, English);
        CollectionAssert.AreEqual(new[] { "the hyphen", "ation of in", "formation" }, LineTexts(layout));
        CollectionAssert.AreEqual(new[] { true, true, false }, layout.Lines.Select(EndsWithHyphen).ToArray());
        CollectionAssert.AreEqual(new[] { 66.0, 72.0, 54.0 }, layout.Lines.Select(l => l.Width).ToArray());
        Assert.AreEqual(layout.Paragraph.Text.Length, layout.Lines[^1].End);
    }

    [TestMethod]
    public void JustifiedLinesFillTheWidthWithSpacesOnly()
    {
        PdfTextLayout layout = Lay("the hyphenation of information", 78, English, PdfTextAlignment.Justify);
        Assert.AreEqual(78, layout.Lines[0].Width, 1e-9);
        Assert.AreEqual(78, layout.Lines[1].Width, 1e-9);
        Assert.AreEqual(54, layout.Lines[2].Width, 1e-9, "the last line stays as it is");
        Assert.AreEqual(600, layout.Lines[0].Runs[^1].Glyphs[^1].Advance, 1e-9, "the hyphen keeps its width");
    }

    [TestMethod]
    public void SoftHyphensKeepPriority()
    {
        string word = "in" + SoftHyphen + "formation";
        PdfTextLayout layout = Lay(word, 60, English);
        CollectionAssert.AreEqual(new[] { "in" + SoftHyphen, "formation" }, LineTexts(layout), "the word's other points are not used");
        Assert.AreEqual(18, layout.Lines[0].Width, 1e-9);
    }

    [TestMethod]
    public void AddressesAndNumbersDoNotHyphenate()
    {
        PdfTextLayout address = Lay("https://example.com/information", 100, English);
        Assert.IsTrue(address.Lines.All(l => !EndsWithHyphen(l)));
        PdfTextLayout number = Lay("123456789012345678901234", 60, English);
        Assert.IsTrue(number.Lines.All(l => !EndsWithHyphen(l)));
        PdfTextLayout caps = Lay("INFORMATION", 40, English);
        Assert.IsTrue(caps.Lines.All(l => !EndsWithHyphen(l)));
    }

    [TestMethod]
    public void KeepsLigaturesWhole()
    {
        // Advances at size 10: o 5, the fi ligature 5.2, a hyphen 3.
        var style = new PdfTextStyle(LigatureFont(), 10);
        PdfHyphenator inside = PdfHyphenator.Create(["f1i"]) with { MinLeft = 1, MinRight = 1, MinWordLength = 1 };
        PdfTextLayout layout = Lay("oofioo", 20, inside, style: style);
        Assert.IsTrue(layout.Lines.All(l => !EndsWithHyphen(l)), "the only point is inside the ligature");
        Assert.IsTrue(layout.Lines.SelectMany(l => l.Runs).SelectMany(r => r.Glyphs).Any(g => g.Text == "fi" || g.Glyph == 5), "the ligature was formed");

        PdfHyphenator around = PdfHyphenator.Create(["o1f", "i1o"]) with { MinLeft = 1, MinRight = 1, MinWordLength = 1 };
        PdfTextLayout broken = Lay("oofioo", 19, around, style: style);
        CollectionAssert.AreEqual(new[] { "oofi", "oo" }, LineTexts(broken));
        Assert.AreEqual(5 + 5 + 5.2 + 3, broken.Lines[0].Width, 1e-9);
        Assert.IsTrue(EndsWithHyphen(broken.Lines[0]));
    }

    [TestMethod]
    public void AFontWithoutAHyphenDoesNotBreakWords()
    {
        var style = new PdfTextStyle(PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Arabic.ttf")), 10);
        PdfHyphenator h = PdfHyphenator.Create(["o1x"]) with { MinLeft = 1, MinRight = 1, MinWordLength = 1 };
        PdfTextLayout layout = Lay("oxox", 12, h, style: style);
        Assert.IsTrue(layout.Lines.All(l => !EndsWithHyphen(l)));
    }

    /// <summary>The hyphen at a soft hyphen in the middle of a run took a later space's index and widened with the spaces.</summary>
    [TestMethod]
    public void AHyphenBeforeAnEndingSpaceDoesNotJustify()
    {
        string text = "aa hy" + SoftHyphen + "phen bb ";
        PdfTextLayout layout = Lay(text, 40, null, PdfTextAlignment.Justify);
        CollectionAssert.AreEqual(new[] { "aa hy" + SoftHyphen, "phen ", "bb " }, LineTexts(layout));
        Assert.AreEqual(600, layout.Lines[0].Runs[^1].Glyphs[^1].Advance, 1e-9);
        Assert.AreEqual(40, layout.Lines[0].Width, 1e-9);
    }

    [TestMethod]
    public void ExtractedTextHasNoHardHyphen()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            g.DrawText(new PdfParagraph { Hyphenator = English }.Add("the hyphenation of information", new PdfTextStyle(PdfStandardFont.Helvetica, 10)), 50, 100, 60);
        }

        string text = PdfDocument.Open(doc.ToArray()).Pages[0].ExtractText();
        Assert.DoesNotContain("-", text);
        Assert.AreEqual("the hyphenation of information", text.Replace("\u00AD", "").Replace("\r", "").Replace("\n", "").Replace("hyphen ation", "hyphenation"), text);
    }

    private static BoxFragment Layout(string html, double width, Func<string, PdfHyphenator?>? hyphenators = null)
    {
        HtmlDocument doc = HtmlParser.Parse(html);
        var resolver = new StyleResolver(new CssMedia(width, 1000));
        resolver.AddStyleSheet("body { margin: 0; font: 10px Courier; line-height: 1 } p { margin: 0 }", CssOrigin.Author);
        resolver.AddDocument(doc);
        BlockBox root = new BoxBuilder(resolver, _ => null).Build(doc);
        var fonts = new FontResolver(null, useSystemFonts: false) { Hyphenators = hyphenators is null ? null : new HyphenatorCache(hyphenators) };
        return new LayoutEngine(fonts).Layout(root, width);
    }

    /// <summary>The text of each line in a box with a shown hyphen as "-".</summary>
    private static string[] Lines(BoxFragment root) =>
        [.. HtmlLayoutFixture.All(root).OfType<LineFragment>().Select(l =>
            string.Concat(l.Items.OfType<TextFragment>().Select(t => t.Text.Replace(SoftHyphen.ToString(), t.Glyphs[^1].Text == "-" ? "-" : ""))).TrimEnd())];

    private static string[] HtmlLines(string html, double width = 50, Func<string, PdfHyphenator?>? hyphenators = null) =>
        Lines(Layout(html, width, hyphenators ?? PdfHyphenationPatterns.ForLanguage));

    [TestMethod]
    public void HyphensAutoUsesTheLanguageOfTheElement()
    {
        CollectionAssert.AreEqual(new[] { "hyphen-", "ation" }, HtmlLines("""<p lang="en-US" style="hyphens: auto">hyphenation</p>"""));
    }

    [TestMethod]
    public void WithoutTheOptionHyphensAutoChangesNothing()
    {
        CollectionAssert.AreEqual(new[] { "hyphenation" }, Lines(Layout("""<p lang="en" style="hyphens: auto">hyphenation</p>""", 50)));
    }

    [TestMethod]
    public void OnlyAutoHyphenates()
    {
        CollectionAssert.AreEqual(new[] { "hyphenation" }, HtmlLines("""<p lang="en">hyphenation</p>"""), "the initial value is manual");
        CollectionAssert.AreEqual(new[] { "hyphenation" }, HtmlLines("""<p lang="en" style="hyphens: manual">hyphenation</p>"""));
        CollectionAssert.AreEqual(new[] { "hyphenation" }, HtmlLines("""<p lang="en" style="hyphens: none">hyphenation</p>"""));
        CollectionAssert.AreEqual(new[] { "hyphenation" }, HtmlLines("""<p lang="en" style="hyphens: auto; white-space: nowrap">hyphenation</p>"""));
    }

    [TestMethod]
    public void NeedsAKnownLanguage()
    {
        CollectionAssert.AreEqual(new[] { "hyphenation" }, HtmlLines("""<p style="hyphens: auto">hyphenation</p>"""), "no lang attribute");
        CollectionAssert.AreEqual(new[] { "hyphenation" }, HtmlLines("""<p lang="" style="hyphens: auto">hyphenation</p>"""), "an empty one");
        CollectionAssert.AreEqual(new[] { "hyphenation" }, HtmlLines("""<p lang="ru" style="hyphens: auto">hyphenation</p>"""), "no patterns");
        CollectionAssert.AreEqual(new[] { "hyphenation" }, HtmlLines("""<p lang="en" style="hyphens: auto">hyphenation</p>""", hyphenators: _ => null));
    }

    [TestMethod]
    public void LanguageAndHyphensInherit()
    {
        CollectionAssert.AreEqual(new[] { "hyphen-", "ation" },
            HtmlLines("""<html lang="en"><body style="hyphens: auto"><div><p>hyphenation</p></div></body></html>"""));

        // A span in another language follows that language: French min right is 2, and its patterns differ.
        CollectionAssert.AreEqual(new[] { "in-", "for-", "ma-", "tion" },
            HtmlLines("""<html lang="en"><body style="hyphens: auto"><p><span lang="fr">information</span></p></body></html>""", 30));

        // An inner element can turn it off.
        CollectionAssert.AreEqual(new[] { "hyphenation" },
            HtmlLines("""<html lang="en"><body style="hyphens: auto"><p><span style="hyphens: none">hyphenation</span></p></body></html>"""));
    }

    [TestMethod]
    public void AWordAcrossInlineElementsIsOneWord()
    {
        CollectionAssert.AreEqual(new[] { "hyphen-", "ation" },
            HtmlLines("""<p lang="en" style="hyphens: auto"><b>hy</b>phen<i>ation</i></p>"""));
    }

    [TestMethod]
    public void SoftHyphensAndAddressesInHtml()
    {
        CollectionAssert.AreEqual(new[] { "in-", "formation" },
            HtmlLines("""<p lang="en" style="hyphens: auto">in&shy;formation</p>""", 60));
        CollectionAssert.AreEqual(new[] { "example.com/information" }.SelectMany(s => s.Split('\n')).ToArray(),
            HtmlLines("""<p lang="en" style="hyphens: auto">example.com/information</p>""", 200));
        string[] narrow = HtmlLines("""<p lang="en" style="hyphens: auto">https://example.com/information</p>""", 100);
        Assert.IsTrue(narrow.All(l => l.Length > 0 && l[^1] != '-'), string.Join("|", narrow));
    }

    [TestMethod]
    public void BreakAllStaysAsItWas()
    {
        CollectionAssert.AreEqual(new[] { "hyphenat", "ion" },
            HtmlLines("""<p lang="en" style="hyphens: auto; word-break: break-all">hyphenation</p>"""));
    }

    [TestMethod]
    public void UppercasedTextHyphenatesLikeTheWordsAsWritten()
    {
        string[] expected = ["HYPHEN-", "ATION IN-", "FORMATION"];
        CollectionAssert.AreEqual(expected,
            HtmlLines("""<p lang="en" style="hyphens: auto; text-transform: uppercase">hyphenation information</p>""", 60));
        CollectionAssert.AreEqual(expected,
            HtmlLines("""<p lang="en" style="hyphens: auto; text-transform: uppercase">Hyphenation Information</p>""", 60), "a capital first letter is still a word");

        // White space collapsed before the transform moves nothing.
        CollectionAssert.AreEqual(expected,
            HtmlLines("""<p lang="en" style="hyphens: auto; text-transform: uppercase">   hyphenation &#10;&#9;  information   </p>""", 60));

        // One word across inline elements, and through a span that sets the transform itself.
        CollectionAssert.AreEqual(new[] { "HYPHEN-", "ATION" },
            HtmlLines("""<p lang="en" style="hyphens: auto; text-transform: uppercase"><b>hy</b>phen<i>ation</i></p>"""));
        CollectionAssert.AreEqual(new[] { "HYPHEN-", "ation" },
            HtmlLines("""<p lang="en" style="hyphens: auto"><span style="text-transform: uppercase">hyphen</span>ation</p>"""));

        // A narrow column measures the same points it lays out.
        CollectionAssert.AreEqual(new[] { "HY-", "PHEN-", "ATION" },
            Lines(Layout("""<table style="width: 1px; border-spacing: 0"><tr><td style="padding: 0; hyphens: auto; text-transform: uppercase" lang="en">hyphenation</td></tr></table>""", 600, PdfHyphenationPatterns.ForLanguage)));
    }

    [TestMethod]
    public void TextWrittenInCapitalsStaysWhole()
    {
        CollectionAssert.AreEqual(new[] { "HYPHENATION", "INFORMATION" },
            HtmlLines("""<p lang="en" style="hyphens: auto">HYPHENATION INFORMATION</p>""", 60), "written so, no transform");
        CollectionAssert.AreEqual(new[] { "HYPHENATION", "INFORMATION" },
            HtmlLines("""<p lang="en" style="hyphens: auto; text-transform: uppercase">HYPHENATION INFORMATION</p>""", 60), "written so, and uppercased too");
        CollectionAssert.AreEqual(new[] { "INFORMATIONSYSTEM" },
            HtmlLines("""<p lang="en" style="hyphens: auto; text-transform: uppercase">InformationSystem</p>""", 60), "a capital past the first letter");

        // Without uppercase the text as laid out is the text as written, and lowercase has no capitals left.
        CollectionAssert.AreEqual(new[] { "hyphen-", "ation" },
            HtmlLines("""<p lang="en" style="hyphens: auto; text-transform: lowercase">HYPHENATION</p>"""));
        CollectionAssert.AreEqual(new[] { "Hyphen-", "ation" },
            HtmlLines("""<p lang="en" style="hyphens: auto; text-transform: capitalize">hyphenation</p>"""));
    }

    [TestMethod]
    public void SharpSBecomesSSAndTheBreaksStayInPlace()
    {
        CollectionAssert.AreEqual(new[] { "STRASSE" }, Lines(Layout("""<p style="text-transform: uppercase">stra&szlig;e</p>""", 600)), "with or without hyphenation");

        // stra-ßen-bahn: the point before the sharp s, and one after it, which moves by one.
        CollectionAssert.AreEqual(new[] { "STRA-", "SSEN-", "BAHN" },
            HtmlLines("""<p lang="de" style="hyphens: auto; text-transform: uppercase">Stra&szlig;enbahn</p>""", 36));

        // Across inline elements, the shift goes with the word.
        CollectionAssert.AreEqual(new[] { "STRA-", "SSEN-", "BAHN" },
            HtmlLines("""<p lang="de" style="hyphens: auto; text-transform: uppercase"><b>Stra</b>&szlig;en<i>bahn</i></p>""", 36));

        // Text after the box moves by one too, here not uppercased and so with its own case.
        CollectionAssert.AreEqual(new[] { "STRASSE", "Silben-", "trennung" },
            HtmlLines("""<p lang="de" style="hyphens: auto"><span style="text-transform: uppercase">Stra&szlig;e</span> Silbentrennung</p>""", 48));

        // Two sharp s in one word, so the second point moves by two.
        CollectionAssert.AreEqual(new[] { "GROSS-", "STRA-", "SSE" },
            HtmlLines("""<p lang="de" style="hyphens: auto; text-transform: uppercase">Gro&szlig;stra&szlig;e</p>""", 36));
    }

    [TestMethod]
    public void UppercasedTextConvertsEndToEnd()
    {
        var options = new HtmlConverterOptions { UseSystemFonts = false, PageStyle = "size: 200px 200px; margin: 0", Hyphenators = PdfHyphenationPatterns.ForLanguage };
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf("""<p lang="en" style="margin: 0; font: 10px Courier; width: 60px; hyphens: auto; text-transform: uppercase">hyphenation information</p>""", options));
        string text = doc.Pages[0].ExtractText();
        Assert.DoesNotContain("-", text);
        Assert.AreEqual("HYPHENATIONINFORMATION", text.Replace(((char)0xAD).ToString(), "").Replace("\r", "").Replace("\n", "").Replace(" ", ""), text);
        Assert.Contains(((char)0xAD).ToString(), text, "the shown hyphens are marked");
    }

    [TestMethod]
    public void MinContentCountsHyphenationPoints()
    {
        // A narrow table cell: hy- phen- ation, where phen- and ation are five glyphs, 30 px. The
        // layout at the column's width shares its break positions with the measurement.
        BoxFragment table = Layout("""<table style="width: 1px; border-spacing: 0"><tr><td id="cell" style="padding: 0; hyphens: auto" lang="en">hyphenation</td></tr></table>""", 600, PdfHyphenationPatterns.ForLanguage);
        CollectionAssert.AreEqual(new[] { "hy-", "phen-", "ation" }, Lines(table));
        Assert.AreEqual(30, HtmlLayoutFixture.ById(table, "cell").Width, HtmlLayoutFixture.Tolerance);
    }

    [TestMethod]
    public void JustifiedHtmlFillsTheLine()
    {
        BoxFragment root = Layout("""<p lang="en" style="hyphens: auto; text-align: justify">the hyphenation of information</p>""", 78, PdfHyphenationPatterns.ForLanguage);
        string[] lines = Lines(root);
        CollectionAssert.AreEqual(new[] { "the hyphen-", "ation of in-", "formation" }, lines);
        LineFragment first = HtmlLayoutFixture.All(root).OfType<LineFragment>().First();
        TextFragment text = first.Items.OfType<TextFragment>().Single();
        Assert.AreEqual(78, text.Width, HtmlLayoutFixture.Tolerance);
    }

    [TestMethod]
    public void ConvertsEndToEnd()
    {
        int asked = 0;
        var options = new HtmlConverterOptions
        {
            UseSystemFonts = false,
            PageStyle = "size: 200px 400px; margin: 0",
            Hyphenators = tag =>
            {
                asked++;
                return PdfHyphenationPatterns.ForLanguage(tag);
            },
        };
        string html = """
            <html lang="de"><body style="margin: 0; font: 12px Helvetica; hyphens: auto; text-align: justify; width: 90px">
            <p>Die Rechtschreibung regelt die Silbentrennung im Bundesverfassungsgericht.</p>
            <p lang="en-GB">The hyphenation of British programmes.</p>
            </body></html>
            """;
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, options));
        string text = doc.Pages[0].ExtractText();
        Assert.AreEqual(2, asked, "once per language");
        Assert.DoesNotContain("-", text);
        string joined = text.Replace("\u00AD", "").Replace("\r", " ").Replace("\n", " ");
        StringAssert.Contains(joined.Replace(" ", ""), "DieRechtschreibungregeltdieSilbentrennungimBundesverfassungsgericht.");
        StringAssert.Contains(joined.Replace(" ", ""), "ThehyphenationofBritishprogrammes.");

        // Default options: the same document does not hyphenate at all.
        string plain = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false, PageStyle = options.PageStyle })).Pages[0].ExtractText();
        StringAssert.Contains(plain, "Bundesverfassungsgericht");
    }

    [TestMethod]
    public void OptionsSurviveAFileConversion()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".html");
        File.WriteAllText(path, """<p lang="en" style="hyphens: auto; width: 60px; font: 12px Courier">hyphenation hyphenation hyphenation</p>""");
        try
        {
            int asked = 0;
            HtmlConverter.ConvertFileToDocument(path, new HtmlConverterOptions
            {
                UseSystemFonts = false,
                Hyphenators = tag =>
                {
                    asked++;
                    return PdfHyphenationPatterns.ForLanguage(tag);
                },
            });
            Assert.AreEqual(1, asked);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
