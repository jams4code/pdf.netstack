using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class BoxTreeTests
{
    /// <summary>A compact tree: Type&lt;element&gt;[children], text in quotes, list markers in braces.</summary>
    private static string Dump(Box b)
    {
        if (b is TextBox t) return "\"" + t.Text + "\"";
        var sb = new StringBuilder(b.GetType().Name.Replace("Box", ""));
        if (b.Element is not null) sb.Append('<').Append(b.Element.Name).Append('>');
        if (b is BlockBox { Marker: Box marker }) sb.Append('{').Append(string.Join(" ", marker.Children.Select(Dump))).Append('}');
        if (b.Children.Count > 0) sb.Append('[').Append(string.Join(" ", b.Children.Select(Dump))).Append(']');
        return sb.ToString();
    }

    private static Box Body(string html, string? css = null, Func<string, PdfImage?>? images = null)
    {
        HtmlDocument doc = HtmlParser.Parse(html);
        var resolver = new StyleResolver(CssMedia.Default);
        if (css is not null) resolver.AddStyleSheet(css, CssOrigin.Author);
        resolver.AddDocument(doc);
        BlockBox root = new BoxBuilder(resolver, images).Build(doc);
        Assert.AreEqual("html", root.Element?.Name);
        return root.Children.Single();
    }

    private static string Tree(string html, string? css = null, Func<string, PdfImage?>? images = null) =>
        string.Join(" ", Body(html, css, images).Children.Select(Dump));

    [TestMethod]
    public void BuildsBlocksAndInlines()
    {
        Assert.AreEqual("Block<p>[\"Hello \" Inline<b>[\"big\"] \" world\"]", Tree("<p>Hello <b>big</b> world</p>"));
        Assert.AreEqual("Block<p>[\"a\" LineBreak<br> \"b\"]", Tree("<p>a<br>b"));
        Assert.AreEqual("Block<div>[Inline<b>[\"y\"]]", Tree("<div><span style=\"display:none\">x</span><div style=\"display:contents\"><b>y</b></div></div>"));
        Assert.AreEqual("InlineBlock<input>[\"abc\"] \" \" InlineBlock<input>[\"" + (char)0x2611 + "\"]", Tree("<input value=abc> <input type=checkbox checked>"));
    }

    [TestMethod]
    public void CollapsesWhiteSpaceAcrossInlines()
    {
        Assert.AreEqual("Block<p>[\"a \" Inline<b>[\"b \"] \"c \"]", Tree("<p>  a \n  <b> b </b>  c  </p>"));
        Assert.AreEqual("Block<pre>[\"  a\n  b\"]", Tree("<pre>  a\n  b</pre>"));
        Assert.AreEqual("Block<p>[\"a\nb \"]", Tree("<p style=\"white-space: pre-line\">a   \n   b   </p>"));
        Assert.AreEqual("Block<p>[\"HELLO \" Inline<i>[\"World Wide\"]]", Tree("<p style=\"text-transform: uppercase\">hello <i style=\"text-transform: capitalize\">world wide</i>"));
    }

    [TestMethod]
    public void WrapsMixedContentInAnonymousBlocks()
    {
        Assert.AreEqual("Block<div>[Block[\"text\"] Block<p>[\"para\"] Block[\"more\"]]", Tree("<div>text<p>para</p>more</div>"));

        // An inline around a block is split; each part keeps the element, edges drawn once.
        Box div = Body("<div><span>a<p>b</p>c</span></div>").Children.Single();
        Assert.AreEqual("Block<div>[Block[Inline<span>[\"a\"]] Block<p>[\"b\"] Block[Inline<span>[\"c\"]]]", Dump(div));
        var first = (InlineBox)div.Children[0].Children[0];
        var last = (InlineBox)div.Children[2].Children[0];
        Assert.IsTrue(first.HasStartEdge && !first.HasEndEdge);
        Assert.IsTrue(!last.HasStartEdge && last.HasEndEdge);
        Assert.IsTrue(div.Children[0].IsAnonymous);
    }

    [TestMethod]
    public void NumbersListItems()
    {
        string Markers(string html, string? css = null) =>
            string.Join("|", Body(html, css).Children.SelectMany(l => l.Children).OfType<BlockBox>()
                .Select(li => li.Marker is null ? "-" : string.Concat(li.Marker.Children.OfType<TextBox>().Select(t => t.Text))));

        Assert.AreEqual("3. |10. |11. ", Markers("<ol start=3><li>a</li><li value=10>b</li><li>c</li></ol>"));
        Assert.AreEqual("3. |2. |1. ", Markers("<ol reversed><li>a<li>b<li>c</ol>"));
        Assert.AreEqual("I. |II. ", Markers("<ol style=\"list-style-type: upper-roman\"><li>a<li>b</ol>"));
        Assert.AreEqual("a. |b. ", Markers("<ol type=a><li>a<li>b</ol>"));
        Assert.AreEqual((char)0x2022 + " ", Markers("<ul><li>a</ul>"));
        Assert.AreEqual("-", Markers("<ul style=\"list-style: none\"><li>a</ul>"));
        Assert.AreEqual((char)0x0661 + ". |" + (char)0x0662 + ". ", Markers("<ol style=\"list-style-type: arabic-indic\"><li>a<li>b</ol>"));
        Assert.AreEqual("> ", Markers("<ul><li>a</ul>", "li::marker { content: \"> \" }"));

        // Inside markers become the first inline of the item.
        Assert.AreEqual("Block<ul>[Block<li>[Inline[\"" + (char)0x2022 + " \"] \"a\"]]", Tree("<ul style=\"list-style-position: inside\"><li>a</ul>"));

        // Nested lists keep their own list-item counter.
        Box outer = Body("<ol><li>a<ol><li>x<li>y</ol><li>b</ol>").Children.Single();
        BlockBox second = (BlockBox)outer.Children[1];
        Assert.AreEqual("2. ", ((TextBox)second.Marker!.Children.Single()).Text);
    }

    [TestMethod]
    public void GeneratesContent()
    {
        string css = """
            body { counter-reset: sec }
            h2 { counter-increment: sec }
            h2::before { content: "Section " counter(sec, upper-roman) ". " }
            a::after { content: " (" attr(href) ")" }
            ol.n { counter-reset: item; list-style: none }
            ol.n li { counter-increment: item }
            ol.n li::before { content: counters(item, ".") " " }
            p.block::before { content: "x"; display: block }
            """;
        Assert.AreEqual(
            "Block<h2>[Inline[\"Section I. \"] \"A\"] Block<h2>[Inline[\"Section II. \"] \"B\"]",
            Tree("<h2>A</h2><h2>B</h2>", css));
        Assert.AreEqual("Block<p>[Inline<a>[\"link\" Inline[\" (x.html)\"]]]", Tree("<p><a href=x.html>link</a>", css));

        string open = ((char)0x201C).ToString(), close = ((char)0x201D).ToString(), open2 = ((char)0x2018).ToString(), close2 = ((char)0x2019).ToString();
        Assert.AreEqual(
            $"Block<p>[Inline<q>[Inline[\"{open}\"] \"hi \" Inline<q>[Inline[\"{open2}\"] \"there\" Inline[\"{close2}\"]] Inline[\"{close}\"]]]",
            Tree("<p><q>hi <q>there</q></q>"));

        string numbers = string.Join("|", Body("<ol class=n><li>a<ol class=n><li>b<li>c</ol><li>d</ol>", css)
            .Children.Single().Children.OfType<BlockBox>().SelectMany(Flatten).OfType<TextBox>().Select(t => t.Text).Where(t => t.Length > 0 && t[t.Length - 1] == ' '));
        Assert.AreEqual("1 |1.1 |1.2 |2 ", numbers);

        Assert.AreEqual("Block<p>[Block[\"x\"] Block[\"y\"]]", Tree("<p class=block>y</p>", css));
    }

    private static IEnumerable<Box> Flatten(Box b) => new[] { b }.Concat(b.Children.SelectMany(Flatten));

    [TestMethod]
    public void FixesUpTables()
    {
        Assert.AreEqual(
            "Table<table>[TableRowGroup<tbody>[TableRow<tr>[TableCell<td>[\"a\"] TableCell<td>[\"b\"]]]]",
            Tree("<table> <tr> <td>a</td> <td>b</td> </tr> </table>"));

        // Table parts outside a table get anonymous wrappers; loose content in a row gets a cell.
        Assert.AreEqual(
            "Table[TableRowGroup[TableRow<div>[TableCell<div>[\"x\"] TableCell[\"loose\"]]]]",
            Tree("<div style=\"display:table-row\"><div style=\"display:table-cell\">x</div>loose</div>"));

        TableCellBox cell = Flatten(Body("<table><tr><td colspan=3 rowspan=2>a<td colspan=99999>b</table>")).OfType<TableCellBox>().First();
        Assert.AreEqual(3, cell.ColumnSpan);
        Assert.AreEqual(2, cell.RowSpan);
        Assert.AreEqual(1000, Flatten(Body("<table><tr><td colspan=99999>b</table>")).OfType<TableCellBox>().Single().ColumnSpan);

        Assert.AreEqual(
            "Table<table>[TableCaption<caption>[\"t\"] TableColumnGroup<colgroup>[TableColumn<col>] TableRowGroup<thead>[TableRow<tr>[TableCell<th>[\"h\"]]]]",
            Tree("<table><caption>t</caption><colgroup><col></colgroup><thead><tr><th>h</th></tr></thead></table>"));
    }

    [TestMethod]
    public void WrapsFlexItems()
    {
        Assert.AreEqual(
            "Flex<div>[Block[\"text\"] Block<span>[\"s\"] Block<p>[\"p\"]]",
            Tree("<div style=\"display:flex\">text<span>s</span> <p>p</p></div>"));
        Assert.AreEqual("Block<p>[\"a \" Flex<span>[Block<b>[\"x\"]] \" b\"]", Tree("<p>a <span style=\"display:inline-flex\"><b>x</b></span> b"));
    }

    [TestMethod]
    public void LoadsImagesOrShowsAltText()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "rgb8-interlaced-300dpi.png");
        PdfImage? Load(string src) => src == "logo.png" ? PdfImage.Load(fixture) : null;

        var image = (ReplacedBox)Body("<p><img src=logo.png alt=Logo></p>", images: Load).Children.Single().Children.Single();
        // PNG stores pixels per meter: 300 dpi is 11811 ppm, 299.9994 dpi.
        Assert.AreEqual(image.Image.PixelWidth * 96.0 / 300, image.IntrinsicWidth, 1e-4);
        Assert.IsFalse(image.IsBlockLevel);
        Assert.IsTrue(((ReplacedBox)Body("<img src=logo.png style=\"display:block\">", images: Load).Children.Single()).IsBlockLevel);

        Assert.AreEqual("Block<p>[Inline<img>[\"Logo\"]]", Tree("<p><img src=missing.png alt=Logo></p>", images: Load));
        Assert.AreEqual("Block<p>[\"a\"]", Tree("<p>a<img src=missing.png></p>", images: Load));
    }
}
