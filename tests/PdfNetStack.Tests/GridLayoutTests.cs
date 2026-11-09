using System.Text.RegularExpressions;
using PdfNetStack.Fonts;
using PdfNetStack.Html;
using PdfNetStack.Html.Layout;
using static PdfNetStack.Tests.HtmlLayoutFixture;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class GridLayoutTests
{
    private const string Bare = "body { margin: 0 } p { margin: 0 }";

    private static double W(string text) => Measure(PdfStandardFont.TimesRoman, text, 16);

    private static BoxFragment Grid(string container, string items) =>
        Layout($"<div id=g style=\"display: grid; {container}\">{items}</div>", Bare);

    private static string Cells(int n) => string.Concat(Enumerable.Range(1, n).Select(i => $"<div id=i{i}>{i}</div>"));

    [TestMethod]
    public void SizesFixedAndFlexibleTracks()
    {
        BoxFragment root = Grid("width: 400px; grid-template-columns: 100px 1fr 2fr", Cells(3));
        Assert.AreEqual(100, ById(root, "i1").Width, Tolerance);
        Assert.AreEqual(100, ById(root, "i2").Width, Tolerance);
        Assert.AreEqual(200, ById(root, "i3").Width, Tolerance);

        root = Grid("width: 420px; grid-template-columns: 100px 1fr 2fr; column-gap: 10px", Cells(3));
        Assert.AreEqual(110, ById(root, "i2").X, Tolerance);
        Assert.AreEqual(220, ById(root, "i3").X, Tolerance);
        Assert.AreEqual(200, ById(root, "i3").Width, Tolerance);

        root = Grid("width: 300px; grid-template-columns: repeat(3, 1fr)", Cells(3));
        Assert.AreEqual(100, ById(root, "i3").Width, Tolerance);

        root = Grid("width: 350px; grid-template-columns: repeat(auto-fill, 100px)", Cells(4));
        Assert.AreEqual(200, ById(root, "i3").X, Tolerance, "three 100px columns fit in 350px");
        Assert.AreEqual(0, ById(root, "i4").X, Tolerance);

        root = Grid("width: 300px; grid-template-columns: auto 1fr", "<div id=i1>label</div><div id=i2>value</div>");
        Assert.AreEqual(W("label"), ById(root, "i1").Width, Tolerance, "an auto track fits its content");
        Assert.AreEqual(300 - W("label"), ById(root, "i2").Width, Tolerance, "fr takes the rest");

        root = Grid("width: 300px; grid-template-columns: minmax(200px, 1fr) 50px", Cells(2));
        Assert.AreEqual(250, ById(root, "i1").Width, Tolerance);
    }

    [TestMethod]
    public void PlacesItemsAutomaticallyAndByLines()
    {
        double line = NormalLineHeight(PdfStandardFont.TimesRoman, 16);
        BoxFragment root = Grid("width: 200px; grid-template-columns: 1fr 1fr", Cells(5));
        Assert.AreEqual(100, ById(root, "i2").X, Tolerance);
        Assert.AreEqual(line, ById(root, "i3").Y, Tolerance, "the third item starts the second row");
        Assert.AreEqual(2 * line, ById(root, "i5").Y, Tolerance);

        root = Grid("width: 300px; grid-template-columns: repeat(3, 1fr)", """
            <div id=wide style="grid-column: 1 / 3">w</div>
            <div id=next>n</div>
            <div id=full style="grid-column: 1 / -1">f</div>
            <div id=span style="grid-column: span 2; grid-row: 4">s</div>
            <div id=last style="grid-row: 3; grid-column: 3">l</div>
            """);
        Assert.AreEqual(200, ById(root, "wide").Width, Tolerance);
        Assert.AreEqual(200, ById(root, "next").X, Tolerance);
        Assert.AreEqual(300, ById(root, "full").Width, Tolerance, "line -1 is the last explicit line");
        Assert.AreEqual(200, ById(root, "span").Width, Tolerance);
        Assert.AreEqual(3 * line, ById(root, "span").Y, Tolerance);
        Assert.AreEqual(200, ById(root, "last").X, Tolerance);
        Assert.AreEqual(2 * line, ById(root, "last").Y, Tolerance);
    }

    [TestMethod]
    public void PlacesNamedAreas()
    {
        BoxFragment root = Grid(
            "width: 300px; grid-template-columns: 100px 1fr; grid-template-areas: 'head head' 'side main'",
            "<div id=main style=\"grid-area: main\">m</div><div id=head style=\"grid-area: head\">h</div><div id=side style=\"grid-area: side\">s</div>");
        BoxFragment head = ById(root, "head"), side = ById(root, "side"), main = ById(root, "main");
        Assert.AreEqual(300, head.Width, Tolerance);
        Assert.AreEqual(0, head.Y, Tolerance);
        Assert.AreEqual(head.Bottom, side.Y, Tolerance);
        Assert.AreEqual(100, side.Width, Tolerance);
        Assert.AreEqual(100, main.X, Tolerance);
        Assert.AreEqual(side.Y, main.Y, Tolerance);
    }

    [TestMethod]
    public void SizesRowsAndAlignsItems()
    {
        BoxFragment root = Grid("width: 200px; grid-template-columns: 1fr 1fr", "<div id=a>x</div><div id=b style=\"height: 60px\">y</div>");
        Assert.AreEqual(60, ById(root, "a").Height, Tolerance, "items stretch to the row by default");

        double line = NormalLineHeight(PdfStandardFont.TimesRoman, 16);
        root = Grid("width: 200px; grid-template-columns: 1fr 1fr; align-items: center; justify-items: end", "<div id=a>x</div><div id=b style=\"height: 60px\">y</div>");
        BoxFragment a = ById(root, "a");
        Assert.AreEqual((60 - line) / 2, a.Y, Tolerance);
        Assert.AreEqual(100 - W("x"), a.X, Tolerance, "justify-items: end shrinks the item to its content on the right");

        root = Grid("width: 200px; grid-template-columns: 1fr; grid-template-rows: 40px; grid-auto-rows: 25px; row-gap: 5px", Cells(3));
        Assert.AreEqual(40, ById(root, "i1").Height, Tolerance);
        Assert.AreEqual(45, ById(root, "i2").Y, Tolerance);
        Assert.AreEqual(25, ById(root, "i3").Height, Tolerance, "implicit rows follow grid-auto-rows");
        Assert.AreEqual(100, ById(root, "g").Height, Tolerance);
    }

    [TestMethod]
    public void PacksDenseAndFlowsByColumns()
    {
        // Two spans of two in three 100px columns: the second wraps, leaving a hole after the first.
        const string Items = "<div id=a style=\"grid-column: span 2\">a</div><div id=b style=\"grid-column: span 2\">b</div><div id=c>c</div>";
        BoxFragment sparse = Grid("width: 300px; grid-template-columns: repeat(3, 100px); grid-auto-rows: 30px", Items);
        Assert.AreEqual(30, ById(sparse, "b").Y, Tolerance);
        Assert.AreEqual(200, ById(sparse, "c").X, Tolerance, "sparse: the cursor never goes back");
        Assert.AreEqual(30, ById(sparse, "c").Y, Tolerance);

        BoxFragment dense = Grid("width: 300px; grid-template-columns: repeat(3, 100px); grid-auto-rows: 30px; grid-auto-flow: dense", Items);
        Assert.AreEqual(30, ById(dense, "b").Y, Tolerance);
        Assert.AreEqual(200, ById(dense, "c").X, Tolerance);
        Assert.AreEqual(0, ById(dense, "c").Y, Tolerance, "dense: c fills the hole in the first row");

        // A definite column behind the cursor moves it to the next row, and later items follow it.
        BoxFragment behind = Grid("width: 300px; grid-template-columns: repeat(3, 100px); grid-auto-rows: 30px",
            "<div id=a>a</div><div id=b>b</div><div id=c style=\"grid-column: 1\">c</div><div id=d>d</div>");
        Assert.AreEqual(30, ById(behind, "c").Y, Tolerance);
        Assert.AreEqual(100, ById(behind, "d").X, Tolerance);
        Assert.AreEqual(30, ById(behind, "d").Y, Tolerance);

        // Column flow fills each column top to bottom, adding 50px columns as it goes.
        BoxFragment columns = Grid("width: 300px; grid-template-rows: 30px 30px; grid-auto-flow: column; grid-auto-columns: 50px", Cells(5));
        Assert.AreEqual(0, ById(columns, "i2").X, Tolerance);
        Assert.AreEqual(30, ById(columns, "i2").Y, Tolerance);
        Assert.AreEqual(50, ById(columns, "i3").X, Tolerance);
        Assert.AreEqual(0, ById(columns, "i3").Y, Tolerance);
        Assert.AreEqual(100, ById(columns, "i5").X, Tolerance);
        Assert.AreEqual(50, ById(columns, "i5").Width, Tolerance);
        Assert.AreEqual(60, ById(columns, "g").Height, Tolerance);

        const string Tall = "<div id=a style=\"grid-row: span 2\">a</div><div id=b style=\"grid-row: span 2\">b</div><div id=c>c</div>";
        BoxFragment columnSparse = Grid("width: 300px; grid-template-rows: repeat(3, 30px); grid-auto-flow: column; grid-auto-columns: 50px", Tall);
        Assert.AreEqual(50, ById(columnSparse, "b").X, Tolerance);
        Assert.AreEqual(50, ById(columnSparse, "c").X, Tolerance);
        Assert.AreEqual(60, ById(columnSparse, "c").Y, Tolerance);

        BoxFragment columnDense = Grid("width: 300px; grid-template-rows: repeat(3, 30px); grid-auto-flow: column dense; grid-auto-columns: 50px", Tall);
        Assert.AreEqual(0, ById(columnDense, "c").X, Tolerance, "column dense: c fills the hole under a");
        Assert.AreEqual(60, ById(columnDense, "c").Y, Tolerance);
    }

    [TestMethod]
    public void PlacesByLineNames()
    {
        BoxFragment root = Grid(
            "width: 300px; grid-template-columns: [full-start] 50px [content-start] 1fr [content-end] 50px [full-end]",
            "<div id=a style=\"grid-column: content\">a</div><div id=b style=\"grid-column: full-start / content-end\">b</div><div id=c style=\"grid-column: content-end / full\">c</div>");
        Assert.AreEqual(50, ById(root, "a").X, Tolerance, "a name alone finds name-start and name-end");
        Assert.AreEqual(200, ById(root, "a").Width, Tolerance);
        Assert.AreEqual(0, ById(root, "b").X, Tolerance);
        Assert.AreEqual(250, ById(root, "b").Width, Tolerance);
        Assert.AreEqual(250, ById(root, "c").X, Tolerance);
        Assert.AreEqual(50, ById(root, "c").Width, Tolerance);
        Assert.AreEqual(ById(root, "b").Y, ById(root, "c").Y, Tolerance, "c fits beside b in the same row");

        // Names inside repeat() repeat with the tracks; a count picks the nth, negative from the end.
        root = Grid(
            "width: 300px; grid-template-columns: repeat(3, [col] 100px) [end]",
            "<div id=a style=\"grid-column: col 2 / span col\">a</div><div id=b style=\"grid-column: col -1\">b</div><div id=c style=\"grid-column: span 2 / end\">c</div>");
        Assert.AreEqual(100, ById(root, "a").X, Tolerance);
        Assert.AreEqual(100, ById(root, "a").Width, Tolerance, "span col ends at the next line named col");
        Assert.AreEqual(200, ById(root, "b").X, Tolerance);
        Assert.AreEqual(100, ById(root, "c").X, Tolerance);
        Assert.AreEqual(200, ById(root, "c").Width, Tolerance);

        // Lines named x-start and x-end make an area x; areas name their edge lines the same way.
        root = Grid(
            "width: 300px; grid-template-columns: [side-start] 80px [side-end main-start] 1fr [main-end]; grid-template-areas: 'x x'",
            "<div id=m style=\"grid-column: main\">m</div><div id=s style=\"grid-column: x-start / side-end\">s</div>");
        Assert.AreEqual(80, ById(root, "m").X, Tolerance);
        Assert.AreEqual(220, ById(root, "m").Width, Tolerance);
        Assert.AreEqual(0, ById(root, "s").X, Tolerance);
        Assert.AreEqual(80, ById(root, "s").Width, Tolerance);

        // A name no line has: every implicit line counts as having it, so the item starts at the
        // first line after the explicit grid, line 4, leaving the implicit column 3 empty.
        root = Grid("width: 300px; grid-template-columns: 100px 100px; grid-auto-columns: 40px", "<div id=a style=\"grid-column: nothere\">a</div>");
        Assert.AreEqual(240, ById(root, "a").X, Tolerance);
        Assert.AreEqual(40, ById(root, "a").Width, Tolerance);

        // Line -4 of a two column grid lies before line 1: an implicit column is added there.
        root = Grid("width: 300px; grid-template-columns: 100px 100px; grid-auto-columns: 30px", "<div id=a style=\"grid-column: -4\">a</div><div id=b>b</div>");
        Assert.AreEqual(0, ById(root, "a").X, Tolerance);
        Assert.AreEqual(30, ById(root, "a").Width, Tolerance);
        Assert.AreEqual(30, ById(root, "b").X, Tolerance);
        Assert.AreEqual(100, ById(root, "b").Width, Tolerance);
    }

    [TestMethod]
    public void SubgridsShareTheParentColumns()
    {
        // Columns: auto auto 1fr in 400px with 10px gaps. The subgrid's items size the parent's
        // columns, plus its 5px padding on the outer ones: 65 (60 + 5), 50, then 400 - 115 - 20 = 265.
        BoxFragment root = Layout("""
            <div id=g style="display: grid; width: 400px; grid-template-columns: auto auto 1fr; column-gap: 10px">
              <div id=p1 style="width: 40px">a</div><div id=p2 style="width: 30px">b</div><div id=p3>c</div>
              <div id=s style="grid-column: 1 / -1; display: grid; grid-template-columns: subgrid; padding: 0 5px">
                <div id=s1 style="width: 60px">d</div><div id=s2 style="width: 50px">e</div><div id=s3>f</div>
              </div>
            </div>
            """, Bare);
        Assert.AreEqual(75, ById(root, "p2").X, Tolerance);
        Assert.AreEqual(135, ById(root, "p3").X, Tolerance);
        Assert.AreEqual(400, ById(root, "s").Width, Tolerance);
        Assert.AreEqual(5, ById(root, "s1").X, Tolerance, "the first item starts at the subgrid's content edge");
        Assert.AreEqual(75, ById(root, "s2").X, Tolerance, "the subgrid's items line up with the parent's");
        Assert.AreEqual(135, ById(root, "s3").X, Tolerance);
        Assert.AreEqual(260, ById(root, "s3").Width, Tolerance, "the last item stops at the subgrid's content edge");

        // A subgrid gap of 20px against the parent's 10px: half the difference (5px) on each side
        // of its inner gaps. Columns: 65 (60 + 5), 60 (50 + 5 + 5), 255.
        root = Layout("""
            <div id=g style="display: grid; width: 400px; grid-template-columns: auto auto 1fr; column-gap: 10px">
              <div id=p1 style="width: 40px">a</div><div id=p2 style="width: 30px">b</div><div id=p3>c</div>
              <div id=s style="grid-column: 1 / -1; display: grid; grid-template-columns: subgrid; column-gap: 20px">
                <div id=s1 style="width: 60px">d</div><div id=s2 style="width: 50px">e</div><div id=s3>f</div>
              </div>
            </div>
            """, Bare);
        Assert.AreEqual(75, ById(root, "p2").X, Tolerance);
        Assert.AreEqual(145, ById(root, "p3").X, Tolerance);
        Assert.AreEqual(0, ById(root, "s1").X, Tolerance);
        Assert.AreEqual(80, ById(root, "s2").X, Tolerance);
        Assert.AreEqual(150, ById(root, "s3").X, Tolerance);
        Assert.AreEqual(250, ById(root, "s3").Width, Tolerance);
    }

    [TestMethod]
    public void SubgridsShareTheParentRows()
    {
        // Two cards of three rows each: the rows take the taller item of each pair, 30, 50 and 15.
        BoxFragment root = Layout("""
            <div id=g style="display: grid; width: 200px; grid-template-columns: 1fr 1fr">
              <div id=a style="grid-row: span 3; display: grid; grid-template-rows: subgrid">
                <div id=ah style="height: 20px"></div><div id=ab style="height: 50px"></div><div id=af style="height: 10px"></div>
              </div>
              <div id=b style="grid-row: span 3; display: grid; grid-template-rows: subgrid">
                <div id=bh style="height: 30px"></div><div id=bb style="height: 20px"></div><div id=bf style="height: 15px"></div>
              </div>
            </div>
            """, Bare);
        Assert.AreEqual(100, ById(root, "b").X, Tolerance);
        Assert.AreEqual(30, ById(root, "ab").Y, Tolerance);
        Assert.AreEqual(30, ById(root, "bb").Y, Tolerance);
        Assert.AreEqual(80, ById(root, "af").Y, Tolerance);
        Assert.AreEqual(80, ById(root, "bf").Y, Tolerance);
        Assert.AreEqual(95, ById(root, "a").Height, Tolerance, "a subgrid fills the rows it spans");
        Assert.AreEqual(95, ById(root, "g").Height, Tolerance);

        // Nested: a subgrid inside a subgrid still sizes the outer rows, 40 and 25.
        root = Layout("""
            <div id=g style="display: grid; width: 200px; grid-template-columns: 1fr 1fr">
              <div id=a style="grid-row: span 2; display: grid; grid-template-rows: subgrid">
                <div id=inner style="grid-row: span 2; display: grid; grid-template-rows: subgrid">
                  <div id=x style="height: 40px"></div><div id=y style="height: 10px"></div>
                </div>
              </div>
              <div id=b style="grid-row: span 2; display: grid; grid-template-rows: subgrid">
                <div id=p style="height: 20px"></div><div id=q style="height: 25px"></div>
              </div>
            </div>
            """, Bare);
        Assert.AreEqual(40, ById(root, "y").Y, Tolerance);
        Assert.AreEqual(40, ById(root, "q").Y, Tolerance);
        Assert.AreEqual(65, ById(root, "g").Height, Tolerance);
    }

    [TestMethod]
    public void SubgridsSeeTheParentLineNames()
    {
        BoxFragment root = Layout("""
            <div id=g style="display: grid; width: 300px; grid-template-columns: [a] 100px [b] 100px [c] 100px [d]">
              <div id=s style="grid-column: b / d; display: grid; grid-template-columns: subgrid [x] [y]">
                <div id=i1 style="grid-column: c">1</div>
                <div id=i2 style="grid-column: x / y">2</div>
                <div id=i3 style="grid-column: a">3</div>
              </div>
              <div id=t style="display: grid; grid-template-columns: subgrid [p] [q] [r]"></div>
            </div>
            """, Bare);
        Assert.AreEqual(100, ById(root, "s").X, Tolerance);
        Assert.AreEqual(200, ById(root, "i1").X, Tolerance, "the parent's line c is the subgrid's second line");
        Assert.AreEqual(100, ById(root, "i2").X, Tolerance, "the subgrid's own names add to the parent's");
        Assert.AreEqual(100, ById(root, "i2").Width, Tolerance);
        Assert.AreEqual(200, ById(root, "i3").X, Tolerance, "a line past the subgrid is clamped to its last track");
        Assert.AreEqual(100, ById(root, "i3").Width, Tolerance);
        Assert.AreEqual(0, ById(root, "t").X, Tolerance);
        Assert.AreEqual(200, ById(root, "t").Width, Tolerance, "without a span, a subgrid spans the tracks its name list describes");
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void DeepSubgridsStayLinear()
    {
        // Every level shares both axes with its parent: each is measured once and laid out once.
        const int Depth = 300;
        string open = "<div style=\"display: grid; grid-template-columns: subgrid; grid-template-rows: subgrid; grid-column: span 2; grid-row: span 2\">";
        string html = "<div style=\"display: grid; grid-template-columns: 1fr 1fr\">"
            + string.Concat(Enumerable.Repeat(open, Depth)) + "<p>x</p><p>y</p>" + string.Concat(Enumerable.Repeat("</div>", Depth)) + "</div>";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        Assert.IsLessThan(30_000, clock.ElapsedMilliseconds);
        Assert.HasCount(1, doc.Pages);
    }

    [TestMethod]
    public void ReadsTheGridShorthands()
    {
        // grid-template: rows of area strings with their sizes and line names, then the columns.
        BoxFragment root = Grid(
            "width: 300px; grid-template: [top] 'head head' 30px [body-start] 'side main' / 100px 1fr",
            "<div id=h style=\"grid-area: head\">h</div><div id=m style=\"grid-area: main; height: 50px\">m</div><div id=s style=\"grid-row: body-start; grid-column: 1\">s</div>");
        Assert.AreEqual(300, ById(root, "h").Width, Tolerance);
        Assert.AreEqual(30, ById(root, "h").Height, Tolerance);
        Assert.AreEqual(100, ById(root, "m").X, Tolerance);
        Assert.AreEqual(30, ById(root, "m").Y, Tolerance);
        Assert.AreEqual(0, ById(root, "s").X, Tolerance);
        Assert.AreEqual(30, ById(root, "s").Y, Tolerance);
        Assert.AreEqual(50, ById(root, "s").Height, Tolerance, "a row without a size is auto");

        root = Grid("width: 300px; grid-template: 20px 40px / 1fr 2fr", Cells(4));
        Assert.AreEqual(100, ById(root, "i2").X, Tolerance);
        Assert.AreEqual(20, ById(root, "i3").Y, Tolerance);
        Assert.AreEqual(40, ById(root, "i3").Height, Tolerance);

        // grid with auto-flow: the side it is on sets the flow and the implicit track size.
        root = Grid("width: 300px; grid: auto-flow dense 40px / repeat(3, 100px)",
            "<div id=a style=\"grid-column: span 2\">a</div><div id=b style=\"grid-column: span 2\">b</div><div id=c>c</div>");
        Assert.AreEqual(200, ById(root, "c").X, Tolerance);
        Assert.AreEqual(0, ById(root, "c").Y, Tolerance);
        Assert.AreEqual(40, ById(root, "b").Y, Tolerance);

        root = Grid("width: 300px; grid: 30px 30px / auto-flow 50px", Cells(3));
        Assert.AreEqual(30, ById(root, "i2").Y, Tolerance);
        Assert.AreEqual(50, ById(root, "i3").X, Tolerance);
        Assert.AreEqual(50, ById(root, "i3").Width, Tolerance);
    }

    [TestMethod]
    public void CollapsesEmptyAutoFitTracks()
    {
        // Four 100px+ tracks fit in 430px with 10px gaps. auto-fill keeps the two empty ones;
        // auto-fit collapses them with their gaps, and the two items share the width.
        BoxFragment root = Grid("width: 430px; column-gap: 10px; grid-template-columns: repeat(auto-fill, minmax(100px, 1fr))", Cells(2));
        Assert.AreEqual(100, ById(root, "i1").Width, Tolerance);
        Assert.AreEqual(110, ById(root, "i2").X, Tolerance);

        root = Grid("width: 430px; column-gap: 10px; grid-template-columns: repeat(auto-fit, minmax(100px, 1fr))", Cells(2));
        Assert.AreEqual(210, ById(root, "i1").Width, Tolerance);
        Assert.AreEqual(220, ById(root, "i2").X, Tolerance);
        Assert.AreEqual(210, ById(root, "i2").Width, Tolerance);

        // A full row keeps every track.
        root = Grid("width: 430px; column-gap: 10px; grid-template-columns: repeat(auto-fit, minmax(100px, 1fr))", Cells(5));
        Assert.AreEqual(100, ById(root, "i1").Width, Tolerance);
        Assert.AreEqual(0, ById(root, "i5").X, Tolerance);

        // The lines after collapsed tracks move back with them.
        root = Grid("width: 400px; grid-template-columns: repeat(auto-fit, 100px) [end]; grid-auto-columns: 50px", "<div id=a>a</div><div id=c style=\"grid-column: end\">c</div>");
        Assert.AreEqual(100, ById(root, "c").X, Tolerance);
        Assert.AreEqual(50, ById(root, "c").Width, Tolerance);
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void NestedRepeatsAreIgnoredInLinearTime()
    {
        // repeat() is not allowed inside repeat(); deep nesting must not cost time doubling per level.
        string nested = string.Concat(Enumerable.Repeat("repeat(2, ", 40)) + "10px" + new string(')', 40);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        BoxFragment root = Grid($"width: 300px; grid-template-columns: {nested} 20px", "<div>a</div><div>b</div>");
        Assert.IsLessThan(5_000, clock.ElapsedMilliseconds);
        Assert.IsNotNull(ById(root, "g"));
    }

    [TestMethod]
    [DoNotParallelize] // timing: parallel tests would slow the measured code on a small runner
    public void HostileSpansStayBounded()
    {
        // Large spans on many items, some fixed and some placed automatically, in a narrow grid.
        string items = string.Concat(Enumerable.Range(0, 3000).Select(i => i % 2 == 0
            ? "<div style=\"grid-row: span 1000; grid-column: span 1000\">x</div>"
            : $"<div style=\"grid-row: {(i % 900) + 1}; grid-column: span 3\">y</div>"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        BoxFragment root = Grid("width: 300px; grid-template-columns: repeat(3, 1fr)", items);
        Assert.IsLessThan(30_000, clock.ElapsedMilliseconds);
        Assert.IsNotNull(ById(root, "g"));

        // The same items in dense column flow, where every search starts again from the first cell.
        string swapped = items.Replace("grid-row", "grid-tmp").Replace("grid-column", "grid-row").Replace("grid-tmp", "grid-column");
        clock.Restart();
        root = Grid("width: 300px; grid-template-rows: repeat(3, 10px); grid-auto-flow: column dense", swapped);
        Assert.IsLessThan(30_000, clock.ElapsedMilliseconds);
        Assert.IsNotNull(ById(root, "g"));

        // Huge line numbers, names that never match and named spans past every line.
        string named = string.Concat(Enumerable.Range(0, 2000).Select(i => i % 2 == 0
            ? "<div style=\"grid-column: -100000 x; grid-row: span x / nope\">z</div>"
            : "<div style=\"grid-column: x / span 100000 y; grid-row: nope / span 3 x\">z</div>"));
        clock.Restart();
        root = Grid("width: 300px; grid-template-columns: repeat(1000, [x] 1px)", named);
        Assert.IsLessThan(30_000, clock.ElapsedMilliseconds);
        Assert.IsNotNull(ById(root, "g"));
    }

    [TestMethod]
    public void ShrinksInlineGridsAndBreaksBetweenRows()
    {
        BoxFragment root = Layout("<p><span id=g style=\"display: inline-grid; grid-template-columns: auto auto; column-gap: 4px\"><b>ab</b><i>cd</i></span></p>", Bare);
        double expected = Measure(PdfStandardFont.TimesBold, "ab", 16) + 4 + Measure(PdfStandardFont.TimesItalic, "cd", 16);
        Assert.AreEqual(expected, ById(root, "g").Width, Tolerance);

        string cards = string.Concat(Enumerable.Range(1, 60).Select(i => $"<div style=\"height: 60px; border: 1px solid\">Card {i}</div>"));
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf($"<div style=\"display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 8px\">{cards}</div>", new HtmlConverterOptions { UseSystemFonts = false }));
        Assert.IsGreaterThan(1, doc.Pages.Count);
        var all = new System.Text.StringBuilder();
        for (int page = 0; page < doc.Pages.Count; page++)
        {
            PdfNetStack.Objects.PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
            IEnumerable<PdfNetStack.Objects.PdfObject> parts = contents is PdfNetStack.Objects.PdfArray arr ? arr : [contents!];
            foreach (PdfNetStack.Objects.PdfObject part in parts) all.Append(System.Text.Encoding.ASCII.GetString(doc.GetStreamData((PdfNetStack.Objects.PdfStream)doc.Resolve(part)!))).Append('|');
        }

        string text = string.Concat(Regex.Matches(all.ToString(), @"\(((?:\\.|[^\\)])*)\)").Cast<Match>().Select(m => m.Groups[1].Value + " "));
        for (int i = 1; i <= 60; i++) Assert.HasCount(1, Regex.Matches(text, $"Card {i} "), $"card {i} is drawn once: pages break between grid rows");
    }
}
