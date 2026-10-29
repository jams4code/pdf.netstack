using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Html;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class HtmlConverterTests
{
    private static readonly HtmlConverterOptions Standard = new() { UseSystemFonts = false };

    private static PdfDocument Convert(string html, HtmlConverterOptions? options = null) =>
        PdfDocument.Open(HtmlConverter.ConvertToPdf(html, options ?? Standard));

    private static string Content(PdfDocument doc, int page)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.GetEncoding("ISO-8859-1").GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    /// <summary>The text of a page's string operands joined, ignoring kerning numbers between them.</summary>
    private static string Text(PdfDocument doc, int page) =>
        string.Concat(Regex.Matches(Content(doc, page), @"\(((?:\\.|[^\\)])*)\)").Cast<Match>().Select(m => Regex.Unescape(m.Groups[1].Value)));

    [TestMethod]
    public void ConvertsADocument()
    {
        PdfDocument doc = Convert("""
            <html lang=fr><head><title>Report</title><meta name=author content="Jamal"></head>
            <body><h1>Hello</h1><p>World of <b>bold</b> text</p></body></html>
            """);
        Assert.HasCount(1, doc.Pages);
        Assert.AreEqual(595.28, doc.Pages[0].Width, 0.01);
        Assert.AreEqual(841.89, doc.Pages[0].Height, 0.01);
        Assert.AreEqual("Report", doc.Info.Title);
        Assert.AreEqual("Jamal", doc.Info.Author);
        Assert.AreEqual("fr", doc.Language);
        Assert.AreEqual("Hello", doc.Outlines.Single().Title);
        string text = Text(doc, 0);
        StringAssert.Contains(text, "Hello");
        StringAssert.Contains(text, "World of");
        StringAssert.Contains(text, "bold");
    }

    [TestMethod]
    public void BreaksPages()
    {
        string paragraphs = string.Concat(Enumerable.Range(1, 120).Select(i => $"<p>Paragraph {i}</p>"));
        PdfDocument many = Convert(paragraphs);
        Assert.IsGreaterThan(2, many.Pages.Count);
        StringAssert.Contains(Text(many, 0), "Paragraph 1");
        StringAssert.Contains(Text(many, many.Pages.Count - 1), "Paragraph 120");

        // No paragraph is drawn twice: every one appears on exactly one page.
        string all = string.Join("|", Enumerable.Range(0, many.Pages.Count).Select(p => Text(many, p)));
        for (int i = 1; i <= 120; i++)
        {
            Assert.HasCount(1, Regex.Matches(all, $"Paragraph {i}(?![0-9])"), $"paragraph {i}");
        }

        PdfDocument forced = Convert("<p>one</p><p style=\"break-before: page\">two</p><p>three</p>");
        Assert.HasCount(2, forced.Pages);
        StringAssert.Contains(Text(forced, 1), "two");
        Assert.DoesNotContain("two", Text(forced, 0));
    }

    [TestMethod]
    public void RepeatsTableHeadersOnEveryPage()
    {
        string rows = string.Concat(Enumerable.Range(1, 90).Select(i => $"<tr><td>Row {i}</td><td>v</td></tr>"));
        PdfDocument doc = Convert($"<table><thead><tr><th>Heading</th><th>Value</th></tr></thead><tbody>{rows}</tbody><tfoot><tr><td>Total</td><td>0</td></tr></tfoot></table>");
        Assert.IsGreaterThan(1, doc.Pages.Count);
        for (int page = 0; page < doc.Pages.Count; page++)
        {
            string text = Text(doc, page);
            StringAssert.StartsWith(text, "HeadingValue", $"page {page + 1} starts with the header");
            Assert.HasCount(1, Regex.Matches(text, "Heading"));
        }

        // Every row is drawn once, and the footer once, at the end.
        string all = string.Join("|", Enumerable.Range(0, doc.Pages.Count).Select(p => Text(doc, p)));
        for (int i = 1; i <= 90; i++) Assert.HasCount(1, Regex.Matches(all, $"Row {i}(?![0-9])"), $"row {i}");
        StringAssert.Contains(Text(doc, doc.Pages.Count - 1), "Total");
    }

    [TestMethod]
    public void RepeatsFixedBoxesOnEveryPage()
    {
        PdfDocument doc = Convert("""
            <div style="position: fixed; top: 0; right: 0">Draft</div>
            <p>one</p><p style="break-before: page">two</p><p style="break-before: page">three</p>
            """);
        Assert.HasCount(3, doc.Pages);
        for (int page = 0; page < 3; page++) Assert.HasCount(1, Regex.Matches(Text(doc, page), "Draft"), $"page {page + 1}");
    }

    [TestMethod]
    public void AppliesPageRulesAndMarginBoxes()
    {
        PdfDocument doc = Convert("""
            <style>
              @page { size: letter landscape; margin: 1in; @bottom-center { content: "Page " counter(page) " of " counter(pages) } }
              @page :first { @top-right { content: "Confidential" } }
            </style>
            <p>first</p><p style="page-break-before: always">second</p>
            """);
        Assert.HasCount(2, doc.Pages);
        Assert.AreEqual(792, doc.Pages[0].Width, 0.01);
        Assert.AreEqual(612, doc.Pages[0].Height, 0.01);
        StringAssert.Contains(Text(doc, 0), "Page 1 of 2");
        StringAssert.Contains(Text(doc, 0), "Confidential");
        StringAssert.Contains(Text(doc, 1), "Page 2 of 2");
        Assert.DoesNotContain("Confidential", Text(doc, 1));
    }

    [TestMethod]
    public void LaysOutNamedPagesAtTheirOwnSize()
    {
        // 25 words of Helvetica 16px are about 980px: two lines on A4 portrait (794px), one on landscape (1123px).
        string words = string.Join(" ", Enumerable.Repeat("word", 25));
        PdfDocument doc = Convert($$"""
            <style>
              @page { size: A4; margin: 0 }
              @page wide { size: A4 landscape; margin-top: 40px; @top-center { content: "Annex" } }
              body { margin: 0; font: 16px Helvetica }
              .wide { page: wide }
            </style>
            <p>{{words}}</p>
            <section class=wide><p>{{words}}</p></section>
            <p id=back>back</p><p><a href="#back">link</a></p>
            """);
        Assert.HasCount(3, doc.Pages);
        Assert.AreEqual(595.28, doc.Pages[0].Width, 0.01);
        Assert.AreEqual(841.89, doc.Pages[1].Width, 0.01, "the named page is landscape");
        Assert.AreEqual(595.28, doc.Pages[2].Width, 0.01, "auto returns to the unnamed page");

        int Lines(int page) => Regex.Matches(Content(doc, page), @"\bBT\b").Cast<Match>().Count();
        Assert.AreEqual(2, Lines(0));
        Assert.AreEqual(2, Lines(1), "one line of text and the margin box");
        StringAssert.Contains(Text(doc, 1), "Annex");
        Assert.DoesNotContain("Annex", Text(doc, 0) + Text(doc, 2));

        // Internal links find their pages across the runs.
        Assert.AreEqual(2, doc.Pages[2].Annotations.Single().Destination!.Index);
    }

    [TestMethod]
    public void SetsNamedStringsForRunningHeaders()
    {
        PdfDocument doc = Convert("""
            <style>
              @page { @top-left { content: string(chapter) } @top-right { content: string(chapter, last) } @bottom-center { content: string(chapter, first-except) } }
              h1 { string-set: chapter content(text) }
              h2 { string-set: chapter "Part: " attr(title) }
            </style>
            <h1>Intro  text</h1><p>a</p>
            <h1 style="break-before: page">Second</h1><h2 title="Two B">x</h2>
            <p style="break-before: page">no heading here</p>
            """);
        Assert.HasCount(3, doc.Pages);
        StringAssert.Contains(Text(doc, 0), "Intro text", "content(text) collapses white space");
        Assert.DoesNotContain("Part", Text(doc, 0));

        // Page two sets it twice: first takes the first, last takes the last.
        string two = Text(doc, 1);
        StringAssert.Contains(two, "Second");
        StringAssert.Contains(two, "Part: Two B");

        // Page three sets nothing: the value carries over, and first-except shows it.
        Assert.HasCount(3, Regex.Matches(Text(doc, 2), "Part: Two B"));
    }

    [TestMethod]
    public void AddsLinksAndNestedBookmarks()
    {
        PdfDocument doc = Convert("""
            <h1>Intro</h1><p><a href="https://example.com/">site</a> and <a href="#part">below</a></p>
            <h2 id=part style="break-before: page">Part</h2><h3>Detail</h3><h1>End</h1>
            """);
        Assert.HasCount(2, doc.Pages);
        PdfArray annotations = (PdfArray)doc.Resolve(doc.Pages[0].Dictionary["Annots"])!;
        Assert.HasCount(2, annotations);

        Assert.HasCount(2, doc.Outlines);
        Assert.AreEqual("Intro", doc.Outlines[0].Title);
        Assert.AreEqual("Part", doc.Outlines[0].Children.Single().Title);
        Assert.AreEqual("Detail", doc.Outlines[0].Children.Single().Children.Single().Title);
        Assert.AreEqual(1, doc.Outlines[0].Children.Single().Destination!.Index);
    }

    [TestMethod]
    public void LoadsResourcesOnlyFromTheBaseFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "pdfnetstack-html-" + Guid.NewGuid().ToString("N"));
        string site = Path.Combine(root, "site");
        Directory.CreateDirectory(site);
        try
        {
            File.WriteAllText(Path.Combine(site, "style.css"), "p { color: rgb(255, 0, 0) }");
            File.WriteAllText(Path.Combine(root, "secret.css"), "p { color: rgb(0, 0, 255) }");
            var options = new HtmlConverterOptions { UseSystemFonts = false, BaseUri = new Uri(Path.Combine(site, "page.html")) };

            PdfDocument inside = Convert("<link rel=stylesheet href=style.css><p>x</p>", options);
            StringAssert.Contains(Content(inside, 0), "1 0 0 rg");

            PdfDocument outside = Convert("<link rel=stylesheet href=../secret.css><p>x</p>", options);
            Assert.DoesNotContain("0 0 1 rg", Content(outside, 0));

            // Without a base folder or loader, file URLs do not load at all.
            PdfDocument absolute = Convert($"<link rel=stylesheet href=\"{new Uri(Path.Combine(site, "style.css"))}\"><p>x</p>");
            Assert.DoesNotContain("1 0 0 rg", Content(absolute, 0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LoadsDataUrisAndCustomLoaders()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "rgb8.png"));
        string dataUri = "data:image/png;base64," + System.Convert.ToBase64String(png);
        PdfDocument image = Convert($"<p><img src=\"{dataUri}\" style=\"width: 20px\"></p>");
        StringAssert.Contains(Content(image, 0), " Do");

        var requested = new List<Uri>();
        var options = new HtmlConverterOptions
        {
            UseSystemFonts = false,
            BaseUri = new Uri("https://example.com/docs/"),
            ResourceLoader = uri =>
            {
                requested.Add(uri);
                return uri.AbsolutePath.EndsWith(".css", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("p { color: rgb(0, 255, 0) }") : null;
            },
        };
        PdfDocument loaded = Convert("<link rel=stylesheet href=theme.css><p>x<img src=missing.png alt=alt></p>", options);
        StringAssert.Contains(Content(loaded, 0), "0 1 0 rg");
        CollectionAssert.AreEqual(new[] { "https://example.com/docs/theme.css", "https://example.com/docs/missing.png" }, requested.Select(u => u.AbsoluteUri).ToArray());
    }

    [TestMethod]
    public void PaintsBackgroundsBordersAndDecorations()
    {
        PdfDocument doc = Convert("""
            <body style="background: #00ff00">
            <div style="background-color: rgb(255, 0, 0); border: 2px dashed rgb(0, 0, 255)">box</div>
            <p><u>under</u> <s>struck</s></p>
            """);
        string content = Content(doc, 0);
        StringAssert.Contains(content, "0 1 0 rg", "the body background covers the page");
        StringAssert.Contains(content, "1 0 0 rg");
        StringAssert.Contains(content, "0 0 1 RG");
        StringAssert.Contains(content, "] 0 d", "dashed borders set a dash pattern");
        StringAssert.Contains(Text(doc, 0), "under");
    }

    [TestMethod]
    public void ReportsLoaderFailuresToTheCaller()
    {
        // The conversion runs on its own thread; exceptions still reach the caller unchanged.
        var options = new HtmlConverterOptions
        {
            UseSystemFonts = false,
            BaseUri = new Uri("https://example.com/"),
            ResourceLoader = _ => throw new InvalidOperationException("blocked"),
        };
        InvalidOperationException e = Assert.ThrowsExactly<InvalidOperationException>(() => HtmlConverter.ConvertToPdf("<img src=a.png>", options));
        Assert.AreEqual("blocked", e.Message);
        Assert.ThrowsExactly<ArgumentNullException>(() => HtmlConverter.ConvertToPdf(null!));
    }

    [TestMethod]
    public void SurvivesHostileMarkup()
    {
        PdfDocument deep = Convert(string.Concat(Enumerable.Repeat("<div style=\"padding-left: 1px\">", 20_000)) + "deep");
        Assert.IsGreaterThanOrEqualTo(1, deep.Pages.Count);

        PdfDocument wide = Convert("<p>" + new string('x', 20_000) + "</p>");
        Assert.HasCount(1, wide.Pages);

        PdfDocument tall = Convert("<div style=\"height: 5000px; background: red\"></div>");
        Assert.IsGreaterThan(4, tall.Pages.Count);
    }
}
