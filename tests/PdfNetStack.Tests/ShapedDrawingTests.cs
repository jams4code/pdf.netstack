using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>
/// What the content stream draws against what the shaper and layout computed: the content is
/// replayed as a viewer would (Tm, Tf, Ts, Tj and TJ with the font's /W widths) and every glyph
/// must land at its shaped pen position plus offset. Uses the synthetic Test-Arabic.ttf, whose
/// marks carry horizontal and vertical offsets.
/// </summary>
[TestClass]
public sealed class ShapedDrawingTests
{
    private static readonly PdfOpenTypeFont Arabic = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Arabic.ttf"));

    private static (PdfDocument Doc, PdfTextLayout Layout) Draw(string text, double size = 16) =>
        Draw(new PdfParagraph { Alignment = PdfTextAlignment.Right }.Add(text, new PdfTextStyle(Arabic, size)));

    private static (PdfDocument Doc, PdfTextLayout Layout) Draw(PdfParagraph paragraph)
    {
        PdfDocument doc = PdfDocument.Create();
        PdfTextLayout layout;
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) layout = g.DrawText(paragraph, 50, 100, 400);
        return (PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Optimize = false })), layout);
    }

    private static string Content(PdfDocument doc)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[0].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    /// <summary>Widths by CID from the font's /W array, in thousandths.</summary>
    private static Dictionary<int, double> Widths(PdfDocument doc)
    {
        var resources = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        var type0 = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(resources["Font"])!)["F1"])!;
        var cid = (PdfDictionary)doc.Resolve(((PdfArray)doc.Resolve(type0["DescendantFonts"])!)[0])!;
        var w = (PdfArray)doc.Resolve(cid["W"])!;
        var widths = new Dictionary<int, double>();
        for (int i = 0; i < w.Count; i += 2)
        {
            int first = (int)((PdfNumber)w[i]).ToDouble();
            var run = (PdfArray)w[i + 1];
            for (int k = 0; k < run.Count; k++) widths[first + k] = ((PdfNumber)run[k]).ToDouble();
        }

        return widths;
    }

    /// <summary>Each glyph code with the x and rise it is drawn at, replaying the text operators.</summary>
    private static List<(int Code, double X, double Rise)> Replay(PdfDocument doc)
    {
        Dictionary<int, double> widths = Widths(doc);
        var placed = new List<(int, double, double)>();
        var numbers = new List<double>();
        double x = 0, size = 0, rise = 0;
        string previous = "";
        bool array = false;
        foreach (Match m in Regex.Matches(Content(doc), @"<[0-9A-Fa-f]*>|-?\d*\.?\d+|/\w+|\[|\]|[A-Za-z*']+"))
        {
            string t = m.Value;
            bool actualText = previous == "/ActualText";
            previous = t;
            if (actualText) continue;
            if (t[0] == '<')
            {
                for (int i = 1; i + 4 <= t.Length - 1; i += 4)
                {
                    int code = Convert.ToInt32(t.Substring(i, 4), 16);
                    placed.Add((code, x, rise));
                    x += widths.GetValueOrDefault(code) / 1000 * size;
                }

                continue;
            }

            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                // In a TJ array a number moves left by thousandths of the font size.
                if (array) x -= n / 1000 * size;
                else numbers.Add(n);
                continue;
            }

            switch (t)
            {
                case "Tm":
                    x = numbers[^2];
                    break;
                case "Tf":
                    size = numbers[^1];
                    break;
                case "Ts":
                    rise = numbers[^1];
                    break;
                case "[":
                    array = true;
                    break;
                case "]":
                    array = false;
                    break;
            }

            numbers.Clear();
        }

        return placed;
    }

    /// <summary>Every glyph of the layout's first line with the x and rise the shaping gives it.</summary>
    private static List<(int Glyph, double X, double Rise)> Expected(PdfTextLayout layout, double left, double size)
    {
        var expected = new List<(int, double, double)>();
        PdfTextLine line = layout.Lines[0];
        foreach (LineRun run in line.Runs)
        {
            double pen = left + line.Left + run.X;
            foreach (ShapedGlyph g in run.Glyphs)
            {
                if (g.Glyph != 0 || g.Advance != 0) expected.Add((g.Glyph, pen + g.XOffset * size / 1000, g.YOffset * size / 1000));
                pen += g.Advance * size / 1000;
            }
        }

        return expected;
    }

    [TestMethod]
    public void MarksWithOffsetsDoNotMoveTheGlyphsAfterThem()
    {
        // Fatha, shadda and kasra sit above and below with offsets; the bases after them must not move.
        string text = "\u0628\u064e\u0628\u0651\u064e\u0628\u0650 \u0628\u064e\u0628";
        (PdfDocument doc, PdfTextLayout layout) = Draw(text);
        List<(int Code, double X, double Rise)> drawn = Replay(doc);
        List<(int Glyph, double X, double Rise)> expected = Expected(layout, 50, 16);
        Assert.HasCount(expected.Count, drawn);
        Assert.IsTrue(expected.Any(e => e.Rise != 0), "the sample has raised marks");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].Glyph, drawn[i].Code, $"glyph {i}");
            Assert.AreEqual(expected[i].X, drawn[i].X, 1e-3, $"x of glyph {i}");
            Assert.AreEqual(expected[i].Rise, drawn[i].Rise, 1e-3, $"rise of glyph {i}");
        }

        // The line ends where the layout says: right aligned at 450.
        double end = drawn.Max(d => d.X);
        Assert.IsLessThanOrEqualTo(450 + 1e-3, end);
    }

    [TestMethod]
    public void MeasuredWidthIsTheDrawnWidth()
    {
        string text = "\u0644\u064e\u0627\u064e \u0628\u0644\u0651\u0627\u0650";
        (PdfDocument doc, PdfTextLayout layout) = Draw(text);
        Dictionary<int, double> widths = Widths(doc);
        List<(int Code, double X, double Rise)> drawn = Replay(doc);
        double right = drawn.Max(d => d.X + widths[d.Code] / 1000 * 16);
        Assert.AreEqual(450, right, 1e-3, "the last glyph ends at the right edge");
        Assert.AreEqual(Arabic.MeasureString(text, 16), layout.Lines[0].Width, 1e-9);
    }

    [TestMethod]
    public void SpacesAtRunEdgesStayOutOfActualText()
    {
        // An RTL paragraph that starts with a period in another font, as a fallback gives it: the
        // space after it is the Arabic run's logical first character but its visual right end.
        string arabic = " \u0628\u064e\u0628 \u0628\u0628";
        string text = "." + arabic;
        (PdfDocument doc, _) = Draw(new PdfParagraph { Alignment = PdfTextAlignment.Right }
            .Add(".", new PdfTextStyle(PdfStandardFont.Helvetica, 16))
            .Add(arabic, new PdfTextStyle(Arabic, 16)));
        string content = Content(doc);
        foreach (Match m in Regex.Matches(content, @"/ActualText<FEFF([0-9A-F]*)>"))
        {
            string hex = m.Groups[1].Value;
            Assert.IsFalse(hex.StartsWith("0020", StringComparison.Ordinal) || hex.EndsWith("0020", StringComparison.Ordinal), $"ActualText {hex} has an edge space");
        }

        Assert.AreEqual(text, doc.Pages[0].ExtractText());
    }
}
