using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Html;
using PdfNetStack.Html.Css;
using PdfNetStack.Html.Dom;
using PdfNetStack.Html.Layout;
using PdfNetStack.Html.Parsing;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>
/// Variable fonts in font matching: <see cref="PdfFontCollection"/> and HTML pick the instance
/// that CSS Fonts 4 selects. Test-Axes.ttf (tools/make_variable_font_fixtures.py) has wght
/// 100..900, wdth 75..125, opsz 8..72, ital 0..1 and slnt -15..0, and each axis adds its own
/// amount to the advance of H (600 at the default), so a measured width tells the location.
/// </summary>
[TestClass]
public sealed class VariableFontMatchingTests
{
    private const string Axes = "PdfNetStack Test Axes";

    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", name);

    private static PdfFontCollection Collection(params string[] names)
    {
        var fonts = new PdfFontCollection();
        foreach (string name in names) fonts.AddFile(FixturePath(name));
        return fonts;
    }

    /// <summary>The axis values of a font: its location for an instance, the defaults for a font as loaded.</summary>
    private static Dictionary<string, double> Location(PdfOpenTypeFont font)
    {
        IReadOnlyList<PdfFontAxis> axes = font.VariationAxes;
        return axes.Select((a, i) => (a.Tag, Value: font.Location is { } l ? l[i] : a.Default)).ToDictionary(p => p.Tag, p => p.Value);
    }

