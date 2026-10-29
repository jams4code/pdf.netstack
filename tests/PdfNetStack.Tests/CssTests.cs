using System.Diagnostics;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class CssTests
{
    private static string Kinds(string css) =>
        string.Join(" ", CssTokenizer.Tokenize(css).Select(t => t.Type == CssTokenType.Whitespace ? "_" : t.Type + ":" + t));

    [TestMethod]
    public void TokenizesPerCssSyntax()
    {
        Assert.AreEqual("Ident:a _ Delim:> _ Ident:b", Kinds("a > b"));
        Assert.AreEqual("Dimension:12px Percentage:50% Number:-0.5 Number:100000", Kinds("12px 50% -.5 1e5").Replace(" _", ""));
        Assert.AreEqual("Hash:#main Delim:. Ident:x-y", Kinds("#main .x-y").Replace(" _", ""));
        Assert.AreEqual("Url:url(a.png) Function:url( String:\"c.png\" RightParen:", Kinds("url( a.png ) url(\"c.png\")").Replace(" _", ""));
        Assert.AreEqual("AtKeyword:@media Ident:print", Kinds("@media/* comment */print").Replace(" _", ""));

        // Escapes: a hex escape eats one following space; a quote escapes itself.
        List<CssToken> tokens = CssTokenizer.Tokenize(".\\31 0 \"a\\\"b\" '\\41 x'");
        Assert.AreEqual("10", tokens[1].Value);
        Assert.AreEqual("a\"b", tokens[3].Value);
        Assert.AreEqual("Ax", tokens[5].Value);

        CssToken hash = CssTokenizer.Tokenize("#123")[0];
        Assert.IsFalse(hash.IsId);
        Assert.IsTrue(CssTokenizer.Tokenize("#a1")[0].IsId);
    }

    [TestMethod]
    public void ParsesRulesAndDeclarations()
    {
        List<CssRule> rules = CssParser.ParseStyleSheet("""
            @charset "utf-8";
            @import url(base.css) print;
            <!-- h1, .t { color: red !important; margin: 0 auto ; bogus } -->
            @media print and (min-width: 10cm) { p { orphans: 3 } }
            @page :first { margin: 2cm; @top-center { content: "Report" } size: A4 }
            @font-face { font-family: "Inter"; src: url(inter.woff) }
            @keyframes spin { from { x: 1 } }
            p { color: blue
            """);

        Assert.AreEqual("base.css", ((CssImportRule)rules[0]).Url);
        var style = (CssStyleRule)rules[1];
        CollectionAssert.AreEqual(new[] { "h1", ".t" }, style.Selectors.Select(s => s.Text).ToArray());
        Assert.AreEqual("color: red !important", style.Declarations[0].ToString());
        Assert.AreEqual("margin: 0 auto", style.Declarations[1].ToString());
        Assert.HasCount(2, style.Declarations);

        var media = (CssMediaRule)rules[2];
        Assert.AreEqual("orphans: 3", ((CssStyleRule)media.Rules.Single()).Declarations.Single().ToString());

        var page = (CssPageRule)rules[3];
        Assert.AreEqual("first", page.Pseudo);
        CollectionAssert.AreEqual(new[] { "margin: 2cm", "size: A4" }, page.Declarations.Select(d => d.ToString()).ToArray());
        Assert.AreEqual("content: \"Report\"", page.MarginBoxes["top-center"].Single().ToString());

        Assert.AreEqual("font-family", ((CssFontFaceRule)rules[4]).Declarations[0].Property);

        // @keyframes is dropped; an unclosed block at end of file still yields its rule.
        Assert.AreEqual("color: blue", ((CssStyleRule)rules[5]).Declarations.Single().ToString());
        Assert.HasCount(6, rules);
    }

    [TestMethod]
    public void InvalidSelectorDropsTheWholeRule()
    {
        List<CssRule> rules = CssParser.ParseStyleSheet("p, a:unknown { color: red } #1x { } p..q { } div { color: green }");
        Assert.AreEqual("div", ((CssStyleRule)rules.Single()).Selectors.Single().Text);
        Assert.AreEqual("--Brand", CssParser.ParseDeclarations("--Brand: #fff; COLOR: red").First().Property);
        Assert.AreEqual("color", CssParser.ParseDeclarations("--Brand: #fff; COLOR: red").Last().Property);
    }

    private const string Page = """
        <html lang="en-GB"><body>
        <div id="main" class="box wide" data-kind="note-x">
          <h1>Title</h1>
          <p class="lead">One</p>
          <p>Two</p>
          <span>Three</span>
          <p dir="rtl">Four</p>
          <a href="#x" type="TEXT/html">link</a>
        </div>
        <ul><li>a<li>b<li>c<li>d<li>e</ul>
        <p></p>
        </body></html>
        """;

    private static readonly HtmlDocument Doc = HtmlParser.Parse(Page);

    /// <summary>The elements matched by a selector, as name or name#index for repeats.</summary>
    private static string Select(string selector)
    {
        List<CssSelector> list = CssSelector.Parse(selector) ?? throw new AssertFailedException("invalid selector " + selector);
        return string.Join(" ", Doc.Descendants()
            .Where(e => list.Any(s => s.Matches(e)))
            .Select(e => e.Name + (e.ParentElement is null ? "" : (e.ElementIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [TestMethod]
    [DataRow("p", "p2 p3 p5 p3")]
    [DataRow("div > p", "p2 p3 p5")]
    [DataRow("body p", "p2 p3 p5 p3")]
    [DataRow("h1 + p", "p2")]
    [DataRow("h1 ~ p", "p2 p3 p5")]
    [DataRow("p + span", "span4")]
    [DataRow(".box.wide > .lead", "p2")]
    [DataRow("#main a", "a6")]
    [DataRow("*:root", "html")]
    [DataRow("html > body > ul > li:first-child", "li1")]
    [DataRow("li:last-child, h1:only-of-type", "h11 li5")]
    [DataRow("li:nth-child(2n+1)", "li1 li3 li5")]
    [DataRow("li:nth-child(even)", "li2 li4")]
    [DataRow("li:nth-child(-n+2)", "li1 li2")]
    [DataRow("li:nth-last-child(2)", "li4")]
    [DataRow("li:nth-child( 3n - 1 )", "li2 li5")]
    [DataRow("div > :nth-of-type(2)", "p3")]
    [DataRow("div > p:nth-last-of-type(1)", "p5")]
    [DataRow("div > :nth-child(odd of p)", "p2 p5")]
    [DataRow("p:empty", "p3")]
    [DataRow("div > p:not(.lead)", "p3 p5")]
    [DataRow("div > :is(h1, span)", "h11 span4")]
    [DataRow("div > :where(p.lead)", "p2")]
    [DataRow("[data-kind]", "div1")]
    [DataRow("[data-kind=note-x]", "div1")]
    [DataRow("[data-kind|=note]", "div1")]
    [DataRow("[data-kind^=no][data-kind$=\"-x\"][data-kind*=te]", "div1")]
    [DataRow("[class~=wide]", "div1")]
    [DataRow("[type=\"text/html\"]", "a6")]
    [DataRow("[data-kind=NOTE-X i]", "div1")]
    [DataRow("a:link", "a6")]
    [DataRow("a:hover", "")]
    [DataRow("h1:lang(en)", "h11")]
    [DataRow("p:dir(rtl)", "p5")]
    [DataRow("*|p.lead", "p2")]
    public void MatchesSelectors(string selector, string expected) => Assert.AreEqual(expected, Select(selector));

    [TestMethod]
    [DataRow("p", 0, 0, 1)]
    [DataRow("#a .b > p::before", 1, 1, 2)]
    [DataRow("a[href]:hover", 0, 2, 1)]
    [DataRow(":not(#a, .b)", 1, 0, 0)]
    [DataRow(":where(#a) p", 0, 0, 1)]
    [DataRow("li:nth-child(2 of .x)", 0, 2, 1)]
    [DataRow("*", 0, 0, 0)]
    public void ComputesSpecificity(string selector, int a, int b, int c) =>
        Assert.AreEqual(new CssSpecificity(a, b, c), CssSelector.Parse(selector)!.Single().Specificity);

    [TestMethod]
    public void ParsesPseudoElements()
    {
        Assert.AreEqual("before", CssSelector.Parse("p::before")!.Single().PseudoElement);
        Assert.AreEqual("after", CssSelector.Parse("p:after")!.Single().PseudoElement);
        Assert.IsNull(CssSelector.Parse("p::before span"));
        Assert.IsNull(CssSelector.Parse("p::nonsense"));
        Assert.IsNull(CssSelector.Parse("p:nth-child(2.5)"));
        Assert.IsNull(CssSelector.Parse("p:nth-of-type(2 of p)"));
        Assert.IsNull(CssSelector.Parse("p >"));
        // :is() forgives an invalid argument and matches nothing for it.
        Assert.AreEqual("", Select("p:is(:bogus)"));
        Assert.AreEqual("p2", Select("div > p:is(:bogus, .lead)"));
    }

    [TestMethod]
    public void HostileSelectorsStayBounded()
    {
        // Deep nesting and long compound chains are rejected instead of recursing without limit.
        Assert.IsNull(CssSelector.Parse(string.Concat(Enumerable.Repeat(":not(", 20)) + "p" + new string(')', 20)));
        Assert.IsNull(CssSelector.Parse(string.Join(" ", Enumerable.Repeat("div", 1000))));
        Assert.IsEmpty(CssParser.ParseStyleSheet(new string('{', 100_000)));

        // A descendant chain against a deep tree fails fast instead of trying every ancestor combination.
        HtmlDocument deep = HtmlParser.Parse(string.Concat(Enumerable.Repeat("<div>", 2000)) + "<span>x");
        CssSelector chain = CssSelector.Parse(string.Join(" ", Enumerable.Repeat("div", 100)) + " p span")!.Single();
        HtmlElement span = deep.Descendants().Single(e => e.Name == "span");
        var clock = Stopwatch.StartNew();
        Assert.IsFalse(chain.Matches(span));
        Assert.IsLessThan(5_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    public void ParsesAnPlusB()
    {
        static (int, int)? Ab(string s) =>
            NthCondition.TryParseFormula(CssParser.ComponentValues(CssTokenizer.Tokenize(s)), out int a, out int b) ? (a, b) : null;

        Assert.AreEqual((2, 1), Ab("odd"));
        Assert.AreEqual((2, 0), Ab("EVEN"));
        Assert.AreEqual((0, 7), Ab("7"));
        Assert.AreEqual((0, -3), Ab("-3"));
        Assert.AreEqual((1, 0), Ab("n"));
        Assert.AreEqual((1, 2), Ab("+n+2"));
        Assert.AreEqual((-1, 3), Ab("-n+3"));
        Assert.AreEqual((3, -1), Ab("3n-1"));
        Assert.AreEqual((3, -1), Ab("3n - 1"));
        Assert.AreEqual((3, 1), Ab("3n +1"));
        Assert.AreEqual((-2, -1), Ab("-2n-1"));
        Assert.AreEqual((1, -1), Ab("n- 1"));
        Assert.IsNull(Ab("2.5n"));
        Assert.IsNull(Ab("3n+"));
        Assert.IsNull(Ab("foo"));
    }
}
