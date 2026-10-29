using System.Diagnostics;
using PdfNetStack.Fonts;
using PdfNetStack.Html.Layout;
using static PdfNetStack.Tests.HtmlLayoutFixture;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class TableLayoutTests
{
    private const string Bare = "body { margin: 0 } table { border-spacing: 0 } td, th { padding: 0 }";

    private static readonly PdfFont Times = PdfStandardFont.TimesRoman;

    private static double W(string text) => Measure(Times, text, 16);

    [TestMethod]
    public void SizesColumnsToContent()
    {
        BoxFragment root = Layout("<table id=t><tr><td id=a>aaaa</td><td id=b>bb</td></tr></table>", Bare);
        BoxFragment a = ById(root, "a"), b = ById(root, "b"), t = ById(root, "t");
        Assert.AreEqual(W("aaaa"), a.Width, Tolerance);
        Assert.AreEqual(W("bb"), b.Width, Tolerance);
        Assert.AreEqual(a.X + a.Width, b.X, Tolerance);
        Assert.AreEqual(W("aaaa") + W("bb"), t.Width, Tolerance, "an auto table shrinks to its content");

        // The default style sheet: 2px spacing around cells, 1px cell padding.
        BoxFragment spaced = ById(Layout("<table id=t><tr><td>aaaa</td><td>bb</td></tr></table>", "body { margin: 0 }"), "t");
        Assert.AreEqual(W("aaaa") + W("bb") + 4 + (3 * 2), spaced.Width, Tolerance);
    }

    [TestMethod]
    public void DistributesWidthBetweenMinAndMax()
    {
        // A full width table gives the extra to columns by their maximum widths.
        BoxFragment root = Layout("<table style=\"width: 100%\"><tr><td id=a>aaaa</td><td id=b>bb</td></tr></table>", Bare);
        double ratio = W("aaaa") / W("bb");
        Assert.AreEqual(ratio, ById(root, "a").Width / ById(root, "b").Width, 1e-6);
        Assert.AreEqual(600, ById(root, "a").Width + ById(root, "b").Width, Tolerance);

        // A column with a width keeps it; the flexible one takes the rest.
        root = Layout("<table style=\"width: 100%\"><tr><td id=a style=\"width: 200px\">x</td><td id=b>y</td></tr></table>", Bare);
        Assert.AreEqual(200, ById(root, "a").Width, Tolerance);
        Assert.AreEqual(400, ById(root, "b").Width, Tolerance);

        // Percentages are of the table.
        root = Layout("<table style=\"width: 400px\"><tr><td id=a style=\"width: 25%\">x</td><td id=b>y</td></tr></table>", Bare);
        Assert.AreEqual(100, ById(root, "a").Width, Tolerance);

        // Too narrow for the maximums: each column gets its minimum plus a share of the rest.
        string words = "lorem ipsum dolor sit amet consectetur";
        root = Layout($"<div style=\"width: 300px\"><table id=t><tr><td id=a>{words}</td><td id=b>{words} {words}</td></tr></table></div>", Bare);
        BoxFragment narrow = ById(root, "t");
        Assert.AreEqual(300, narrow.Width, Tolerance);
        Assert.IsGreaterThanOrEqualTo(W("consectetur") - Tolerance, ById(root, "a").Width);
        Assert.IsGreaterThan(ById(root, "a").Width, ById(root, "b").Width);
    }

    [TestMethod]
    public void HonoursFixedLayout()
    {
        BoxFragment root = Layout(
            "<table style=\"table-layout: fixed; width: 300px\"><tr><td id=a style=\"width: 100px\">xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx</td><td id=b>y</td></tr></table>",
            Bare);
        Assert.AreEqual(100, ById(root, "a").Width, Tolerance, "fixed layout ignores content");
        Assert.AreEqual(200, ById(root, "b").Width, Tolerance);
    }

    [TestMethod]
    public void SpansColumnsAndRows()
    {
        BoxFragment root = Layout("""
            <table><tr><td id=s colspan=2>a much wider spanning cell</td></tr><tr><td id=a>a</td><td id=b>b</td></tr></table>
            """, Bare);
        BoxFragment s = ById(root, "s"), a = ById(root, "a"), b = ById(root, "b");
        Assert.AreEqual(a.Width + b.Width, s.Width, Tolerance);
        Assert.AreEqual(W("a much wider spanning cell"), s.Width, Tolerance, "the spanning cell's width is spread over its columns");
        Assert.AreEqual(W("a") / W("b"), a.Width / b.Width, 1e-6, "in proportion to their own widths");

        root = Layout("""
            <table><tr><td id=r rowspan=2 style="height: 100px">x</td><td id=c1>a</td></tr><tr><td id=c2>b</td></tr></table>
            """, Bare);
        BoxFragment r = ById(root, "r"), c1 = ById(root, "c1"), c2 = ById(root, "c2");
        Assert.AreEqual(100, r.Height, Tolerance);
        Assert.AreEqual(100, c1.Height + c2.Height, Tolerance, "the last spanned row grows to fit the tall cell");
        Assert.AreEqual(c1.Bottom, c2.Y, Tolerance);
    }

    [TestMethod]
    public void AlignsCellContentVertically()
    {
        BoxFragment root = Layout("""
            <table><tr>
              <td id=tall style="height: 80px">x</td>
              <td id=mid>y</td>
              <td id=top style="vertical-align: top">y</td>
              <td id=bottom style="vertical-align: bottom">y</td>
            </tr></table>
            """, Bare);
        double line = NormalLineHeight(Times, 16);
        BoxFragment mid = ById(root, "mid"), top = ById(root, "top"), bottom = ById(root, "bottom");
        Assert.AreEqual(80, mid.Height, Tolerance, "cells stretch to the row");
        Assert.AreEqual(mid.Y + ((80 - line) / 2), Lines(mid)[0].Y, Tolerance, "middle is the default for table rows");
        Assert.AreEqual(top.Y, Lines(top)[0].Y, Tolerance);
        Assert.AreEqual(bottom.Bottom - line, Lines(bottom)[0].Y, Tolerance);
    }

    [TestMethod]
    public void CollapsesBorders()
    {
        BoxFragment root = Layout(
            "<table id=t><tr><td id=a>aaaa</td><td id=b>bb</td></tr></table>",
            Bare + " table { border-collapse: collapse } td { border: 2px solid rgb(0, 0, 0) } td#b { border-left: 6px solid rgb(255, 0, 0) }");
        BoxFragment a = ById(root, "a"), b = ById(root, "b"), t = ById(root, "t");
        Assert.AreEqual(2, a.Border.Left, Tolerance, "outer edges belong to the cell whole");
        Assert.AreEqual(3, a.Border.Right, Tolerance, "the wider edge wins, and each side takes half");
        Assert.AreEqual(3, b.Border.Left, Tolerance);
        Assert.AreEqual(1, a.CollapsedBorders![1].Color.R, "both cells draw the winning red");
        Assert.AreEqual(1, b.CollapsedBorders![3].Color.R);
        Assert.AreEqual(0, a.CollapsedBorders[3].Color.R);
        Assert.AreEqual(W("aaaa") + W("bb") + 2 + 3 + 3 + 2, t.Width, Tolerance);
        Assert.AreEqual(0, t.Border.Left, "the table's own border is drawn by its cells");
    }

    [TestMethod]
    public void OrdersGroupsAndPlacesCaptions()
    {
        BoxFragment root = Layout("""
            <table id=t><caption id=c>Caption</caption>
              <tfoot><tr><td id=f>foot</td></tr></tfoot>
              <tbody><tr><td id=b>body</td></tr></tbody>
              <thead><tr><td id=h>head</td></tr></thead>
            </table>
            """, Bare);
        BoxFragment c = ById(root, "c"), t = ById(root, "t"), h = ById(root, "h"), b = ById(root, "b"), f = ById(root, "f");
        Assert.IsLessThanOrEqualTo(t.Y + Tolerance, c.Bottom, "the caption sits above the table");
        Assert.IsLessThan(b.Y, h.Y, "the header group comes first wherever it is written");
        Assert.IsLessThan(f.Y, b.Y, "the footer group comes last");
    }

    [TestMethod]
    public void ShrinksInlineTables()
    {
        BoxFragment root = Layout("<p>x <table id=t style=\"display: inline-table\"><tr><td>ab</td></tr></table> y</p>", Bare);
        BoxFragment t = ById(root, "t");
        Assert.AreEqual(W("ab"), t.Width, Tolerance);
    }

    [TestMethod]
    public void HostileSpansStayBounded()
    {
        string row = "<tr>" + string.Concat(Enumerable.Repeat("<td colspan=1000 rowspan=1000>x</td>", 50)) + "</tr>";
        var clock = Stopwatch.StartNew();
        BoxFragment root = Layout("<table id=t>" + string.Concat(Enumerable.Repeat(row, 200)) + "</table>", Bare);
        Assert.IsLessThan(30_000, clock.ElapsedMilliseconds);
        Assert.IsNotNull(ById(root, "t"));
    }
}
