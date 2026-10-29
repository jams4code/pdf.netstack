using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;
using static PdfNetStack.Tests.HtmlLayoutFixture;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class LayoutTests
{
    [TestMethod]
    public void CollapsesVerticalMargins()
    {
        BoxFragment root = Layout("""
            <body style="margin: 8px">
              <p id=a style="margin: 10px 0">x</p>
              <p id=b style="margin: 20px 0">y</p>
              <div id=e style="margin: 30px 0"></div>
              <p id=c style="margin: 5px 0">z</p>
              <div id=d style="border-top: 1px solid; margin-top: 0"><p id=inner style="margin-top: 7px">w</p></div>
            </body>
            """);
        double line = NormalLineHeight(PdfStandardFont.TimesRoman, 16);

        BoxFragment a = ById(root, "a"), b = ById(root, "b"), c = ById(root, "c"), d = ById(root, "d"), inner = ById(root, "inner");
        Assert.AreEqual(10, a.Y, Tolerance, "body's 8px and p's 10px collapse to 10px");
        Assert.AreEqual(8, a.X, Tolerance);
        Assert.AreEqual(584, a.Width, Tolerance);
        Assert.AreEqual(line, a.Height, Tolerance);
        Assert.AreEqual(a.Bottom + 20, b.Y, Tolerance, "sibling margins collapse to the larger");
        Assert.AreEqual(b.Bottom + 30, c.Y, Tolerance, "an empty block's margins collapse through it");
        Assert.AreEqual(c.Bottom + 5 + 1 + 7, inner.Y, Tolerance, "a border keeps the child's margin inside");
        Assert.AreEqual(d.Y + 1 + 7, inner.Y, Tolerance);
    }

    [TestMethod]
    public void ResolvesWidthsAndAutoMargins()
    {
        BoxFragment root = Layout("""
            <div id=c style="width: 200px; margin: 0 auto; padding: 10px; border: 5px solid">x</div>
            <div id=bb style="width: 200px; box-sizing: border-box; padding: 10px; border: 5px solid">x</div>
            <div id=max style="max-width: 100px; margin-left: 50px">x</div>
            <div id=pct style="width: 50%; margin-left: auto">x</div>
            """);

        BoxFragment c = ById(root, "c");
        Assert.AreEqual(230, c.Width, Tolerance);
        Assert.AreEqual(8 + ((584 - 230) / 2.0), c.X, Tolerance);
        Assert.AreEqual(200, c.ContentWidth, Tolerance);
        Assert.AreEqual(200, ById(root, "bb").Width, Tolerance);
        Assert.AreEqual(170, ById(root, "bb").ContentWidth, Tolerance);
        Assert.AreEqual(100, ById(root, "max").Width, Tolerance);
        Assert.AreEqual(58, ById(root, "max").X, Tolerance);
        Assert.AreEqual(292, ById(root, "pct").Width, Tolerance);
        Assert.AreEqual(8 + 292, ById(root, "pct").X, Tolerance, "auto left margin pushes it right");
    }

    [TestMethod]
    public void BreaksAndAlignsLines()
    {
        PdfFont helvetica = PdfStandardFont.Helvetica;
        double word = Measure(helvetica, "xxxx", 20), space = Measure(helvetica, " ", 20);
        double width = (word * 2) + space + 1; // two words fit, three do not
        string css = $"p {{ font: 20px sans-serif; width: {width}px; margin: 0 }} body {{ margin: 0 }}";

        BoxFragment root = Layout("<p id=p>xxxx xxxx xxxx xxxx xxxx</p>", css);
        List<LineFragment> lines = Lines(ById(root, "p"));
        Assert.HasCount(3, lines);
        Assert.AreEqual(NormalLineHeight(helvetica, 20), lines[0].Height, Tolerance);
        Assert.AreEqual(lines[0].Bottom, lines[1].Y, Tolerance);

        TextFragment first = lines[0].Items.OfType<TextFragment>().Single();
        Assert.AreEqual("xxxx xxxx", first.Text.TrimEnd());
        Assert.AreEqual(0, first.X, Tolerance);

        BoxFragment centred = ById(Layout("<p id=p style=\"text-align: center\">xxxx</p>", css), "p");
        Assert.AreEqual((width - word) / 2, Lines(centred)[0].Items.OfType<TextFragment>().Single().X, Tolerance);
        BoxFragment right = ById(Layout("<p id=p style=\"text-align: right\">xxxx</p>", css), "p");
        Assert.AreEqual(width - word, Lines(right)[0].Items.OfType<TextFragment>().Single().X, Tolerance);

        // Justified lines fill the width, except the last.
        BoxFragment justified = ById(Layout("<p id=p style=\"text-align: justify\">xxxx xxxx xxxx</p>", css), "p");
        TextFragment full = Lines(justified)[0].Items.OfType<TextFragment>().Single();
        Assert.AreEqual(1, full.JustifySpace, 1e-6, "the one spare pixel goes to the one space");
        Assert.AreEqual(0, Lines(justified)[1].Items.OfType<TextFragment>().Single().JustifySpace);

        // A forced break, and a word longer than the line overflows rather than splitting.
        Assert.HasCount(2, Lines(ById(Layout("<p id=p>a<br>b</p>", css), "p")));
        Assert.HasCount(1, Lines(ById(Layout("<p id=p>xxxxxxxxxxxxxxxxxxxx</p>", css), "p")));
        // x is 10px at 20px Helvetica: 8 fit in the 86.6px line, so 20 take 3 lines.
        Assert.HasCount(3, Lines(ById(Layout("<p id=p style=\"overflow-wrap: anywhere\">xxxxxxxxxxxxxxxxxxxx</p>", css), "p")));
    }

    [TestMethod]
    public void PlacesInlineBoxEdges()
    {
        PdfFont times = PdfStandardFont.TimesRoman;
        BoxFragment root = Layout("<p id=p>a<span style=\"padding: 0 10px; border: 2px solid; margin: 0 3px\">b</span>c</p>", "body { margin: 0 } p { margin: 0 }");
        LineFragment line = Lines(ById(root, "p")).Single();
        List<TextFragment> texts = line.Items.OfType<TextFragment>().ToList();
        double a = Measure(times, "a", 16), b = Measure(times, "b", 16);
        Assert.AreEqual(0, texts[0].X, Tolerance);
        Assert.AreEqual(a + 3 + 2 + 10, texts[1].X, Tolerance);
        Assert.AreEqual(a + 3 + 2 + 10 + b + 10 + 2 + 3, texts[2].X, Tolerance);

        InlineFragment span = line.Items.OfType<InlineFragment>().Single();
        Assert.AreEqual(a + 3, span.X, Tolerance, "the background starts at the border edge, inside the margin");
        Assert.AreEqual(b + 24, span.Width, Tolerance);
        Assert.IsTrue(span.StartEdge && span.EndEdge);
        Assert.AreEqual(times.Ascent * 16 + 2, line.Baseline - span.Y, Tolerance, "content area plus the border above the baseline");
    }

    [TestMethod]
    public void SetsLineHeightAndVerticalAlign()
    {
        PdfFont times = PdfStandardFont.TimesRoman;
        BoxFragment root = Layout("""
            <p id=fixed style="line-height: 30px">x</p>
            <p id=ratio style="line-height: 2">x</p>
            <p id=img>x<img src=img.png style="width: 50px; height: 40px"></p>
            <p id=mid>x<img src=img.png style="width: 50px; height: 40px; vertical-align: middle"></p>
            """, "body { margin: 0 } p { margin: 0 }");

        Assert.AreEqual(30, Lines(ById(root, "fixed")).Single().Height, Tolerance);
        Assert.AreEqual(32, Lines(ById(root, "ratio")).Single().Height, Tolerance);

        // An image sits on the baseline with its bottom edge: the line grows above.
        LineFragment imageLine = Lines(ById(root, "img")).Single();
        BoxFragment image = imageLine.Items.OfType<BoxFragment>().Single();
        Assert.AreEqual(50, image.Width, Tolerance);
        Assert.AreEqual(imageLine.Baseline, image.Bottom, Tolerance);
        double half = (NormalLineHeight(times, 16) - ((times.Ascent + times.Descent) * 16)) / 2;
        Assert.AreEqual(40 + (times.Descent * 16) + half, imageLine.Height, Tolerance);

        // middle: the image's midpoint at the baseline plus half the x-height.
        LineFragment midLine = Lines(ById(root, "mid")).Single();
        BoxFragment mid = midLine.Items.OfType<BoxFragment>().Single();
        Assert.AreEqual(midLine.Baseline - (times.XHeight * 16 / 2), mid.Y + 20, Tolerance);
    }

    [TestMethod]
    public void ShrinksInlineBlocksAndPlacesMarkers()
    {
        PdfFont times = PdfStandardFont.TimesRoman;
        BoxFragment root = Layout("""
            <p><span id=ib style="display: inline-block; border: 1px solid; padding: 4px">hello</span></p>
            <ul><li id=li>item</li></ul>
            <div dir=rtl><p id=rtl>abc</p></div>
            """, "body { margin: 0 } p { margin: 0 }");

        BoxFragment ib = ById(root, "ib");
        Assert.AreEqual(Measure(times, "hello", 16) + 10, ib.Width, Tolerance);

        BoxFragment li = ById(root, "li");
        LineFragment marker = li.Marker!;
        Assert.AreEqual(li.ContentX, marker.X + marker.Width, Tolerance, "an outside marker ends at the content edge");
        Assert.AreEqual(Measure(times, (char)0x2022 + " ", 16), marker.Width, Tolerance, "the space after the bullet is kept (::marker is white-space: pre)");
        Assert.AreEqual(Lines(li)[0].Baseline, marker.Baseline, Tolerance);
        Assert.AreEqual(40, li.ContentX, Tolerance);

        BoxFragment rtl = ById(root, "rtl");
        TextFragment text = Lines(rtl)[0].Items.OfType<TextFragment>().Single();
        Assert.AreEqual(rtl.ContentX + rtl.ContentWidth, text.X + text.Width, Tolerance, "right to left text starts at the right");
    }

    [TestMethod]
    public void DrawsRightToLeftRunsInVisualOrder()
    {
        // The fixture font has Arabic joining forms; the HTML layout must match the core shaper's visual order.
        var fonts = new PdfFontCollection();
        fonts.AddFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Arabic.ttf"));
        PdfOpenTypeFont arabic = fonts.Families.Select(f => fonts.Find(f)!).Single();
        string word = new([(char)0x0628, (char)0x0645, (char)0x0645]); // beh meem meem: initial, medial, final forms
        Assert.IsTrue(word.All(c => arabic.HasGlyph(c)), "the fixture covers the test word");

        HtmlDocument doc = HtmlParser.Parse($"<p style=\"font-family: '{arabic.FamilyName}'\">{word}</p>");
        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddDocument(doc);
        BlockBox root = new BoxBuilder(resolver).Build(doc);
        BoxFragment layout = new LayoutEngine(new FontResolver(fonts, useSystemFonts: false)).Layout(root, 500);
        TextFragment text = All(layout).OfType<TextFragment>().Single();
        CollectionAssert.AreEqual(arabic.Shape(word).Select(g => g.Glyph).ToArray(), text.Glyphs.Select(g => g.Glyph).ToArray());
    }

    [TestMethod]
    public void MissingGlyphsDoNotStopLayout()
    {
        // No installed fonts: a character the standard fonts lack is shown as a question mark.
        BoxFragment root = Layout("<p id=p>a" + (char)0x6F22 + "b</p>");
        List<TextFragment> texts = Lines(ById(root, "p")).Single().Items.OfType<TextFragment>().ToList();
        Assert.AreEqual(3, texts.Sum(t => t.Glyphs.Count));
        Assert.AreEqual('?', (char)texts[1].Glyphs.Single().CodePoint, "the Han run is its own item, drawn as a question mark");
    }
}
