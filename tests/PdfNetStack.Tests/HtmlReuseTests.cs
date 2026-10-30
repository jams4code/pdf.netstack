using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Html;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;
using PdfNetStack.Objects;
using PdfNetStack.Text;

namespace PdfNetStack.Tests;

/// <summary>
/// The work HTML conversion shares, caches or passes over gives the same result as doing it in
/// full: shaped runs, measured inline content, pages painted with the paint index, lazily found
/// fallback fonts, lookup digests and saving without the optimizer's proof.
/// </summary>
[TestClass]
public sealed class HtmlReuseTests
{
    private static PdfOpenTypeFont Arabic() =>
        PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Arabic.ttf"));

    private static BlockBox Build(string html, double width = 600)
    {
        HtmlDocument doc = HtmlParser.Parse(html);
        var resolver = new StyleResolver(new CssMedia(width, 1000));
        resolver.AddDocument(doc);
        return new BoxBuilder(resolver).Build(doc);
    }

    /// <summary>The first box, depth first, whose children are all inline-level.</summary>
    private static Box InlineContainer(Box box) => FindInline(box) ?? throw new AssertFailedException("no inline content");

    private static Box? FindInline(Box box) =>
        box.Children.Count > 0 && box.Children.All(c => c.IsInlineLevel) ? box : box.Children.Select(FindInline).FirstOrDefault(b => b is not null);

    private static string Describe(List<LineFragment> lines) => string.Join("\n", lines.Select(l =>
        FormattableString.Invariant($"{l.X:R} {l.Y:R} {l.Width:R} {l.Height:R} {l.Baseline:R}: ")
        + string.Join(" | ", l.Items.Select(i => i is TextFragment t
            ? FormattableString.Invariant($"{t.Text} {t.X:R} {t.Width:R} {t.JustifySpace:R} ") + string.Join(",", t.Glyphs.Select(g => FormattableString.Invariant($"{g.Glyph}@{g.Cluster}+{g.Advance:R}")))
            : FormattableString.Invariant($"{i.GetType().Name} {i.X:R} {i.Y:R} {i.Width:R} {i.Height:R}")))));

    [TestMethod]
    public void ShapedRunsAreTheSameWhereverTheyStand()
    {
        PdfOpenTypeFont font = Arabic();
        var cache = new ShapingCache();
        const string first = "To fio ببب x", second = "x To fio ببب x To fio";
        foreach ((string text, int start, int end) in new[] { (first, 0, 7), (second, 2, 9), (first, 7, 10), (second, 9, 12), (second, 14, 20) })
        {
            List<ShapedGlyph> expected = font.ShapeRun(text, start, end, text[start] > 0x600, text[start] > 0x600 ? "arab" : "latn");
            List<ShapedGlyph> shaped = cache.Shape(font, text, start, end, text[start] > 0x600, text[start] > 0x600 ? "arab" : "latn");
            CollectionAssert.AreEqual(expected, shaped, $"{start} to {end} of \"{text}\"");
        }

        // A run past the length kept is shaped each time, the same way.
        string paragraph = string.Concat(Enumerable.Repeat("To fio x ", 40));
        Assert.IsGreaterThan(ShapingCache.MaxRunLength, paragraph.Length);
        CollectionAssert.AreEqual(font.ShapeRun(paragraph, 0, paragraph.Length, false, "latn"), cache.Shape(font, paragraph, 0, paragraph.Length, false, "latn"));
    }