    private static void AssertAt(PdfOpenTypeFont? font, string expected)
    {
        Assert.IsNotNull(font);
        Dictionary<string, double> location = Location(font);
        string actual = string.Join(" ", location.Select(p => p.Key + "=" + p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.AreEqual(expected, actual);
    }

    /// <summary>The advance of H in font units, measured with the font.</summary>
    private static double H(PdfOpenTypeFont? font) => Math.Round(font!.MeasureString("H", 1000), 2);

    [TestMethod]
    public void WeightInsideTheAxisPicksThatValue()
    {
        PdfFontCollection fonts = Collection("Test-Axes.ttf");
        CollectionAssert.AreEqual(new[] { Axes }, fonts.Families.ToArray());

        PdfOpenTypeFont? semi = fonts.Find(Axes, 650);
        AssertAt(semi, "wght=650 wdth=100 opsz=12 ital=0 slnt=0");
        Assert.AreEqual(650, H(semi));
        Assert.AreEqual(650, semi!.Weight);

        // Outside the axis: the nearest end, per the CSS order.
        AssertAt(fonts.Find(Axes, 950), "wght=900 wdth=100 opsz=12 ital=0 slnt=0");
        AssertAt(fonts.Find(Axes, 50), "wght=100 wdth=100 opsz=12 ital=0 slnt=0");
        Assert.AreEqual(700, H(fonts.Find(Axes, 950)));

        // The default location is the font itself, and every location is made once.
        PdfOpenTypeFont regular = fonts.Find(Axes)!;
        Assert.IsNull(regular.Location);
        Assert.AreSame(regular, regular.GetInstance(new Dictionary<string, double> { ["wght"] = 400 }));
        Assert.AreSame(semi, fonts.Find(Axes, 650));
        Assert.AreSame(semi, regular.GetInstance(new Dictionary<string, double> { ["wght"] = 650 }));
    }

    [TestMethod]
    public void WidthMatchesBeforeStyleAndWeight()
    {
        PdfFontCollection fonts = Collection("Test-Axes.ttf");
        PdfOpenTypeFont? condensed = fonts.Find(Axes, new PdfFontQuery { Width = 75 });
        AssertAt(condensed, "wght=400 wdth=75 opsz=12 ital=0 slnt=0");
        Assert.AreEqual(500, H(condensed));
        Assert.AreEqual(520, H(fonts.Find(Axes, new PdfFontQuery { Width = 80 })));

        // Narrower than the axis: nothing narrower, so the nearest wider value.
        AssertAt(fonts.Find(Axes, new PdfFontQuery { Width = 50 }), "wght=400 wdth=75 opsz=12 ital=0 slnt=0");
        AssertAt(fonts.Find(Axes, new PdfFontQuery { Width = 200, Weight = 300 }), "wght=300 wdth=125 opsz=12 ital=0 slnt=0");
    }

    [TestMethod]
    public void ItalicSetsItalAndObliqueSetsSlnt()
    {
        PdfFontCollection fonts = Collection("Test-Axes.ttf");

        // CSS Fonts 4, 7.2: one of ital and slnt follows the style, never both.
        PdfOpenTypeFont? italic = fonts.Find(Axes, new PdfFontQuery { Style = PdfFontStyle.Italic });
        AssertAt(italic, "wght=400 wdth=100 opsz=12 ital=1 slnt=0");
        Assert.AreEqual(620, H(italic));
        Assert.IsTrue(italic!.IsItalic);
        AssertAt(fonts.Find(Axes, 700, italic: true), "wght=700 wdth=100 opsz=12 ital=1 slnt=0");

        // slnt is the CSS angle negated; angles beyond the axis take its end.
        PdfOpenTypeFont? oblique = fonts.Find(Axes, new PdfFontQuery { Style = PdfFontStyle.Oblique, ObliqueAngle = 10 });
        AssertAt(oblique, "wght=400 wdth=100 opsz=12 ital=0 slnt=-10");
        Assert.AreEqual(607, H(oblique));
        Assert.AreEqual(-10, oblique!.ItalicAngle);
        AssertAt(fonts.Find(Axes, new PdfFontQuery { Style = PdfFontStyle.Oblique, ObliqueAngle = 40 }), "wght=400 wdth=100 opsz=12 ital=0 slnt=-15");

        // Leaning left has no oblique of that sign: the italic is next in the CSS order.
        AssertAt(fonts.Find(Axes, new PdfFontQuery { Style = PdfFontStyle.Oblique, ObliqueAngle = -12 }), "wght=400 wdth=100 opsz=12 ital=1 slnt=0");
    }

    [TestMethod]
    public void OpticalSizeAndVariationsApplyLast()
    {
        PdfFontCollection fonts = Collection("Test-Axes.ttf");
        PdfOpenTypeFont? caption = fonts.Find(Axes, new PdfFontQuery { OpticalSize = 40 });
        AssertAt(caption, "wght=400 wdth=100 opsz=40 ital=0 slnt=0");
        Assert.AreEqual(581, H(caption));

        // Sizes past the axis share its end, and so one instance.
        Assert.AreSame(fonts.Find(Axes, new PdfFontQuery { OpticalSize = 80 }), fonts.Find(Axes, new PdfFontQuery { OpticalSize = 96 }));

        // Explicit values win over the matched ones; tags the font lacks are ignored, values are clamped.
        var query = new PdfFontQuery
        {
            Weight = 700,
            OpticalSize = 40,
            Variations = new Dictionary<string, double> { ["wght"] = 300, ["opsz"] = 8, ["wdth"] = 500, ["XXXX"] = 1 },
        };
        AssertAt(fonts.Find(Axes, query), "wght=300 wdth=125 opsz=8 ital=0 slnt=0");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fonts.Find(Axes, new PdfFontQuery { Weight = 0 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fonts.Find(Axes, new PdfFontQuery { ObliqueAngle = 91 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fonts.Find(Axes, new PdfFontQuery { OpticalSize = 0 }));
        Assert.ThrowsExactly<ArgumentException>(() => fonts.Find(Axes, new PdfFontQuery { Variations = new Dictionary<string, double> { ["wght"] = double.NaN } }));
    }

    [TestMethod]
    public void NamedInstancesAreFacesFoundByFullName()
    {
        PdfFontCollection fonts = Collection("Test-Axes.ttf");
        AssertAt(fonts.Find(Axes + " Bold"), "wght=700 wdth=100 opsz=12 ital=0 slnt=0");
        AssertAt(fonts.Find(Axes + " Expanded Black"), "wght=900 wdth=125 opsz=12 ital=0 slnt=0");
        AssertAt(fonts.Find(Axes + " Display"), "wght=400 wdth=100 opsz=72 ital=0 slnt=0");
        Assert.AreEqual("PdfNetStackTestAxes-Display", fonts.Find(Axes + " display")!.Name);

        // Asking for a named instance's values gives that instance.
        AssertAt(fonts.Find(Axes, new PdfFontQuery { Width = 125, Weight = 900 }), "wght=900 wdth=125 opsz=12 ital=0 slnt=0");
        Assert.IsNull(fonts.Find(Axes + " Nope"));
    }

    [TestMethod]
    public void AddingLoadedFontsAndInstances()
    {
        PdfOpenTypeFont variable = PdfOpenTypeFont.Load(FixturePath("Test-Axes.ttf"));
        var fonts = new PdfFontCollection();
        fonts.Add(variable);
        Assert.AreSame(variable, fonts.Find(Axes));
        AssertAt(fonts.Find(Axes, 600), "wght=600 wdth=100 opsz=12 ital=0 slnt=0");
        Assert.AreSame(variable.GetInstance("Bold"), fonts.Find(Axes + " Bold"));

        // An instance added on its own is a face at its location only.
        PdfOpenTypeFont light = variable.GetInstance(new Dictionary<string, double> { ["wght"] = 300, ["wdth"] = 90 });
        var only = new PdfFontCollection();
        only.Add(light);
        Assert.AreSame(light, only.Find(Axes, 900));
        Assert.AreSame(light, only.Find(Axes, new PdfFontQuery { Width = 125, OpticalSize = 12 }));
    }

    [TestMethod]
    [DataRow("Test-Variable.ttf")]
    [DataRow("Test-Variable.otf")]
    public void VariableFixturesMatchByWeight(string name)
    {
        // The advances of A from fontTools' instancer (Test-Variable.instances.json).
        PdfFontCollection fonts = Collection(name);
        const string Family = "PdfNetStack Test Variable";
        PdfOpenTypeFont font = fonts.Find(Family, 550)!;
        Assert.AreEqual(636, Math.Round(font.MeasureString("A", 1000), 2));
        Assert.AreEqual(645, Math.Round(fonts.Find(Family, 700)!.MeasureString("A", 1000), 2));
        Assert.AreEqual(628, Math.Round(fonts.Find(Family)!.MeasureString("A", 1000), 2));
        Assert.AreSame(fonts.Find(Family + " SemiBold"), fonts.Find(Family, 600));

        // No ital or slnt axis and an upright font: italic falls back to it.
        Assert.AreSame(font, fonts.Find(Family, 550, italic: true));
    }

    [TestMethod]
    public void StaticFamiliesMatchAsBefore()
    {
        // A static family keeps matching as before.
        PdfFontCollection fonts = Collection("Test.ttc");
        Assert.AreEqual("PdfNetStackTest-Bold", fonts.Find("PdfNetStack Test", 650)!.Name);
        Assert.AreEqual("PdfNetStackTest-Regular", fonts.Find("PdfNetStack Test", new PdfFontQuery { Width = 50, Style = PdfFontStyle.Oblique })!.Name);
        Assert.AreEqual("PdfNetStackTest-Regular", fonts.Find("PdfNetStackTest-Regular")!.Name);
    }

    [TestMethod]
    public void OneProgramPerLocation()
    {
        PdfFontCollection fonts = Collection("Test-Axes.ttf");
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            double y = 800;
            for (int size = 6; size <= 30; size += 2)
            {
                // Many sizes, three locations: the default, weight 650, and opsz past the axis's end.
                g.DrawString("HI", fonts.Find(Axes)!, size, PdfColor.Black, 72, y -= 20);
                g.DrawString("HI", fonts.Find(Axes, 650)!, size, PdfColor.Black, 200, y);
                g.DrawString("HI", fonts.Find(Axes, new PdfFontQuery { OpticalSize = 72 + size })!, size, PdfColor.Black, 300, y);
            }
        }

        PdfDocument reopened = PdfDocument.Open(doc.ToArray(Plain));
        var resources = (PdfDictionary)reopened.Resolve(reopened.Pages[0].Dictionary["Resources"])!;
        var pageFonts = (PdfDictionary)reopened.Resolve(resources["Font"])!;
        List<string> names = [.. pageFonts.Select(f => ((PdfName)((PdfDictionary)reopened.Resolve(f.Value)!)["BaseFont"]!).Value.Substring(7))];
        CollectionAssert.AreEquivalent(new[] { "PdfNetStackTestAxes-Regular", "PdfNetStackTestAxes_650wght", "PdfNetStackTestAxes-Display" }, names);
    }

    [TestMethod]
    public void DamagedVariationDataFailsCleanly()
    {
        // Every byte of fvar, gvar and HVAR set to 0 and to 255: adding the font either works or is
        // a FontFormatException, and finding a face never throws. fvar goes through a file, as the
        // collection reads it without loading the font.
        byte[] data = File.ReadAllBytes(FixturePath("Test-Axes.ttf"));
        OpenTypeFile file = OpenTypeFile.Parse(data, 0);
        string path = Path.Combine(Path.GetTempPath(), "pdfnetstack-axes-" + Guid.NewGuid().ToString("N") + ".ttf");
        try
        {
            foreach (string tag in new[] { "fvar", "gvar", "HVAR" })
            {
                int start = file.TableOffset(tag), length = file.Table(tag).Length;
                for (int i = start; i < start + length; i++)
                {
                    foreach (byte value in new byte[] { 0, 255 })
                    {
                        byte[] damaged = (byte[])data.Clone();
                        damaged[i] = value;
                        var fonts = new PdfFontCollection();
                        try
                        {
                            if (tag == "fvar")
                            {
                                File.WriteAllBytes(path, damaged);
                                fonts.AddFile(path);
                            }
                            else
                            {
                                fonts.Add(PdfOpenTypeFont.Load(damaged));
                            }
                        }
                        catch (FontFormatException)
                        {
                            continue;
                        }

                        PdfOpenTypeFont? font = fonts.Find(Axes, new PdfFontQuery { Weight = 650, Width = 80, Style = PdfFontStyle.Italic, OpticalSize = 30 });
                        font?.MeasureString("HI", 12);
                    }
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void StyleSearchFollowsCssFonts4()
    {
        FontTraits normal = Face(italic: 0, oblique: 0);
        FontTraits italic = Face(italic: 1);
        FontTraits oblique20 = Face(oblique: 20);
        FontTraits oblique8 = Face(oblique: 8);
        FontTraits backward = Face(oblique: -20);

        // Italic: italics, then obliques from 11 degrees up, then below 11, then upright.
        Assert.AreEqual(new StyleMatch(false, 1), Match(PdfFontStyle.Italic, 0, normal, oblique20, italic));
        Assert.AreEqual(new StyleMatch(true, 20), Match(PdfFontStyle.Italic, 0, normal, oblique8, oblique20));
        Assert.AreEqual(new StyleMatch(true, 8), Match(PdfFontStyle.Italic, 0, normal, oblique8));
        Assert.AreEqual(new StyleMatch(false, 0), Match(PdfFontStyle.Italic, 0, normal, backward));

        // Oblique from 11 degrees: larger angles first, then smaller ones, then italics.
        Assert.AreEqual(new StyleMatch(true, 20), Match(PdfFontStyle.Oblique, 14, oblique8, oblique20, italic));
        Assert.AreEqual(new StyleMatch(true, 8), Match(PdfFontStyle.Oblique, 30, oblique8, italic));
        Assert.AreEqual(new StyleMatch(false, 1), Match(PdfFontStyle.Oblique, 30, normal, italic, backward));

        // Below 11 degrees: smaller angles first; negative angles mirror the search.
        Assert.AreEqual(new StyleMatch(true, 8), Match(PdfFontStyle.Oblique, 10, oblique8, oblique20));
        Assert.AreEqual(new StyleMatch(true, 20), Match(PdfFontStyle.Oblique, 5, oblique20, normal));
        Assert.AreEqual(new StyleMatch(true, -20), Match(PdfFontStyle.Oblique, -14, oblique20, backward, normal));
        Assert.AreEqual(new StyleMatch(true, -12), Match(PdfFontStyle.Oblique, -12, Face(oblique: 0, upTo: -15)));

        // Normal: upright and obliques from 0 up, then italics, then backward obliques.
        Assert.AreEqual(new StyleMatch(true, 0), Match(PdfFontStyle.Normal, 0, italic, normal));
        Assert.AreEqual(new StyleMatch(true, 8), Match(PdfFontStyle.Normal, 0, italic, oblique8));
        Assert.AreEqual(new StyleMatch(false, 1), Match(PdfFontStyle.Normal, 0, italic, backward));

        static FontTraits Face(double? italic = null, double? oblique = null, double? upTo = null) =>
            new(FontRange.Of(400), FontRange.Of(100), italic is double i ? FontRange.Of(i) : null, oblique is double o ? FontRange.Of(o, upTo ?? o) : null);

        static StyleMatch? Match(PdfFontStyle style, double angle, params FontTraits[] faces) => FontMatcher.MatchStyle(faces, style, angle);
    }

    [TestMethod]
    public void WidthSearchFollowsCssFonts4()
    {
        FontRange[] widths = [FontRange.Of(75), FontRange.Of(100), FontRange.Of(150)];
        Assert.AreEqual(150, FontMatcher.MatchWidth(widths, 125));
        Assert.AreEqual(75, FontMatcher.MatchWidth(widths, 90));
        Assert.AreEqual(75, FontMatcher.MatchWidth(widths, 50));
        Assert.AreEqual(90, FontMatcher.MatchWidth([FontRange.Of(80, 90), FontRange.Of(110, 120)], 100));
        Assert.AreEqual(110, FontMatcher.MatchWidth([FontRange.Of(80, 90), FontRange.Of(110, 120)], 105));
        Assert.AreEqual(95, FontMatcher.MatchWidth([FontRange.Of(50, 120)], 95));
        Assert.IsNull(FontMatcher.MatchWidth([], 100));

        // Width narrows the set before weight does.
        var faces = new[] { ("regular", Traits(400, 100)), ("condensed bold", Traits(700, 75)) };
        Assert.AreEqual("condensed bold", FontMatcher.Select(faces, f => f.Item2, new PdfFontQuery { Width = 75 }).Single().Item1);
        Assert.AreEqual("regular", FontMatcher.Select(faces, f => f.Item2, new PdfFontQuery { Weight = 700 }).Single().Item1);

        static FontTraits Traits(double weight, double width) => new(FontRange.Of(weight), FontRange.Of(width), FontRange.Of(0), FontRange.Of(0));
    }

    [TestMethod]
    public void HtmlChoosesInstancesFromFontProperties()
    {
        string html = $$"""
            <style>
            @font-face { font-family: Axes; src: url({{DataUri()}}); font-weight: 100 900; font-stretch: 75% 125%; font-style: oblique 0deg 15deg }
            @font-face { font-family: Axes; src: url({{DataUri()}}); font-weight: 100 900; font-stretch: 75% 125%; font-style: italic }
            body { font: 20px Axes; font-optical-sizing: none }
            .w { font-weight: 650 }
            .c { font-stretch: condensed }
            .i { font-style: italic }
            .o { font-style: oblique 10deg }
            .s { font-optical-sizing: auto; font-size: 40px }
            .v { font-weight: 700; font-variation-settings: "wght" 300, "XXXX" 5, "wdth" 110 }
            .f { font: oblique 10deg bold condensed 20px Axes; font-optical-sizing: none }
            </style>
            <p>H</p><p class=w>H</p><p class=c>H</p><p class=i>H</p><p class=o>H</p><p class=s>H</p><p class=v>H</p><p class=f>H</p>
            """;
        double[] expected = [600, 650, 500, 620, 607, 581, 600 - 60 * 100.0 / 300 + 100 * 10 / 25.0, 600 + 100 * 300 / 500.0 - 100 + 10 * 10 / 15.0];
        double[] sizes = [15, 15, 15, 15, 15, 30, 15, 15];
        AssertWidths(html, expected.Select((h, i) => Math.Round(h) * sizes[i] / 1000).ToArray());
    }

    [TestMethod]
    public void HtmlFontFaceDescriptorsClampAndSet()
    {
        string html = $$"""
            <style>
            @font-face { font-family: Auto; src: url({{DataUri()}}) }
            @font-face { font-family: Light; src: url({{DataUri()}}); font-weight: 300 }
            @font-face { font-family: Named; src: url({{DataUri()}}); font-named-instance: "Condensed" }
            @font-face { font-family: Wide; src: url({{DataUri()}}); font-variation-settings: "wdth" 125 }
            body { font-size: 20px; font-optical-sizing: none }
            </style>
            <p style="font-family: Auto"><b>H</b></p>
            <p style="font-family: Light"><b>H</b></p>
            <p style="font-family: Named"><b>H</b></p>
            <p style="font-family: Wide; font-weight: 900">H</p>
            <p style="font-family: Wide; font-weight: 900; font-variation-settings: 'wdth' 75">H</p>
            """;

        // Auto applies the weight unclamped; 300 clamps it; the named instance overrides it (CSS Fonts 4, 7.2).
        AssertWidths(html, [Math.Round(600 + 100 * 300 / 500.0) * 0.015, Math.Round(600 - 60 * 100 / 300.0) * 0.015, 500 * 0.015, 800 * 0.015, 600 * 0.015]);
    }

    [TestMethod]
    public void HtmlEmbedsOneProgramPerLocation()
    {
        // Two rules over one file, many sizes, and opsz (auto after the font shorthand) past the axis's end: two locations in all.
        string html = $$"""
            <style>
            @font-face { font-family: A; src: url({{DataUri()}}) }
            @font-face { font-family: B; src: url({{DataUri()}}); font-weight: 100 900 }
            </style>
            {{string.Concat(Enumerable.Range(0, 12).Select(i => $"<p style=\"font: {10 + i}px A; font-optical-sizing: none\">H</p><p style=\"font: {10 + i}px B; font-optical-sizing: none\">H</p><p style=\"font: {100 + i}px B\">H</p>"))}}
            """;
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        var names = new HashSet<string>();
        foreach (PdfPage page in doc.Pages)
        {
            foreach (PdfLetter letter in page.GetLetters()) names.Add(letter.FontName.Substring(letter.FontName.IndexOf('+') + 1));
        }

        CollectionAssert.AreEquivalent(new[] { "PdfNetStackTestAxes-Regular", "PdfNetStackTestAxes-Display" }, names.ToArray());
    }

    [TestMethod]
    [DataRow("font-variation-settings: 'wght' 1e309")]
    [DataRow("font-variation-settings: 'wght' -1e309, 'opsz' 1e300")]
    [DataRow("font-variation-settings: 'wght' calc(1e308 * 10)")]
    [DataRow("font-variation-settings: 'wght' calc(infinity)")]
    [DataRow("font-variation-settings: 'wght' calc(NaN)")]
    [DataRow("font-stretch: 1e309%")]
    [DataRow("font-stretch: 1e300%")]
    [DataRow("font-width: 1e309%")]
    [DataRow("font-size: 1e309px")]
    [DataRow("font-size: calc(1e308px * 10)")]
    [DataRow("font-size: 1e300px")]
    [DataRow("font-weight: calc(infinity)")]
    [DataRow("font-weight: 1e309")]
    [DataRow("font-style: oblique 1e309deg")]
    [DataRow("font-style: oblique calc(infinity * 1deg)")]
    [DataRow("font: oblique 1e309deg 1e309px A")]
    public void HostileFontValuesResolve(string declaration)
    {
        // Untrusted style values never make font matching throw: each is dropped or clamped, and
        // the family's variable font still answers.
        string html = $$"""
            <style>
            @font-face { font-family: A; src: url(a.ttf); font-stretch: 1e309% 1e309%; font-variation-settings: "wght" 1e309 }
            @font-face { font-family: A; src: url(a.ttf); font-weight: 1e309; font-style: oblique 1e309deg }
            @font-face { font-family: A; src: url(a.ttf); font-variation-settings: "wght" calc(infinity), "wdth" -1e300 }
            p { font-family: A; font-size: 12px }
            </style>
            <p id=x style="{{declaration}}">H</p>
            """;
        HtmlDocument doc = HtmlParser.Parse(html);
        var resolver = new StyleResolver(CssMedia.Default);
        resolver.AddDocument(doc);
        var fonts = new FontResolver(null, useSystemFonts: false);
        byte[] data = File.ReadAllBytes(FixturePath("Test-Axes.ttf"));
        fonts.AddFontFaces(resolver.FontFaces, url => url == "a.ttf" ? data : null);
        var styles = new Dictionary<HtmlElement, ComputedStyle>();
        foreach (HtmlElement e in doc.Descendants())
        {
            styles[e] = resolver.Compute(e, e.ParentElement is HtmlElement p ? styles[p] : null);
        }

        ComputedStyle style = styles.Single(p => p.Key.GetAttribute("id") == "x").Value;
        Assert.AreEqual(Axes, fonts.Resolve(style).Primary.FamilyName, declaration);
    }

    [TestMethod]
    [DataRow("font-variation-settings: 'wght' 1e309")]
    [DataRow("font-variation-settings: 'wght' calc(1e308 * 10), 'opsz' calc(-infinity)")]
    [DataRow("font-stretch: 1e309%")]
    [DataRow("font-weight: calc(infinity)")]
    [DataRow("font-style: oblique 1e309deg")]
    [DataRow("font-size: 1e309px")]
    [DataRow("font-size: calc(1e308px * 10)")]
    [DataRow("font-size: 1e300px")]
    [DataRow("font: oblique 1e309deg 1e309px A")]
    [DataRow("font: oblique 80deg 1e300px A")]
    public void HostileFontValuesConvert(string declaration)
    {
        // Font sizes clamp to 10000px, so even the largest lays out in a bounded number of pages.
        string html = $$"""
            <style>
            @font-face { font-family: A; src: url({{DataUri()}}); font-stretch: 1e309% 1e309%; font-variation-settings: "wght" 1e309 }
            p { font-family: A; font-size: 12px }
            </style>
            <p style="{{declaration}}">H</p>
            """;
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        Assert.IsLessThanOrEqualTo(40, doc.Pages.Count);
        StringAssert.Contains(doc.Pages.SelectMany(p => p.GetLetters()).First().FontName, "PdfNetStackTestAxes");
    }

    [TestMethod]
    public void QueryValuesAreCheckedWhenSet()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfFontQuery { Weight = double.NaN });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfFontQuery { Weight = 1001 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfFontQuery { Width = double.PositiveInfinity });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfFontQuery { Width = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfFontQuery { ObliqueAngle = double.NaN });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfFontQuery { OpticalSize = double.PositiveInfinity });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfFontQuery { OpticalSize = -2 });
        Assert.ThrowsExactly<ArgumentException>(() => new PdfFontQuery { Variations = new Dictionary<string, double> { ["wght"] = double.NegativeInfinity } });

        // The variations are copied: changing the dictionary later cannot slip a bad value in.
        var variations = new Dictionary<string, double> { ["wght"] = 500 };
        var query = new PdfFontQuery { Variations = variations, OpticalSize = null };
        variations["wght"] = double.NaN;
        Assert.AreEqual(500, query.Variations!["wght"]);
    }

    [TestMethod]
    public void BudgetSnapsAndCapsInstances()
    {
        PdfOpenTypeFont font = PdfOpenTypeFont.Load(FixturePath("Test-Axes.ttf"));
        var budget = new InstanceBudget(3);

        // Whole units for wght, wdth and opsz, half degrees for slnt, tenths for ital.
        PdfOpenTypeFont a = budget.Instance(font, new Dictionary<string, double> { ["wght"] = 650.4, ["opsz"] = 20.5, ["slnt"] = -10.3, ["ital"] = 0.04 });
        AssertAt(a, "wght=650 wdth=100 opsz=21 ital=0 slnt=-10.5");
        Assert.AreSame(a, budget.Instance(font, new Dictionary<string, double> { ["wght"] = 649.6, ["opsz"] = 20.7, ["slnt"] = -10.4 }));

        // Past the cap, the nearest location already made.
        PdfOpenTypeFont light = budget.Instance(font, new Dictionary<string, double> { ["wght"] = 100 });
        PdfOpenTypeFont wide = budget.Instance(font, new Dictionary<string, double> { ["wdth"] = 125 });
        Assert.AreSame(light, budget.Instance(font, new Dictionary<string, double> { ["wght"] = 130 }));
        Assert.AreSame(wide, budget.Instance(font, new Dictionary<string, double> { ["wdth"] = 120, ["wght"] = 420 }));
        Assert.AreSame(a, budget.Instance(font, new Dictionary<string, double> { ["wght"] = 640, ["opsz"] = 20, ["slnt"] = -9 }));

        // The cap is per font: another font has its own.
        PdfOpenTypeFont other = PdfOpenTypeFont.Load(FixturePath("Test-Variable.ttf"));
        AssertAt(budget.Instance(other, new Dictionary<string, double> { ["wght"] = 333.3 }), "wght=333");
    }

    [TestMethod]
    public void HtmlBoundsTheInstancesOfAFont()
    {
        // Sizes a hundredth of a pixel apart share whole opsz values; past 32 locations a font
        // reuses the nearest one it has. 300 paragraphs, at most 32 programs.
        string sizes = string.Concat(Enumerable.Range(0, 100).Select(i => $"<p style=\"font-size: {(10 + i * 0.01).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}px\">H</p>"));
        string weights = string.Concat(Enumerable.Range(0, 200).Select(i => $"<p style=\"font-variation-settings: 'wght' {100 + i * 4}, 'wdth' {75 + i % 50}\">H</p>"));
        string html = $$"""
            <style>
            @font-face { font-family: A; src: url({{DataUri()}}) }
            p { font-family: A; margin: 0; font-size: 10px }
            </style>
            {{sizes}}{{weights}}
            """;
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        var names = new HashSet<string>();
        foreach (PdfPage page in doc.Pages)
        {
            foreach (PdfLetter letter in page.GetLetters()) names.Add(letter.FontName.Substring(letter.FontName.IndexOf('+') + 1));
        }

        Assert.IsLessThanOrEqualTo(32, names.Count, string.Join(", ", names));
        Assert.IsGreaterThanOrEqualTo(30, names.Count, string.Join(", ", names));

        // Whole numbers: 10.00px to 10.49px is opsz 10, 10.50px and up is 11.
        Assert.IsTrue(names.Contains("PdfNetStackTestAxes_10opsz") && names.Contains("PdfNetStackTestAxes_11opsz"), string.Join(", ", names));
        Assert.IsFalse(names.Any(n => n.Contains('.')), string.Join(", ", names));
    }

    [TestMethod]
    public void CssAcceptsTheVariationProperties()
    {
        Assert.IsNotNull(Expand("font-style: oblique -20deg"));
        Assert.IsNotNull(Expand("font-style: oblique 0.25turn"));
        Assert.IsNull(Expand("font-style: oblique 91deg"));
        Assert.IsNull(Expand("font-style: italic 10deg"));
        Assert.IsNotNull(Expand("font-variation-settings: \"wght\" 650, 'opsz' calc(10 + 2)"));
        Assert.IsNull(Expand("font-variation-settings: \"wgt\" 650"));
        Assert.IsNull(Expand("font-variation-settings: wght 650"));
        Assert.IsNull(Expand("font-variation-settings: \"wght\" 650,"));
        Assert.IsNotNull(Expand("font-optical-sizing: none"));
        Assert.IsNull(Expand("font-optical-sizing: 12px"));
        Assert.AreEqual("font-stretch: 80%", string.Join("; ", Expand("font-width: 80%")!.Select(d => d.ToString())));
        StringAssert.Contains(string.Join("; ", Expand("font: oblique 12deg 12px x")!.Select(d => d.ToString())), "font-style: oblique 12deg");

        static List<Html.Css.CssDeclaration>? Expand(string declaration) =>
            Html.Css.CssProperties.Expand(Html.Css.CssParser.ParseDeclarations(declaration).Single());
    }

    private static string DataUri() => "data:font/ttf;base64," + Convert.ToBase64String(File.ReadAllBytes(FixturePath("Test-Axes.ttf")));

    /// <summary>Converts HTML whose paragraphs each hold one H, and checks the width each H is drawn with, in points.</summary>
    private static void AssertWidths(string html, double[] expected)
    {
        PdfDocument doc = PdfDocument.Open(HtmlConverter.ConvertToPdf(html, new HtmlConverterOptions { UseSystemFonts = false }));
        List<PdfLetter> letters = [.. doc.Pages[0].GetLetters().Where(l => l.Text == "H")];
        double[] actual = [.. letters.Select(l => Math.Round(l.BaselineEnd.X - l.BaselineStart.X, 3))];
        CollectionAssert.AreEqual(expected.Select(w => Math.Round(w, 3)).ToArray(), actual, string.Join(", ", letters.Select(l => l.FontName + " " + (l.BaselineEnd.X - l.BaselineStart.X))));
    }
}
