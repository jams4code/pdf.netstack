using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Drawing;
using PdfNetStack.Html;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Layout;
using PdfNetStack.Objects;
using static PdfNetStack.Tests.HtmlLayoutFixture;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class PaintTests
{
    private static readonly string Png = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "rgb8.png")));

    /// <summary>The first page's content stream, decoded.</summary>
    private static string Content(string html)
    {
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        PdfObject? contents = doc.Resolve(doc.Pages[0].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.GetEncoding("ISO-8859-1").GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    // Regex.Count is not on .NET Framework, which the tests also run on.
    private static int Count(string content, string pattern) => Regex.Matches(content, pattern).Cast<Match>().Count();

    [TestMethod]
    public void ScalesOverlappingRadii()
    {
        BoxFragment root = Layout("<div id=a style=\"width: 100px; height: 50px; border-radius: 50%\"></div><div id=b style=\"width: 100px; height: 100px; border-radius: 80px 80px 0 0\"></div>");
        PdfCornerRadii a = Painter.Radii(ById(root, "a").Style, 100, 50);
        Assert.AreEqual(50, a.TopLeftX, Tolerance);
        Assert.AreEqual(25, a.TopLeftY, Tolerance, "a percentage is of the box's own side");
        PdfCornerRadii b = Painter.Radii(ById(root, "b").Style, 100, 100);
        Assert.AreEqual(50, b.TopLeftX, Tolerance, "radii that would overlap scale down together");
        Assert.AreEqual(50, b.TopRightX, Tolerance);
        Assert.AreEqual(0, b.BottomLeftX, Tolerance);
    }

    [TestMethod]
    [DataRow("red, blue", "0 1")]
    [DataRow("red, blue 30%, green, yellow", "0 0.3 0.65 1")]
    [DataRow("red 20%, blue 10%", "0.2 0.2")]
    [DataRow("red 50px, blue", "0.5 1")]
    [DataRow("red 0 50%, blue 50% 100%", "0 0.5 0.5 1")]
    public void FixesUpColorStops(string stops, string expected)
    {
        List<PdfGradientStop> fixedUp = Painter.Stops(Painter.Layers(CssParser.ComponentValues(CssTokenizer.Tokenize(stops))), 100, 1)!;
        Assert.AreEqual(expected, string.Join(" ", fixedUp.Select(s => Math.Round(s.Offset, 4).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [TestMethod]
    public void DrawsRoundedCornersAndRings()
    {
        string content = Content("<div style=\"width: 100px; height: 40px; border: 3px solid rgb(255, 0, 0); border-radius: 10px; background: rgb(0, 0, 255)\"></div>");
        Assert.IsGreaterThanOrEqualTo(8, Count(content, @" c\n"), "rounded corners are Bezier curves");
        StringAssert.Matches(content, new Regex(@"W\*\s+n"), "the border ring clips with the even-odd rule");
        StringAssert.Contains(content, "0 0 1 rg");
        StringAssert.Contains(content, "1 0 0 rg");
    }

    [TestMethod]
    public void PaintsGradientsShadowsAndTransforms()
    {
        string gradient = Content("<div style=\"height: 40px; background: linear-gradient(to right, red, blue)\"></div>");
        StringAssert.Contains(gradient, " sh", "a gradient paints with a shading");

        string radial = Content("<div style=\"height: 40px; background: radial-gradient(circle, white, black)\"></div>");
        StringAssert.Contains(radial, " sh");

        string shadow = Content("<div style=\"height: 40px; box-shadow: 2px 2px 6px rgb(0, 0, 0)\"></div>");
        Assert.IsGreaterThanOrEqualTo(8, Count(shadow, @"\nf\n"), "a blurred shadow is a stack of translucent layers, the outer ones rounded by the blur");
        StringAssert.Contains(shadow, " gs", "with an alpha state");

        string rotated = Content("<div style=\"width: 50px; height: 20px; transform: rotate(90deg)\">x</div>");
        StringAssert.Matches(rotated, new Regex(@"(^|\s)0 1 -1 0 [-0-9.]+ [-0-9.]+ cm"), "rotate(90deg) is matrix(0, 1, -1, 0)");
    }

    [TestMethod]
    public void TilesBackgroundImagesWithinLimits()
    {
        string tiled = Content($"<div style=\"width: 60px; height: 40px; background: url({Png}) repeat; background-size: 20px 20px\"></div>");
        Assert.AreEqual(6, Count(tiled, @" Do\n"), "three columns of two tiles");

        string once = Content($"<div style=\"width: 60px; height: 40px; background: url({Png}) no-repeat center\"></div>");
        Assert.AreEqual(1, Count(once, @" Do\n"));

        string capped = Content($"<div style=\"width: 2000px; height: 2000px; background: url({Png}); background-size: 1px 1px\"></div>");
        Assert.IsLessThanOrEqualTo(4096, Count(capped, @" Do\n"), "a pathological tiling is capped");
    }

    [TestMethod]
    public void FitsImagesAndDrawsOutlines()
    {
        string cover = Content($"<img src=\"{Png}\" style=\"width: 100px; height: 20px; object-fit: cover\">");
        StringAssert.Matches(cover, new Regex(@"re\s+W\s+n\s+[^Q]*Do"), "cover clips the image to its box");
        Assert.AreEqual(1, Count(cover, @" Do\n"));

        string outline = Content("<div style=\"outline: 2px solid rgb(0, 128, 0)\">x</div>");
        StringAssert.Matches(outline, new Regex(@"0 \.50\d* 0 RG"), "the outline strokes in its color");
    }
}
