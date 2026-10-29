using System.Globalization;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class CascadeTests
{
    private static string Rgb(string css)
    {
        CssColor c = CssColor.Parse(css) ?? throw new AssertFailedException("invalid color " + css);
        return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3:0.##}", Math.Round(c.R * 255), Math.Round(c.G * 255), Math.Round(c.B * 255), c.A);
    }

    [TestMethod]
    public void ParsesColors()
    {
        Assert.AreEqual(148, CssColor.Named.Count(n => !n.Key.Contains("text") && n.Key is not ("canvas" or "buttonface" or "buttonborder" or "field" or "highlight" or "mark" or "accentcolor")));
        Assert.AreEqual("102 51 153 1", Rgb("RebeccaPurple"));
        Assert.AreEqual("255 0 0 1", Rgb("#f00"));
        Assert.AreEqual("255 0 0 0.53", Rgb("#f008"));
        Assert.AreEqual("18 52 86 1", Rgb("#123456"));
        Assert.AreEqual("18 52 86 0.47", Rgb("#12345678"));
        Assert.AreEqual("10 20 30 0.5", Rgb("rgba(10, 20, 30, 0.5)"));
        Assert.AreEqual("10 20 30 0.5", Rgb("rgb(10 20 30 / 50%)"));
        Assert.AreEqual("255 128 0 1", Rgb("rgb(100% 50% 0%)"));
        Assert.AreEqual("0 255 0 1", Rgb("hsl(120deg 100% 50%)"));
        Assert.AreEqual("0 0 255 0.25", Rgb("hsla(240, 100%, 50%, .25)"));
        Assert.AreEqual("128 128 128 1", Rgb("hwb(0 50% 50%)"));
        Assert.AreEqual("255 0 0 1", Rgb("hwb(0 0% 0%)"));
        Assert.IsTrue(CssColor.Parse("currentColor")!.Value.IsCurrentColor);
        Assert.AreEqual(0, CssColor.Parse("transparent")!.Value.A);
        Assert.IsNull(CssColor.Parse("rgb(1, 2 3)"));
        Assert.IsNull(CssColor.Parse("#12345"));
        Assert.IsNull(CssColor.Parse("notacolor"));
    }

    [TestMethod]
    [DataRow("lab(29.2345% 39.3825 20.0664)")]
    [DataRow("lch(29.2345% 44.2 27)")]
    [DataRow("oklab(40.101% 0.1147 0.0453)")]
    [DataRow("oklch(40.101% 0.12332 21.555)")]
    public void ConvertsCssColor4Spaces(string css)
    {
        // CSS Color 4 gives these as one color, rgb(49.06% 13.87% 15.9%), in each space.
        CssColor c = CssColor.Parse(css)!.Value;
        Assert.AreEqual(0.4906, c.R, 0.006);
        Assert.AreEqual(0.1387, c.G, 0.006);
        Assert.AreEqual(0.1590, c.B, 0.006);
    }

    [TestMethod]
    [DataRow("", true)]
    [DataRow("print", true)]
    [DataRow("all", true)]
    [DataRow("screen", false)]
    [DataRow("not screen", true)]
    [DataRow("screen, print", true)]
    [DataRow("only print and (orientation: portrait)", true)]
    [DataRow("print and (orientation: landscape)", false)]
    [DataRow("(min-width: 20cm)", true)]
    [DataRow("(max-width: 600px)", false)]
    [DataRow("(width >= 600px)", true)]
    [DataRow("(400px < width < 700px)", false)]
    [DataRow("(700px < width <= 800px)", true)]
    [DataRow("(color) and (not (monochrome))", true)]
    [DataRow("(hover: hover) or (min-aspect-ratio: 16/9)", false)]
    [DataRow("(prefers-color-scheme: dark)", false)]
    [DataRow("print and", false)]
    [DataRow("(bogus-feature: 1)", false)]
    [DataRow("not (bogus)", false)]
    public void EvaluatesMediaQueriesForPrint(string query, bool expected) =>
        Assert.AreEqual(expected, MediaQuery.Matches(query, CssMedia.Default));

    private static string Expand(string declaration)
    {
        List<CssDeclaration>? expanded = CssProperties.Expand(CssParser.ParseDeclarations(declaration).Single());
        return expanded is null ? "invalid" : string.Join("; ", expanded.Select(d => d.ToString()));
    }

    [TestMethod]
    public void ExpandsShorthands()
    {
        Assert.AreEqual("margin-top: 1px; margin-right: 2px; margin-bottom: 1px; margin-left: 2px", Expand("margin: 1px 2px"));
        Assert.AreEqual("padding-top: 1px; padding-right: 2px; padding-bottom: 3px; padding-left: 2px", Expand("padding: 1px 2px 3px"));
        Assert.AreEqual("invalid", Expand("margin: 1px 2px 3px 4px 5px"));
        Assert.AreEqual("invalid", Expand("padding: auto"));
        Assert.AreEqual("border-top-width: 2px; border-top-style: solid; border-top-color: red", Expand("border-top: red 2px solid"));
        Assert.AreEqual("border-left-width: medium; border-left-style: dashed; border-left-color: currentcolor", Expand("border-left: dashed"));
        StringAssert.Contains(Expand("border: 1px solid #ccc"), "border-bottom-color: #ccc");
        Assert.AreEqual("invalid", Expand("border: 1px 2px solid"));
        Assert.AreEqual(
            "border-top-left-radius: 1px 5px; border-top-right-radius: 2px 5px; border-bottom-right-radius: 3px 5px; border-bottom-left-radius: 2px 5px",
            Expand("border-radius: 1px 2px 3px / 5px"));
        Assert.AreEqual(
            "font-style: italic; font-variant-caps: normal; font-weight: 700; font-stretch: normal; font-size: 12px; line-height: 1.5; font-family: \"Inter\", sans-serif; font-variant-ligatures: normal; font-variant-numeric: normal; font-feature-settings: normal; font-kerning: auto",
            Expand("font: italic 700 12px/1.5 \"Inter\", sans-serif"));
        StringAssert.Contains(Expand("font: 0 serif"), "font-size: 0");
        Assert.AreEqual("invalid", Expand("font: bold"));
        Assert.AreEqual("list-style-type: none; list-style-position: inside; list-style-image: none", Expand("list-style: none inside"));
        Assert.AreEqual("list-style-type: square; list-style-position: outside; list-style-image: url(a.png)", Expand("list-style: square url(a.png)"));
        Assert.AreEqual("text-decoration-line: underline overline; text-decoration-style: wavy; text-decoration-color: red; text-decoration-thickness: auto", Expand("text-decoration: underline wavy red overline"));
        Assert.AreEqual("flex-grow: 0; flex-shrink: 0; flex-basis: auto", Expand("flex: none"));
        Assert.AreEqual("flex-grow: 2; flex-shrink: 1; flex-basis: 0%", Expand("flex: 2"));
        Assert.AreEqual("flex-grow: 1; flex-shrink: 3; flex-basis: 10px", Expand("flex: 10px 1 3"));
        Assert.AreEqual("invalid", Expand("flex: 1 10px 2"));
        Assert.AreEqual("break-before: page", Expand("page-break-before: always"));
        Assert.AreEqual("break-inside: avoid", Expand("page-break-inside: avoid"));
        Assert.AreEqual("overflow-wrap: anywhere", Expand("word-wrap: anywhere"));
        Assert.AreEqual("row-gap: 1px; column-gap: 1px", Expand("gap: 1px"));
        Assert.AreEqual("invalid", Expand("color: 12px"));
        Assert.AreEqual("invalid", Expand("-webkit-box-flex: 1"));
        Assert.AreEqual("margin-top: inherit; margin-right: inherit; margin-bottom: inherit; margin-left: inherit", Expand("margin: inherit"));
    }

    [TestMethod]
    public void ExpandsBackgroundLayers()
    {
        string expanded = Expand("background: url(a.png) no-repeat right 10px top / 50% auto padding-box, linear-gradient(red, blue) #eee");
        StringAssert.Contains(expanded, "background-color: #eee");
        StringAssert.Contains(expanded, "background-image: url(a.png), linear-gradient(red, blue)");
        StringAssert.Contains(expanded, "background-position: right 10px top, 0% 0%");
        StringAssert.Contains(expanded, "background-size: 50% auto, auto");
        StringAssert.Contains(expanded, "background-repeat: no-repeat, repeat");
        StringAssert.Contains(expanded, "background-origin: padding-box, padding-box");
        Assert.AreEqual("invalid", Expand("background: red, url(a.png)"));
    }

    /// <summary>Computes the styles of every element in document order, as the box builder will.</summary>
    private static Dictionary<HtmlElement, ComputedStyle> Styles(string html, string? css = null, CssMedia? media = null)
    {
        HtmlDocument doc = HtmlParser.Parse(html);
        var resolver = new StyleResolver(media ?? CssMedia.Default);
        if (css is not null) resolver.AddStyleSheet(css, CssOrigin.Author);
        resolver.AddDocument(doc);
        var styles = new Dictionary<HtmlElement, ComputedStyle>();
        foreach (HtmlElement e in doc.Descendants())
        {
            styles[e] = resolver.Compute(e, e.ParentElement is HtmlElement p ? styles[p] : null);
        }

        return styles;
    }

    private static ComputedStyle Of(Dictionary<HtmlElement, ComputedStyle> styles, string id) =>
        styles.First(p => p.Key.GetAttribute("id") == id).Value;

    private static string Value(ComputedStyle s, string property) => string.Concat(s[property].Select(v => v.ToString()));

    [TestMethod]
    public void CascadesByOriginSpecificityAndOrder()
    {
        var styles = Styles(
            """
            <style>
              p { color: red; margin-left: 1px }
              #a { color: green }
              p.x { color: blue; margin-left: 2px !important }
              p { color: red; color: bogus }
              .late { color: purple }
              p.x { color: orange }
            </style>
            <p id=a class=x>a</p>
            <p id=b class="x late" style="color: teal; margin-left: 9px">b</p>
            <p id=c style="color: navy !important">c</p>
            <p id=d class=late>d</p>
            """);

        Assert.AreEqual("green", Value(Of(styles, "a"), "color"));
        Assert.AreEqual("teal", Value(Of(styles, "b"), "color"));
        Assert.AreEqual("2px", Value(Of(styles, "b"), "margin-left"), "author !important beats a normal style attribute");
        Assert.AreEqual("navy", Value(Of(styles, "c"), "color"));
        Assert.AreEqual("purple", Value(Of(styles, "d"), "color"), "later rule of equal specificity wins; the invalid value is dropped");
    }

    [TestMethod]
    public void InheritsAndComputesValues()
    {
        var styles = Styles(
            """
            <style>
              html { font-size: 10px }
              body { color: rgb(1, 2, 3); line-height: 150%; border: 1px solid; font-weight: 300 }
              #h { font-size: 2em; margin-top: 1em; padding: 1rem }
              #s { font-size: larger; border: inherit; font-weight: bolder }
              #m { width: calc(100px + 2em); height: calc(50% - 10px); margin-left: 1in; line-height: 1.2 }
              #u { color: unset; margin-top: unset; font-size: initial }
            </style>
            <h1 id=h><span id=s>x</span></h1><div id=m></div><div id=u></div>
            """);

        ComputedStyle h = Of(styles, "h");
        Assert.AreEqual(20, h.FontSize);
        Assert.AreEqual("20px", Value(h, "margin-top"));
        Assert.AreEqual("10px", Value(h, "padding-left"));
        Assert.AreEqual("rgb(1, 2, 3)", Value(h, "color"));
        Assert.AreEqual("15px", Value(h, "line-height"), "percentages compute against the element's own size, then inherit as lengths");
        Assert.AreEqual("medium", Value(h, "border-top-width"), "borders do not inherit");
        Assert.AreEqual(700, h.Number("font-weight"), "the user agent makes headings bold");

        ComputedStyle s = Of(styles, "s");
        Assert.AreEqual(24, s.FontSize, 1e-9);
        Assert.AreEqual(900, s.Number("font-weight"));
        Assert.AreEqual("medium", Value(s, "border-top-width"), "inherit takes the parent's computed value");

        ComputedStyle m = Of(styles, "m");
        Assert.AreEqual("120px", Value(m, "width"));
        Assert.AreEqual(90, m.Length("height", 200));
        Assert.AreEqual("96px", Value(m, "margin-left"));
        Assert.AreEqual("1.2", Value(m, "line-height"));

        ComputedStyle u = Of(styles, "u");
        Assert.AreEqual("rgb(1, 2, 3)", Value(u, "color"));
        Assert.AreEqual("0", Value(u, "margin-top"));
        Assert.AreEqual(16, u.FontSize);
    }

    [TestMethod]
    public void SubstitutesCustomProperties()
    {
        var styles = Styles(
            """
            <style>
              :root { --gap: 4px; --brand: #336699; --pad: var(--gap) calc(var(--gap) * 2) }
              #a { margin: var(--gap) 0; color: var(--brand); padding: var(--pad) }
              #b { --gap: 1em; font-size: 10px; margin-top: var(--gap) }
              #c { --x: var(--y); --y: var(--x); color: var(--x); margin-top: var(--missing, 7px) }
              #d { color: var(--missing); border-left: var(--nope) }
              #e { --w: 5px; width: calc(var(--w) * 4) }
            </style>
            <body style="color: olive">
            <div id=a></div><div id=b></div><div id=c></div><div id=d></div><div id=e></div>
            """);

        ComputedStyle a = Of(styles, "a");
        Assert.AreEqual("4px", Value(a, "margin-top"));
        Assert.AreEqual("0", Value(a, "margin-left"));
        Assert.AreEqual("#336699", Value(a, "color"));
        Assert.AreEqual("4px", Value(a, "padding-top"));
        Assert.AreEqual("8px", Value(a, "padding-right"));

        Assert.AreEqual("10px", Value(Of(styles, "b"), "margin-top"), "em in a custom property resolves where it is used");
        Assert.AreEqual("olive", Value(Of(styles, "c"), "color"), "a cycle is invalid at computed time: color is unset, so it inherits");
        Assert.AreEqual("7px", Value(Of(styles, "c"), "margin-top"));
        Assert.AreEqual("olive", Value(Of(styles, "d"), "color"));
        Assert.AreEqual("none", Value(Of(styles, "d"), "border-left-style"));
        Assert.AreEqual("20px", Value(Of(styles, "e"), "width"));
    }

    [TestMethod]
    public void VarExpansionIsBounded()
    {
        // Each level doubles the output: without a budget this would be 2^30 tokens. Past the
        // budget the property is invalid, and an invalid variable takes the fallback.
        var css = new System.Text.StringBuilder(":root { --l0: x; ");
        for (int i = 1; i <= 30; i++) css.Append("--l").Append(i).Append(": var(--l").Append(i - 1).Append(") var(--l").Append(i - 1).Append("); ");
        css.Append("} p { color: var(--l30, red) }");
        var styles = Styles("<p id=p>x</p>", css.ToString());
        Assert.AreEqual("red", Value(Of(styles, "p"), "color"));
    }

    [TestMethod]
    public void AppliesUserAgentDefaultsAndBlockification()
    {
        var styles = Styles(
            """
            <head><style>#f { float: left } #flex { display: flex }</style></head>
            <div id=d><span id=s>x</span><span id=f>y</span></div>
            <ul><li id=li>a<ul><li id=nested>b</li></ul></li></ul>
            <table><tr><td id=td>c</td></tr></table>
            <div id=flex><span id=item>z</span></div>
            <p id=hidden hidden>h</p>
            """);

        Assert.AreEqual("block", Value(Of(styles, "d"), "display"));
        Assert.AreEqual("inline", Value(Of(styles, "s"), "display"));
        Assert.AreEqual("block", Value(Of(styles, "f"), "display"), "floats are blockified");
        Assert.AreEqual("block", Value(Of(styles, "item"), "display"), "flex items are blockified");
        Assert.AreEqual("list-item", Value(Of(styles, "li"), "display"));
        Assert.AreEqual("circle", Value(Of(styles, "nested"), "list-style-type"));
        Assert.AreEqual("table-cell", Value(Of(styles, "td"), "display"));
        Assert.AreEqual("none", Value(Of(styles, "hidden"), "display"));
        Assert.AreEqual("block", styles.Single(p => p.Key.Name == "html").Value.Keyword("display"));
        Assert.AreEqual("8px", Value(styles.Single(p => p.Key.Name == "body").Value, "margin-top"));
    }

    [TestMethod]
    public void MapsPresentationalHints()
    {
        var styles = Styles(
            """
            <style>.override { text-align: left }</style>
            <body bgcolor="ff0000" text=navy>
            <table id=t cellpadding=5 cellspacing=0 border=1 width="80%" align=center>
              <tr><td id=c align=right valign=top width=120 nowrap>x</td><td id=o class=override align=right>y</td></tr>
            </table>
            <font id=f color=green size=+2 face="Arial, Helvetica">z</font>
            """);

        ComputedStyle body = styles.Single(p => p.Key.Name == "body").Value;
        Assert.AreEqual("#ff0000", Value(body, "background-color"));
        Assert.AreEqual("navy", Value(body, "color"));

        ComputedStyle t = Of(styles, "t");
        Assert.AreEqual("80%", Value(t, "width"));
        Assert.AreEqual("auto", Value(t, "margin-left"));
        Assert.AreEqual(0, t.Length("border-spacing"));
        Assert.AreEqual("outset", Value(t, "border-top-style"));

        ComputedStyle c = Of(styles, "c");
        Assert.AreEqual("right", Value(c, "text-align"));
        Assert.AreEqual("top", Value(c, "vertical-align"));
        Assert.AreEqual("120px", Value(c, "width"));
        Assert.AreEqual("nowrap", Value(c, "white-space"));
        Assert.AreEqual("5px", Value(c, "padding-left"));
        Assert.AreEqual("inset", Value(c, "border-top-style"));

        Assert.AreEqual("left", Value(Of(styles, "o"), "text-align"), "author rules beat hints");

        ComputedStyle f = Of(styles, "f");
        Assert.AreEqual("green", Value(f, "color"));
        Assert.AreEqual(24, f.FontSize);
        Assert.AreEqual("\"Arial\",\"Helvetica\"", Value(f, "font-family"));
    }

    [TestMethod]
    public void ComputesPseudoElementsAndMediaRules()
    {
        HtmlDocument doc = HtmlParser.Parse("<q id=q>x</q><p id=p>y</p>");
        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddStyleSheet(
            """
            @media screen { p { color: red } }
            @media print { p { color: green } }
            p::before { content: "Note: "; font-weight: bold }
            @page :first { margin: 1cm }
            @font-face { font-family: X; src: url(x.ttf) }
            @import "missing.css";
            """,
            CssOrigin.Author,
            url => url == "missing.css" ? "p { text-transform: uppercase }" : null);

        HtmlElement p = doc.Body.ChildElements.Single(e => e.Name == "p");
        ComputedStyle body = resolver.Compute(doc.Body, resolver.Compute(doc.Html, null));
        ComputedStyle style = resolver.Compute(p, body);
        Assert.AreEqual("green", Value(style, "color"));
        Assert.AreEqual("uppercase", Value(style, "text-transform"));

        ComputedStyle before = resolver.Compute(p, style, "before");
        Assert.AreEqual("\"Note: \"", Value(before, "content"));
        Assert.AreEqual(700, before.Number("font-weight"));
        Assert.AreEqual("green", Value(before, "color"), "pseudo-elements inherit from their element");

        HtmlElement q = doc.Body.ChildElements.Single(e => e.Name == "q");
        Assert.AreEqual("open-quote", Value(resolver.Compute(q, body, "before"), "content"));
        Assert.HasCount(1, resolver.PageRules);
        Assert.HasCount(1, resolver.FontFaces);
    }
}
