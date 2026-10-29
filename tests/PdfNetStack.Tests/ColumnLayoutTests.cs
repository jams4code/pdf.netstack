using PdfNetStack.Drawing;
using PdfNetStack.Fonts;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class ColumnLayoutTests
{
    private static string[] Lines(Action<PdfGraphics> draw)
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) draw(g);
        return PdfDocument.Open(doc.ToArray()).Pages[0].ExtractText().Split('\n');
    }

    private static readonly string[] Left = ["The left column opens here", "and carries on for a while", "until it reaches its end."];
    private static readonly string[] Right = ["The right column starts", "beside it on the same lines", "and closes the article."];

    [TestMethod]
    public void ReadsColumnsOneAfterTheOther()
    {
        PdfFont font = PdfStandardFont.TimesRoman;
        string[] lines = Lines(g =>
        {
            g.DrawString("A Title Across Both Columns", PdfStandardFont.HelveticaBold, 18, PdfColor.Black, 72, 80);
            for (int i = 0; i < Left.Length; i++)
            {
                g.DrawString(Left[i], font, 11, PdfColor.Black, 72, 130 + (14 * i));
                g.DrawString(Right[i], font, 11, PdfColor.Black, 320, 130 + (14 * i));
            }

            g.DrawString("A footer across the page, below both columns.", font, 9, PdfColor.Black, 72, 260);
        });

        CollectionAssert.AreEqual(
            new[] { "A Title Across Both Columns" }.Concat(Left).Concat(Right).Concat(["A footer across the page, below both columns."]).ToArray(),
            lines,
            string.Join(" | ", lines));
    }

    [TestMethod]
    public void KeepsTablesRowByRow()
    {
        string[][] rows = [["Invoice", "2026-114"], ["Date", "29 October 2026"], ["Customer", "JADEV"], ["Total", "1,250.00 EUR"]];
        string[] lines = Lines(g =>
        {
            for (int i = 0; i < rows.Length; i++)
            {
                g.DrawString(rows[i][0], PdfStandardFont.Helvetica, 11, PdfColor.Black, 72, 100 + (16 * i));
                g.DrawString(rows[i][1], PdfStandardFont.Helvetica, 11, PdfColor.Black, 300, 100 + (16 * i));
            }
        });

        CollectionAssert.AreEqual(rows.Select(r => r[0] + " " + r[1]).ToArray(), lines, string.Join(" | ", lines));
    }

    [TestMethod]
    public void ReadsRightToLeftColumnsFromTheRight()
    {
        PdfFont arabic = PdfOpenTypeFont.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "Test-Arabic.ttf"));
        // The test font covers alef, beh, lam and meem: words made of those.
        string first = "بمم لا بلا مبل امل";
        string second = "الم باب مال لبم بما";
        string[] lines = Lines(g =>
        {
            for (int i = 0; i < 3; i++)
            {
                // The first column is on the right, as Arabic pages read.
                double width = arabic.MeasureString(first, 11);
                g.DrawString(first, arabic, 11, PdfColor.Black, 520 - width, 130 + (14 * i));
                g.DrawString(second, arabic, 11, PdfColor.Black, 72, 130 + (14 * i));
            }
        });

        Assert.HasCount(6, lines, string.Join(" | ", lines));
        Assert.IsTrue(lines.Take(3).All(l => l == first), string.Join(" | ", lines));
        Assert.IsTrue(lines.Skip(3).All(l => l == second), string.Join(" | ", lines));
    }
}
