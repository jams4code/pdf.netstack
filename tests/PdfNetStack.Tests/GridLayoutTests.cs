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
