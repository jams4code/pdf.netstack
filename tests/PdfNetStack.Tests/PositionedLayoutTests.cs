using PdfNetStack.Fonts;
using PdfNetStack.Html.Layout;
using static PdfNetStack.Tests.HtmlLayoutFixture;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class PositionedLayoutTests
{
    private const string Bare = "body { margin: 0 } p { margin: 0 }";

    private static readonly double Line = NormalLineHeight(PdfStandardFont.TimesRoman, 16);

    [TestMethod]
    public void WrapsLinesAroundFloats()
    {
        string words = string.Join(" ", Enumerable.Repeat("word", 80));
        BoxFragment root = Layout($"<div style=\"width: 300px\"><div id=f style=\"float: left; width: 100px; height: 50px\"></div><p id=p>{words}</p></div>", Bare);
        BoxFragment f = ById(root, "f");
        Assert.AreEqual(0, f.X, Tolerance);
        Assert.AreEqual(100, f.Width, Tolerance);

        List<LineFragment> lines = Lines(ById(root, "p"));
        foreach (LineFragment line in lines)
        {
            bool beside = line.Y < 50;
            Assert.AreEqual(beside ? 100 : 0, line.X, Tolerance, $"line at {line.Y}");
            Assert.AreEqual(beside ? 200 : 300, line.Width, Tolerance);
            double right = line.Items.OfType<Html.Layout.TextFragment>().Max(t => t.X + t.Width);
            Assert.IsLessThanOrEqualTo(line.X + line.Width + Tolerance, right);
        }

        Assert.IsGreaterThan(Math.Ceiling(50 / Line), lines.Count);
    }

    [TestMethod]
    public void StacksFloatsSideBySideThenBelow()
    {
        BoxFragment root = Layout("""
            <div style="width: 300px">
              <div id=a style="float: left; width: 120px; height: 40px"></div>
              <div id=b style="float: left; width: 120px; height: 30px"></div>
              <div id=c style="float: left; width: 120px; height: 20px"></div>
              <div id=r style="float: right; width: 50px; height: 10px"></div>
            </div>
            """, Bare);
        Assert.AreEqual(0, ById(root, "a").X, Tolerance);
        Assert.AreEqual(120, ById(root, "b").X, Tolerance);
        // No room beside the first two: the third goes down to where the shorter one ends, beside the taller.
        Assert.AreEqual(30, ById(root, "c").Y, Tolerance);
        Assert.AreEqual(120, ById(root, "c").X, Tolerance);
        Assert.AreEqual(250, ById(root, "r").X, Tolerance);
        Assert.AreEqual(30, ById(root, "r").Y, Tolerance, "a float is never higher than an earlier one");
    }

    [TestMethod]
    public void ClearsAndContainsFloats()
    {
        BoxFragment root = Layout("""
            <div id=plain><div style="float: left; width: 10px; height: 80px"></div></div>
            <p id=cleared style="clear: left">after</p>
            <div id=bfc style="overflow: hidden"><div style="float: right; width: 10px; height: 60px"></div></div>
            <div style="float: left; width: 100px; height: 50px"></div>
            <div id=root style="display: flow-root">beside</div>
            """, Bare);
        Assert.AreEqual(0, ById(root, "plain").Height, Tolerance, "an ordinary block does not grow around its floats");
        Assert.AreEqual(80, ById(root, "cleared").Y, Tolerance, "clear moves the block below the float");
        Assert.AreEqual(60, ById(root, "bfc").Height, Tolerance, "a formatting context root contains its floats");

        BoxFragment beside = ById(root, "root");
        Assert.AreEqual(100, beside.X, Tolerance, "a new formatting context sits beside a float");
        Assert.AreEqual(500, beside.Width, Tolerance);
    }

    [TestMethod]
    public void PositionsAbsoluteBoxes()
    {
        BoxFragment root = Layout("""
            <div id=rel style="position: relative; width: 300px; height: 200px; margin-left: 10px; border: 5px solid">
              <p id=before>before</p>
              <div id=abs style="position: absolute; right: 10px; top: 20px; width: 50px; height: 30px"></div>
              <div id=stretch style="position: absolute; left: 10px; right: 10px; bottom: 0; height: 15px"></div>
              <div id=static style="position: absolute">static</div>
              <p id=after>after</p>
            </div>
            """, Bare);
        BoxFragment rel = ById(root, "rel");
        double padLeft = rel.X + 5, padTop = rel.Y + 5;
        BoxFragment abs = ById(root, "abs");
        Assert.AreEqual(padLeft + 300 - 10 - 50, abs.X, Tolerance, "right inset from the padding box");
        Assert.AreEqual(padTop + 20, abs.Y, Tolerance);

        BoxFragment stretch = ById(root, "stretch");
        Assert.AreEqual(280, stretch.Width, Tolerance, "left and right insets size an auto width");
        Assert.AreEqual(padTop + 200 - 15, stretch.Y, Tolerance, "bottom inset");

        BoxFragment stat = ById(root, "static");
        Assert.AreEqual(ById(root, "before").Bottom, stat.Y, Tolerance, "without insets the box stays where it would have been");
        Assert.AreEqual(Measure(PdfStandardFont.TimesRoman, "static", 16), stat.Width, Tolerance, "and shrinks to fit");
        Assert.AreEqual(ById(root, "before").Bottom, ById(root, "after").Y, Tolerance, "absolute boxes take no room in the flow");
    }

    [TestMethod]
    public void OffsetsRelativeBoxesAndOrdersByZIndex()
    {
        BoxFragment root = Layout("""
            <div id=box style="position: relative; top: 10px; left: 5px; height: 40px">
              <div id=under style="position: absolute; z-index: -1">u</div>
            </div>
            """, Bare);
        BoxFragment box = ById(root, "box");
        Assert.AreEqual(10, box.Y, Tolerance);
        Assert.AreEqual(5, box.X, Tolerance);
        Assert.AreSame(ById(root, "under"), box.Children[0], "negative z-index paints first");
    }
}
