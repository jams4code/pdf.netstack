using PdfNetStack.Html;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Tests;

/// <summary>
/// Absurd sizes in untrusted HTML: computed lengths are clamped to finite ranges, and pagination
/// stops at <see cref="HtmlConverterOptions.MaxPages"/> with <see cref="HtmlConversionException"/>,
/// so no style value makes a conversion allocate without bound.
/// </summary>
[TestClass]
public sealed class HtmlLimitsTests
{
    private static readonly HtmlConverterOptions Standard = new() { UseSystemFonts = false };

    /// <summary>The computed style of the element with id x.</summary>
    private static ComputedStyle Style(string declaration)
    {
        HtmlDocument doc = HtmlParser.Parse($"<div><p id=x style=\"{declaration}\">H</p></div>");
        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddDocument(doc);
        var styles = new Dictionary<HtmlElement, ComputedStyle>();
        foreach (HtmlElement e in doc.Descendants()) styles[e] = resolver.Compute(e, e.ParentElement is HtmlElement p ? styles[p] : null);
        return styles.Single(s => s.Key.GetAttribute("id") == "x").Value;
    }

    private static string Value(ComputedStyle s, string property) => string.Concat(s[property].Select(v => v.ToString()));

    [TestMethod]
    public void NumbersTooLargeForADoubleAreInfinite()
    {
        // The same on every runtime, so the clamps below see the same value.
        Assert.AreEqual(double.PositiveInfinity, CssTokenizer.Tokenize("1e309")[0].Number);
        Assert.AreEqual(double.NegativeInfinity, CssTokenizer.Tokenize("-1e309px")[0].Number);
        Assert.AreEqual(0, CssTokenizer.Tokenize("1e-400")[0].Number);
    }

    [TestMethod]
    public void LengthsAreClampedToFiniteRanges()
    {
        // Font sizes stop at 10000px, as in browsers.
        Assert.AreEqual(10_000, Style("font-size: 1e300px").FontSize);
        Assert.AreEqual(10_000, Style("font-size: 1e309px").FontSize);
        Assert.AreEqual(10_000, Style("font-size: calc(1e308px * 10)").FontSize);
        Assert.AreEqual(10_000, Style("font-size: 1e300%").FontSize);
        Assert.AreEqual(10_000, Style("font-size: 1e300em").FontSize);
        Assert.AreEqual(24, Style("font-size: 24px").FontSize);

        // Other lengths stop at a million px either way.
        Assert.AreEqual("1000000px", Value(Style("margin-top: 1e300px"), "margin-top"));
        Assert.AreEqual("-1000000px", Value(Style("margin-left: -1e309px"), "margin-left"));
        Assert.AreEqual("1000000px", Value(Style("padding-top: 1e300in"), "padding-top"));
        Assert.AreEqual("1000000px", Value(Style("border-top-width: 1e300px; border-top-style: solid"), "border-top-width"));
        Assert.AreEqual("1000000px", Value(Style("letter-spacing: 1e300px"), "letter-spacing"));
        Assert.AreEqual("1000000px", Value(Style("word-spacing: 1e300px"), "word-spacing"));
        Assert.AreEqual("1000000%", Value(Style("width: 1e300%"), "width"));
        Assert.AreEqual("1000000px", Value(Style("width: calc(1e308px * 10)"), "width"));
        Assert.AreEqual("1000000px", Value(Style("line-height: 1e300px"), "line-height"));
        Assert.AreEqual("12.5px", Value(Style("margin-top: 12.5px"), "margin-top"));

        // Unitless line heights stop at 1000.
        Assert.AreEqual("1000", Value(Style("line-height: 1e300"), "line-height"));
        Assert.AreEqual("1.5", Value(Style("line-height: 1.5"), "line-height"));
    }

    [TestMethod]
    [DataRow("font-size: 1e300px")]
    [DataRow("font-size: 1e309px")]
    [DataRow("font-size: calc(1e308px * 10)")]
    [DataRow("font-size: 1e300em")]
    [DataRow("line-height: 1e300")]
    [DataRow("line-height: 1e300px")]
    [DataRow("margin-top: 1e300px")]
    [DataRow("padding-top: 1e300px")]
    [DataRow("border-top: 1e300px solid")]
    [DataRow("letter-spacing: 1e300px")]
    [DataRow("word-spacing: 1e300px")]
    [DataRow("width: 1e300px")]
    [DataRow("display: inline-block; height: 1e300px")]
    public void HostileSizesConvertWithinTheLimits(string declaration)
    {
        // Each of these once ran pagination out of memory; now the page count is bounded.
        byte[] pdf = HtmlConverter.ConvertToPdf($"<p style=\"{declaration}\">Hello</p><p>after</p>", Standard);
        PdfDocument doc = PdfDocument.Open(pdf);
        Assert.IsLessThanOrEqualTo(2000, doc.Pages.Count, declaration);
        Assert.IsTrue(doc.Pages.All(p => p.Width <= 14400 && p.Height <= 14400));
    }

    [TestMethod]
    public void PageSizesStayWithinPdfLimits()
    {
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf("<style>@page { size: 1e300px 1e309px; margin: 0 }</style><p>x</p>", Standard));
        Assert.AreEqual(14400, doc.Pages[0].Width, 0.01);
        Assert.AreEqual(14400, doc.Pages[0].Height, 0.01);
    }

    [TestMethod]
    public void TooManyPagesEndWithAClearException()
    {
        string breaks = string.Concat(Enumerable.Range(0, 12).Select(i => $"<p style=\"break-before: page\">{i}</p>"));
        Assert.HasCount(12, PdfDocument.Open(HtmlConverter.ConvertToPdf(breaks, new HtmlConverterOptions { UseSystemFonts = false, MaxPages = 12 })).Pages);
        var e = Assert.ThrowsExactly<HtmlConversionException>(() => HtmlConverter.ConvertToPdf(breaks, new HtmlConverterOptions { UseSystemFonts = false, MaxPages = 11 }));
        StringAssert.Contains(e.Message, "MaxPages");

        // A tall box counts the same, and named page runs share the one limit.
        Assert.ThrowsExactly<HtmlConversionException>(() => HtmlConverter.ConvertToPdf("<p style=\"margin-top: 1e300px\">x</p>", new HtmlConverterOptions { UseSystemFonts = false, MaxPages = 100 }));
        string named = "<style>.w { page: wide } @page wide { size: A4 landscape }</style>" + string.Concat(Enumerable.Range(0, 6).Select(i => $"<p class=w>{i}</p><p style=\"break-before: page\">{i}</p>"));
        Assert.ThrowsExactly<HtmlConversionException>(() => HtmlConverter.ConvertToPdf(named, new HtmlConverterOptions { UseSystemFonts = false, MaxPages = 5 }));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HtmlConverterOptions { MaxPages = 0 });
    }
}
