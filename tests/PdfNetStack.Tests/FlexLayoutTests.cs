using PdfNetStack.Fonts;
using PdfNetStack.Html.Layout;
using static PdfNetStack.Tests.HtmlLayoutFixture;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class FlexLayoutTests
{
    private const string Bare = "body { margin: 0 } p { margin: 0 }";

    private static double W(string text) => Measure(PdfStandardFont.TimesRoman, text, 16);

    private static BoxFragment Flex(string container, string items, string css = "") =>
        Layout($"<div id=c style=\"display: flex; {container}\">{items}</div>", Bare + css);

    [TestMethod]
    public void GrowsByFlexFactors()
    {
        BoxFragment root = Flex("width: 300px", "<div id=a style=\"flex: 1\">x</div><div id=b style=\"flex: 2\">y</div>");
        Assert.AreEqual(100, ById(root, "a").Width, Tolerance);
        Assert.AreEqual(200, ById(root, "b").Width, Tolerance);
        Assert.AreEqual(100, ById(root, "b").X, Tolerance);

        // Without flex factors items keep their content width, side by side.
        root = Flex("width: 300px", "<span id=a>aa</span><span id=b>b</span>");
        Assert.AreEqual(W("aa"), ById(root, "a").Width, Tolerance);
        Assert.AreEqual(ById(root, "a").Bottom - ById(root, "a").Height, ById(root, "b").Y, Tolerance);
        Assert.AreEqual(W("aa"), ById(root, "b").X, Tolerance);
    }

    [TestMethod]
    public void ShrinksAndFreezesAtMinimums()
    {
        BoxFragment root = Flex("width: 300px", "<div id=a style=\"flex: 0 1 200px\">x</div><div id=b style=\"flex: 0 1 200px\">y</div>");
        Assert.AreEqual(150, ById(root, "a").Width, Tolerance);
        Assert.AreEqual(150, ById(root, "b").Width, Tolerance);

        root = Flex("width: 300px", "<div id=a style=\"flex: 0 1 200px; min-width: 180px\">x</div><div id=b style=\"flex: 0 1 200px\">y</div>");
        Assert.AreEqual(180, ById(root, "a").Width, Tolerance, "a min-width violation freezes the item");
        Assert.AreEqual(120, ById(root, "b").Width, Tolerance, "and the other takes the rest of the shrink");

        // The automatic minimum keeps an item at least as wide as its longest word.
        root = Flex("width: 50px", "<div id=a style=\"flex: 0 1 200px\">unbreakable</div>");
        Assert.AreEqual(W("unbreakable"), ById(root, "a").Width, Tolerance);
    }

    [TestMethod]
    public void JustifiesAndUsesAutoMargins()
    {
        string items = "<div id=a style=\"width: 50px\">x</div><div id=b style=\"width: 70px\">y</div>";
        BoxFragment between = Flex("width: 300px; justify-content: space-between", items);
        Assert.AreEqual(0, ById(between, "a").X, Tolerance);
        Assert.AreEqual(230, ById(between, "b").X, Tolerance);

        BoxFragment centre = Flex("width: 300px; justify-content: center", items);
        Assert.AreEqual(90, ById(centre, "a").X, Tolerance);

        BoxFragment evenly = Flex("width: 300px; justify-content: space-evenly", items);
        Assert.AreEqual(60, ById(evenly, "a").X, Tolerance);
        Assert.AreEqual(170, ById(evenly, "b").X, Tolerance);

        BoxFragment gap = Flex("width: 300px; column-gap: 10px", items);
        Assert.AreEqual(60, ById(gap, "b").X, Tolerance);

        BoxFragment auto = Flex("width: 300px", "<div id=a style=\"width: 50px\">x</div><div id=b style=\"width: 70px; margin-left: auto\">y</div>");
        Assert.AreEqual(230, ById(auto, "b").X, Tolerance, "an auto margin takes the free space");

        BoxFragment reversed = Flex("width: 300px; flex-direction: row-reverse", items);
        Assert.AreEqual(250, ById(reversed, "a").X, Tolerance, "row-reverse packs from the right");
        Assert.AreEqual(180, ById(reversed, "b").X, Tolerance);

        BoxFragment rtl = Flex("width: 300px; direction: rtl", items);
        Assert.AreEqual(250, ById(rtl, "a").X, Tolerance, "a row runs right to left in rtl");

        BoxFragment columnReverse = Flex("flex-direction: column-reverse; height: 100px", "<div id=a style=\"height: 30px\">x</div><div id=b style=\"height: 20px\">y</div>");
        Assert.AreEqual(70, ById(columnReverse, "a").Y, Tolerance);
        Assert.AreEqual(50, ById(columnReverse, "b").Y, Tolerance);

        BoxFragment ordered = Flex("width: 300px", "<div id=a style=\"width: 50px\">x</div><div id=b style=\"width: 70px; order: -1\">y</div>");
        Assert.AreEqual(0, ById(ordered, "b").X, Tolerance);
        Assert.AreEqual(70, ById(ordered, "a").X, Tolerance);
    }

    [TestMethod]
    public void AlignsAcrossTheLine()
    {
        double line = NormalLineHeight(PdfStandardFont.TimesRoman, 16);
        string items = "<div id=a>x</div><div id=b style=\"height: 50px\">y</div>";
        BoxFragment stretch = Flex("width: 300px", items);
        Assert.AreEqual(50, ById(stretch, "a").Height, Tolerance, "items stretch to the tallest by default");
        Assert.AreEqual(50, ById(stretch, "c").Height, Tolerance);

        BoxFragment centre = Flex("width: 300px; align-items: center", items);
        Assert.AreEqual((50 - line) / 2, ById(centre, "a").Y, Tolerance);
        Assert.AreEqual(line, ById(centre, "a").Height, Tolerance);

        BoxFragment end = Flex("width: 300px; align-items: flex-end", items);
        Assert.AreEqual(50 - line, ById(end, "a").Y, Tolerance);

        BoxFragment self = Flex("width: 300px", "<div id=a style=\"align-self: flex-end\">x</div><div id=b style=\"height: 50px\">y</div>");
        Assert.AreEqual(50 - line, ById(self, "a").Y, Tolerance);
    }

    [TestMethod]
    public void WrapsLines()
    {
        string items = string.Concat(Enumerable.Range(1, 3).Select(i => $"<div id=i{i} style=\"width: 120px; height: 20px\">x</div>"));
        BoxFragment root = Flex("width: 300px; flex-wrap: wrap; row-gap: 5px", items);
        Assert.AreEqual(0, ById(root, "i1").Y, Tolerance);
        Assert.AreEqual(120, ById(root, "i2").X, Tolerance);
        Assert.AreEqual(0, ById(root, "i3").X, Tolerance, "the third item does not fit and starts a line");
        Assert.AreEqual(25, ById(root, "i3").Y, Tolerance);
        Assert.AreEqual(45, ById(root, "c").Height, Tolerance);
    }

    [TestMethod]
    public void LaysOutColumns()
    {
        BoxFragment root = Flex("flex-direction: column; height: 300px; width: 200px", "<div id=a style=\"flex: 1\">x</div><div id=b style=\"flex: 2\">y</div>");
        Assert.AreEqual(100, ById(root, "a").Height, Tolerance);
        Assert.AreEqual(200, ById(root, "b").Height, Tolerance);
        Assert.AreEqual(100, ById(root, "b").Y, Tolerance);
        Assert.AreEqual(200, ById(root, "a").Width, Tolerance, "column items stretch across");

        BoxFragment centred = Flex("flex-direction: column; width: 200px; align-items: center", "<div id=a>xx</div>");
        BoxFragment a = ById(centred, "a");
        Assert.AreEqual(W("xx"), a.Width, Tolerance);
        Assert.AreEqual((200 - W("xx")) / 2, a.X, Tolerance);

        BoxFragment auto = Flex("flex-direction: column; width: 200px", "<div id=a>x</div><div id=b>y</div>");
        Assert.AreEqual(ById(auto, "a").Bottom, ById(auto, "b").Y, Tolerance, "without a height, items stack at their own heights");
    }

    [TestMethod]
    public void ShrinksInlineFlexToContent()
    {
        BoxFragment root = Layout("<p><span id=c style=\"display: inline-flex; column-gap: 4px\"><b>ab</b><i>cd</i></span></p>", Bare);
        double expected = Measure(PdfStandardFont.TimesBold, "ab", 16) + 4 + Measure(PdfStandardFont.TimesItalic, "cd", 16);
        Assert.AreEqual(expected, ById(root, "c").Width, Tolerance);
    }
}