    [TestMethod]
    public void MeasuredInlineContentLaysOutAsIfNew()
    {
        Box p = InlineContainer(Build("""
            <p style="text-align: justify; text-indent: 12px">A paragraph <b>with bold</b> and <span style="padding: 0 4px; margin-left: 3px; border: 1px solid">a box</span>
            that wraps over several lines of text, long enough to break more than once at this width.</p>
            """));
        var engine = new LayoutEngine(new FontResolver(null, useSystemFonts: false));
        var measured = new InlineLayout(engine, p.Style, p, p.Children, double.MaxValue / 4, intrinsic: true);
        foreach (double width in new[] { 120.0, 233.5, 600 })
        {
            InlineLayout reused = measured.AtWidth(width) ?? throw new AssertFailedException("the content does not depend on the width");
            string expected = Describe(new InlineLayout(engine, p.Style, p, p.Children, width).Layout(10, 20, out double expectedHeight));
            Assert.AreEqual(expected, Describe(reused.Layout(10, 20, out double height)), $"width {width}");
            Assert.AreEqual(expectedHeight, height);
        }

        // Percentages of the width and atomic inlines are worked out at the width: no reuse.
        Box percent = InlineContainer(Build("<p>a <span style=\"padding-left: 10%\">b</span> c</p>"));
        Assert.IsNull(new InlineLayout(engine, percent.Style, percent, percent.Children, double.MaxValue / 4, intrinsic: true).AtWidth(300));
        Box atomic = InlineContainer(Build("<p>a <span style=\"display: inline-block\">b</span> c</p>"));
        Assert.IsNull(new InlineLayout(engine, atomic.Style, atomic, atomic.Children, double.MaxValue / 4, intrinsic: true).AtWidth(300));
    }

