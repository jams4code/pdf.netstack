using System.Text;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class HtmlParserTests
{
    /// <summary>The tree as a compact string: elements in brackets, text in quotes.</summary>
    private static string Tree(HtmlElement e)
    {
        var sb = new StringBuilder();
        foreach (HtmlNode n in e.Children)
        {
            if (n is HtmlText t) sb.Append('"').Append(t.Data).Append('"');
            else if (n is HtmlElement c) sb.Append(c.Name).Append('[').Append(Tree(c)).Append(']');
        }

        return sb.ToString();
    }

    private static string Body(string html) => Tree(HtmlParser.Parse(html).Body);

    [TestMethod]
    public void ImpliesTheDocumentStructure()
    {
        HtmlDocument doc = HtmlParser.Parse("<title>Report</title><style>p { color: red }</style><p>Hello");
        Assert.AreEqual("Report", doc.Title);
        Assert.AreEqual("title[\"Report\"]style[\"p { color: red }\"]", Tree(doc.Head));
        Assert.AreEqual("p[\"Hello\"]", Tree(doc.Body));
        Assert.AreEqual("p[\"x\"]", Body("<!DOCTYPE html><html lang=en><head></head><body><p>x</p></body></html>"));
        Assert.AreEqual("en", doc.Html.GetAttribute("lang") ?? HtmlParser.Parse("<html lang=en><p>x").Html.GetAttribute("lang"));
    }

    [TestMethod]
    public void DecodesCharacterReferences()
    {
        Assert.AreEqual("p[\"& < © © A ¬it; \u00A0 € � &bogus;\"]", Body("<p>&amp; &lt; &copy; &#169; &#x41; &notit; &nbsp; &#128; &#0; &bogus;"));
        HtmlElement a = HtmlParser.Parse("<a href=\"?a=1&copy=2&amp;b\" title='&lt;x&gt;'>x</a>").Body.ChildElements.Single();
        Assert.AreEqual("?a=1&copy=2&b", a.GetAttribute("href"));
        Assert.AreEqual("<x>", a.GetAttribute("title"));
    }

    [TestMethod]
    public void HandlesVoidAndRawTextElements()
    {
        Assert.AreEqual("p[\"a\"br[]\"b\"img[]\"c\"]", Body("<p>a<br>b<img src=x>c</p>"));
        // A script before any body content belongs to the head, as in browsers.
        Assert.AreEqual("script[\"if (a<b && c) { x = '</p>'; }\"]", Tree(HtmlParser.Parse("<script>if (a<b && c) { x = '</p>'; }</script>").Head));
        Assert.AreEqual("textarea[\"<b> & x\"]", Body("<textarea><b> &amp; x</textarea>"));
    }

    [TestMethod]
    public void ClosesElementsAsBrowsersDo()
    {
        Assert.AreEqual("p[\"one\"]p[\"two\"]div[\"three\"]", Body("<p>one<p>two<div>three</div>"));
        Assert.AreEqual("ul[li[\"a\"]li[\"b\"]]", Body("<ul><li>a<li>b</ul>"));
        Assert.AreEqual("dl[dt[\"t\"]dd[\"d\"]dt[\"u\"]]", Body("<dl><dt>t<dd>d<dt>u</dl>"));
        Assert.AreEqual("h1[\"a\"]h2[\"b\"]", Body("<h1>a<h2>b</h2>"));
        Assert.AreEqual("p[]", Body("</p>"));
        Assert.AreEqual("br[]", Body("</br>"));
    }

    [TestMethod]
    public void BuildsTables()
    {
        Assert.AreEqual("table[tbody[tr[td[\"a\"]td[\"b\"]]tr[td[\"c\"]]]]", Body("<table><tr><td>a<td>b<tr><td>c</table>"));
        Assert.AreEqual("table[tbody[tr[td[\"a\"]]]]", Body("<table><td>a</table>"));
        Assert.AreEqual("\"loose\"table[tbody[tr[td[\"a\"]]]]", Body("<table>loose<tr><td>a</td></tr></table>"));
        Assert.AreEqual("table[thead[tr[th[\"h\"]]]tbody[tr[td[\"d\"]]]]", Body("<table><thead><tr><th>h<tbody><tr><td>d</table>"));
    }

    [TestMethod]
    public void ToleratesBrokenMarkup()
    {
        Assert.AreEqual("b[i[\"x\"]]\"y\"", Body("<b><i>x</b>y</i>"));
        Assert.AreEqual("div[\"a\"]", Body("<div>a"));
        Assert.AreEqual("p[\"x\"]", Body("<!-- comment --><p>x<!-- unterminated"));
        HtmlElement e = HtmlParser.Parse("<DIV CLASS='a b' class=c data-x>t</DIV>").Body.ChildElements.Single();
        Assert.AreEqual("div", e.Name);
        CollectionAssert.AreEqual(new[] { "a", "b" }, e.ClassList.ToArray());
        Assert.AreEqual("", e.GetAttribute("data-x"));
    }

    [TestMethod]
    public void DeepNestingIsBounded()
    {
        string html = string.Concat(Enumerable.Repeat("<div>", 20_000)) + "x";
        HtmlDocument doc = HtmlParser.Parse(html);
        Assert.AreEqual("x", doc.Body.TextContent);
    }
}
