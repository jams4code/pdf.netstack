using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Lays out HTML for the layout tests and finds fragments in the result.</summary>
internal static class HtmlLayoutFixture
{
    public const double Tolerance = 1e-6;

    private static readonly string ImageFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "rgb8.png");

    /// <summary>Standard fonts only, so the expected numbers follow from the AFM metrics on any machine. img.png loads a fixture.</summary>
    public static BoxFragment Layout(string html, string? css = null, double width = 600)
    {
        HtmlDocument doc = HtmlParser.Parse(html);
        var resolver = new StyleResolver(new CssMedia(width, 1000));
        if (css is not null) resolver.AddStyleSheet(css, CssOrigin.Author);
        resolver.AddDocument(doc);
        BlockBox root = new BoxBuilder(resolver, src => src == "img.png" ? new HtmlImage(PdfImage.Load(ImageFixture)) : null).Build(doc);
        return new LayoutEngine(new FontResolver(null, useSystemFonts: false)).Layout(root, width);
    }

    /// <summary>Every fragment, depth first: boxes, markers, lines and line items.</summary>
    public static IEnumerable<Fragment> All(Fragment f)
    {
        yield return f;
        IEnumerable<Fragment> children = f switch
        {
            BoxFragment b => b.Children.Concat(b.Marker is null ? [] : new Fragment[] { b.Marker }),
            LineFragment l => l.Items,
            _ => [],
        };
        foreach (Fragment c in children)
        {
            foreach (Fragment d in All(c)) yield return d;
        }
    }

    /// <summary>The box fragment of the element with an id: the table itself rather than its wrapper.</summary>
    public static BoxFragment ById(BoxFragment root, string id) =>
        All(root).OfType<BoxFragment>().First(f => f.Box.Element?.GetAttribute("id") == id && f.Box is not InlineBox and not TableWrapperBox);

    public static List<LineFragment> Lines(BoxFragment f) => f.Children.OfType<LineFragment>().ToList();

    public static double NormalLineHeight(PdfFont font, double size) => (font.Ascent + font.Descent + font.LineGap) * size;

    public static double Measure(PdfFont font, string text, double size) => font.MeasureString(text, size);
}