    [TestMethod]
    public void PagesPaintTheSameWithTheIndex()
    {
        var html = new StringBuilder("<body style=\"margin: 0\">");
        for (int i = 0; i < 40; i++)
        {
            html.Append(i % 7 == 3 ? "<div style=\"overflow: hidden; height: 30px; background: #eee\"><p>clipped " : "<p style=\"border-bottom: 1px solid\">paragraph ");
            html.Append(i).Append(" <a href=\"#x\">link</a> <span style=\"background: yellow\">marked</span>").Append(i % 7 == 3 ? "</p></div>" : "</p>");
        }

        html.Append("<ul><li>one</li><li style=\"opacity: 0.5\">two</li></ul><div style=\"position: relative; z-index: 2\">layered</div>");
        html.Append("<table style=\"border-collapse: collapse\">");
        for (int r = 0; r < 30; r++) html.Append("<tr><td style=\"border: 1px solid\">row ").Append(r).Append("</td><td>x</td></tr>");
        html.Append("</table><div style=\"transform: rotate(5deg)\">turned</div></body>");
        BoxFragment root = HtmlLayoutFixture.Layout(html.ToString(), width: 500);
        var index = new PaintIndex(root);

        string Paint(double top, double bottom, PaintIndex? with)
        {
            PdfDocument doc = PdfDocument.Create();
            PdfPage page = doc.Pages.Add(PdfRectangle.A4);
            using (PdfGraphics g = page.CreateGraphics())
            {
                new Painter(g, top, bottom, _ => (page, 0), null, index: with).Paint(root);
            }

            PdfObject contents = doc.Resolve(page.Dictionary["Contents"])!;
            return Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)contents));
        }

        Assert.IsGreaterThan(1500, root.Height);
        for (double top = -100; top < root.Height + 200; top += 211)
        {
            Assert.AreEqual(Paint(top, top + 300, null), Paint(top, top + 300, index), $"slice at {top}");
        }
    }

    [TestMethod]
    public void InstalledFallbacksAreFoundOnlyWhenACharacterNeedsThem()
    {
        PdfOpenTypeFont arabic = Arabic();
        int asked = 0;
        var set = new FontSet(PdfStandardFont.Helvetica, [PdfStandardFont.TimesRoman], () =>
        {
            asked++;
            return [PdfStandardFont.TimesRoman, arabic, arabic];
        });

        Assert.AreSame(PdfStandardFont.Helvetica, set.For("abc", 1));
        Assert.AreEqual(0, asked, "the named fonts cover Latin text");
        Assert.AreSame(arabic, set.For("ب", 0));
        Assert.AreSame(arabic, set.For("ببب", 2));
        Assert.AreEqual(1, asked, "the installed fonts are found once");
        Assert.AreSame(PdfStandardFont.Helvetica, set.For("一", 0), "nothing covers it: the primary font");
    }

    [TestMethod]
    public void LookupDigestsNeverMissAGlyph()
    {
        var random = new Random(5);
        for (int trial = 0; trial < 200; trial++)
        {
            var digest = new GlyphDigest();
            var added = new List<int>();
            for (int k = 0; k < 1 + random.Next(6); k++)
            {
                int first = random.Next(0x10000), last = Math.Min(0xFFFF, first + (random.Next(3) == 0 ? random.Next(5000) : random.Next(4)));
                digest.Add(first, last);
                added.AddRange(Enumerable.Range(first, Math.Min(last - first + 1, 300)));
                added.Add(last);
            }

            foreach (int glyph in added) Assert.IsTrue(digest.MayContain(glyph), $"glyph {glyph}");
        }

        // A digest of a few glyphs turns most others away, which is what makes it worth having.
        var few = new GlyphDigest();
        foreach (int glyph in new[] { 36, 68, 81, 1000 }) few.Add(glyph, glyph);
        Assert.IsGreaterThan(50000, Enumerable.Range(0, 0x10000).Count(g => !few.MayContain(g)));
    }

    [TestMethod]
    public void TextBelowHebrewIsLeftToRight()
    {
        // Inline layout skips the bidi algorithm for such text in a left to right or automatic
        // paragraph; the algorithm gives every character level 0 there.
        var random = new Random(9);
        const string extra = "0123456789 ,.-+$%()[]!?\t̀ #/:\n\u0085\u001C";
        for (int trial = 0; trial < 20000; trial++)
        {
            int[] cps = [.. Enumerable.Range(0, 1 + random.Next(30)).Select(_ => random.Next(4) == 0 ? extra[random.Next(extra.Length)] : random.Next(0x590))];
            foreach (int direction in new[] { 0, 2 })
            {
                Assert.IsTrue(Bidi.Analyze(cps, direction).Levels.All(l => l == 0), string.Join(" ", cps.Select(c => c.ToString("X4", System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
    }

    [TestMethod]
    public void SavingWithoutTheProofWritesTheSameBytes()
    {
        string html = "<h1>Report</h1>" + string.Concat(Enumerable.Range(1, 60).Select(i => $"<p>Paragraph {i} with <b>bold</b> text.</p>"));
        PdfDocument doc = HtmlConverter.ConvertToDocument(html, new HtmlConverterOptions { UseSystemFonts = false });
        byte[] proven = doc.ToArray();
        byte[] unproven = doc.ToArray(new PdfSaveOptions { VerifyOptimization = false });
        CollectionAssert.AreEqual(proven, unproven);
    }

    [TestMethod]
    public void ConvertToPdfSavesWithTheGivenOptions()
    {
        const string html = "<p>Hello</p>";
        string plain = Encoding.ASCII.GetString(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        StringAssert.Contains(plain, "/ObjStm");
        var options = new HtmlConverterOptions { UseSystemFonts = false, SaveOptions = new PdfSaveOptions { UseObjectStreams = false } };
        Assert.DoesNotContain("/ObjStm", Encoding.ASCII.GetString(HtmlConverter.ConvertToPdf(html, options)));
        using var stream = new MemoryStream();
        HtmlConverter.ConvertToPdf(html, stream, options);
        Assert.DoesNotContain("/ObjStm", Encoding.ASCII.GetString(stream.ToArray()));
    }

    [TestMethod]
    public void ConvertingAFileKeepsThePageLimit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfnetstack-html-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "two.html");
            File.WriteAllText(file, "<p>one</p><p style=\"break-before: page\">two</p>");
            Assert.HasCount(2, HtmlConverter.ConvertFileToDocument(file, new HtmlConverterOptions { UseSystemFonts = false }).Pages);
            Assert.ThrowsExactly<HtmlConversionException>(() => HtmlConverter.ConvertFileToDocument(file, new HtmlConverterOptions { UseSystemFonts = false, MaxPages = 1 }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
