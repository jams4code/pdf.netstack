using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Drawing;
using PdfNetStack.Drawing.Svg;
using PdfNetStack.Html;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class SvgTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    /// <summary>The content of the form an SVG becomes, decoded.</summary>
    private static string Draw(string svg)
    {
        PdfSvg drawing = PdfSvg.Parse(svg);
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics()) g.DrawSvg(drawing, 10, 10);
        doc = PdfDocument.Open(doc.ToArray());
        var dict = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        var xobjects = (PdfDictionary)doc.Resolve(dict["XObject"])!;
        var form = (PdfStream)doc.Resolve(xobjects.First().Value)!;
        return Encoding.GetEncoding("ISO-8859-1").GetString(doc.GetStreamData(form));
    }

    private static int Count(string content, string pattern) => Regex.Matches(content, pattern).Cast<Match>().Count();

    [TestMethod]
    public void SizesFromWidthHeightAndViewBox()
    {
        PdfSvg sized = PdfSvg.Parse($"<svg {Ns} width=\"200\" height=\"100\"/>");
        Assert.AreEqual(150, sized.Width, 1e-9, "200 CSS px are 150 points");
        Assert.AreEqual(75, sized.Height, 1e-9);
        PdfSvg units = PdfSvg.Parse($"<svg {Ns} width=\"2in\" height=\"1cm\"/>");
        Assert.AreEqual(144, units.Width, 1e-9);
        Assert.AreEqual(72 / 2.54, units.Height, 1e-9);
        PdfSvg viewBox = PdfSvg.Parse($"<svg {Ns} viewBox=\"0 0 40 20\"/>");
        Assert.AreEqual(30, viewBox.Width, 1e-9);
        PdfSvg ratio = PdfSvg.Parse($"<svg {Ns} width=\"80\" viewBox=\"0 0 40 20\"/>");
        Assert.AreEqual(30, ratio.Height, 1e-9, "a missing height follows the viewBox ratio");
        PdfSvg fallback = PdfSvg.Parse($"<svg {Ns}/>");
        Assert.AreEqual(225, fallback.Width, 1e-9, "300 by 150 without a size");
        Assert.ThrowsExactly<FormatException>(() => PdfSvg.Parse("<html><body/></html>"));
    }

    [TestMethod]
    [DataRow("M10 10 L20 20", 2)]
    [DataRow("M10,10 20,20 30,30", 3, DisplayName = "implicit lineto after moveto")]
    [DataRow("m10 10 h10 v10 h-10 z", 4)]
    [DataRow("M0 0C1 1 2 2 3 3S5 5 6 6", 3)]
    [DataRow("M0 0Q1 1 2 2T4 4", 3)]
    [DataRow("M1.5.5L2-3", 2, DisplayName = "numbers run together")]
    [DataRow("M0 0a5 5 0 1010 0", 2, DisplayName = "arc flags without separators")]
    [DataRow("M0 0 L10 10 X 5 5", 2, DisplayName = "an error keeps what came before")]
    [DataRow("L10 10", 0, DisplayName = "a path must start with a move")]
    public void ParsesPathData(string d, int segments)
    {
        PdfPath? path = SvgPath.Parse(d);
        if (segments == 0)
        {
            Assert.IsNull(path);
            return;
        }

        (double minX, double minY, double maxX, double maxY) = SvgPath.Bounds(path!);
        Assert.IsGreaterThanOrEqualTo(minX, maxX);
        Assert.IsGreaterThanOrEqualTo(minY, maxY);
    }

    [TestMethod]
    public void ConvertsArcsToCurvesThroughTheEndpoint()
    {
        // A half circle of radius 5 from (0,0) to (10,0). The sweep flag picks the positive-angle
        // direction, clockwise on a y-down canvas, so it bulges up; the two quarter curves have their
        // control points on the tangents, at most 5 from the chord.
        PdfPath up = SvgPath.Parse("M0 0 A5 5 0 0 1 10 0")!;
        (double minX, double minY, double maxX, double maxY) = SvgPath.Bounds(up);
        Assert.AreEqual(0, minX, 1e-9);
        Assert.AreEqual(10, maxX, 1e-9);
        Assert.AreEqual(-5, minY, 1e-9);
        Assert.AreEqual(0, maxY, 1e-9);

        (_, double downMin, _, double downMax) = SvgPath.Bounds(SvgPath.Parse("M0 0 A5 5 0 0 0 10 0")!);
        Assert.AreEqual(0, downMin, 1e-9);
        Assert.AreEqual(5, downMax, 1e-9, "without the sweep flag it bulges down");
    }

    [TestMethod]
    public void ParsesTransformsAndColors()
    {
        PdfMatrix m = SvgRenderer.Transform("translate(10 20) scale(2)");
        PdfPoint p = m.Transform(new PdfPoint(1, 1));
        Assert.AreEqual(12, p.X, 1e-9, "scale applies first, then translate");
        Assert.AreEqual(22, p.Y, 1e-9);
        PdfPoint r = SvgRenderer.Transform("rotate(90 10 10)").Transform(new PdfPoint(20, 10));
        Assert.AreEqual(10, r.X, 1e-9, "rotate about a point");
        Assert.AreEqual(20, r.Y, 1e-9);

        var style = new Dictionary<string, string> { ["color"] = "#00ff00" };
        Assert.AreEqual(PdfColor.FromRgb(255, 0, 0), SvgRenderer.Color("red", style));
        Assert.AreEqual(PdfColor.FromRgb(0x11, 0x22, 0x33), SvgRenderer.Color("#123", style));
        Assert.AreEqual(PdfColor.FromRgb(0, 255, 0), SvgRenderer.Color("currentColor", style));
        Assert.AreEqual(0.5, SvgRenderer.Color("rgba(0, 0, 255, 0.5)", style)!.Value.Alpha, 1e-9);
        Assert.IsNull(SvgRenderer.Color("nonsense", style));
    }

    [TestMethod]
    public void DrawsShapesStylesAndGradients()
    {
        string content = Draw($$"""
            <svg {{Ns}} width="100" height="100">
              <style>.blue { fill: #0000ff }</style>
              <defs><linearGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient></defs>
              <rect class="blue" x="0" y="0" width="10" height="10"/>
              <circle cx="50" cy="50" r="10" fill="url(#g)"/>
              <path d="M0 0 L10 10" stroke="#ff0000" stroke-width="2" stroke-dasharray="4 2"/>
              <rect x="0" y="0" width="5" height="5" style="fill: none; stroke: black"/>
              <g display="none"><rect width="99" height="99" fill="#00ff00"/></g>
            </svg>
            """);
        StringAssert.Contains(content, "0 0 1 rg", "a class from the style element");
        StringAssert.Contains(content, " sh", "a gradient fill");
        StringAssert.Contains(content, "1 0 0 RG");
        StringAssert.Contains(content, "[4 2] 0 d");
        Assert.DoesNotContain("0 1 0 rg", content, "display: none draws nothing");
    }

    [TestMethod]
    public void RendersEachDrawingOncePerDocument()
    {
        PdfSvg logo = PdfSvg.Parse($"<svg {Ns} width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>");
        PdfDocument doc = PdfDocument.Create();
        for (int i = 0; i < 3; i++)
        {
            using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
            g.DrawSvg(logo, 10, 10);
            g.DrawSvg(logo, 100, 10, 40, 40);
        }

        doc = PdfDocument.Open(doc.ToArray());
        var forms = new HashSet<int>();
        foreach (PdfPage page in doc.Pages)
        {
            var resources = (PdfDictionary)doc.Resolve(page.Dictionary["Resources"])!;
            foreach (PdfObject x in ((PdfDictionary)doc.Resolve(resources["XObject"])!).Select(kv => kv.Value)) forms.Add(((PdfReference)x).Number);
        }

        Assert.HasCount(1, forms, "one form for six placements on three pages");
    }

    [TestMethod]
    public void ResistsHostileDocuments()
    {
        // Entities are never expanded: a billion laughs doctype is skipped.
        string laughs = "<!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>"
            + $"<svg {Ns} width=\"10\" height=\"10\"><text x=\"0\" y=\"10\">&c;&c;&c;</text></svg>";
        StringAssert.Contains(Draw(laughs), "&c;&c;&c;");

        // A use cycle, deep nesting and a huge path all end.
        var clock = Stopwatch.StartNew();
        Draw($"<svg {Ns}><g id=\"a\"><use href=\"#b\"/></g><g id=\"b\"><use href=\"#a\"/></g><use href=\"#a\"/></svg>");
        Draw($"<svg {Ns}>" + string.Concat(Enumerable.Repeat("<g>", 5000)) + "<rect width=\"1\" height=\"1\"/>" + string.Concat(Enumerable.Repeat("</g>", 5000)) + "</svg>");
        Draw($"<svg {Ns}><path d=\"M0 0" + string.Concat(Enumerable.Repeat(" l1 1", 600_000)) + "\"/></svg>");
        Draw($"<svg {Ns}><path d=\"M0 0 L1e999 5\"/><circle r=\"1e400\"/></svg>");
        Assert.IsLessThan(30_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    public void HtmlUsesSvgImagesInlineAndInBackgrounds()
    {
        string svg = $"<svg {Ns} width=\"40\" height=\"20\" viewBox=\"0 0 40 20\"><rect width=\"40\" height=\"20\" fill=\"#ff0000\"/><circle cx=\"5\" cy=\"5\" r=\"2\" fill=\"#0000ff\"/></svg>";
        string uri = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        string html = $"""
            <p><img id=a src="{uri}"></p>
            <div style="width: 80px; height: 20px; background: url({uri}) repeat-x"></div>
            <svg width="30" height="30" viewBox="0 0 30 30"><rect width="10" height="10" fill="#00ff00"/><rect x="20" width="10" height="10" fill="#00ff00"/></svg>
            """;
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        PdfObject? contents = doc.Resolve(doc.Pages[0].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        string page = string.Concat(parts.Select(p => Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
        Assert.AreEqual(4, Count(page, @" Do\n"), "the image, two background tiles and the inline drawing");

        var resources = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        Assert.HasCount(2, ((PdfDictionary)doc.Resolve(resources["XObject"])!).Select(kv => kv.Value).ToList(), "the data URI drawing once, the inline one once");

        // Self-closing elements inside inline SVG are siblings, so both green squares are drawn.
        string forms = string.Concat(((PdfDictionary)doc.Resolve(resources["XObject"])!).Select(kv => kv.Value).Select(x => Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(x)!))));
        Assert.AreEqual(2, Count(forms, @"0 1 0 rg[\s\S]*?re"), "two squares");
    }
}
