using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Fitting an image into a box, aligning it there, and drawing it faded.</summary>
[TestClass]
public sealed class ImagePlacementTests
{
    // 7 by 5 pixels at 96 per inch: 5.25 by 3.75 points.
    private static readonly PdfImage Image = PdfImage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "rgb8.png"));

    private static string Draw(Action<PdfGraphics> draw)
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics()) draw(g);
        return Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(doc.Pages[0].Dictionary["Contents"])!));
    }

    [TestMethod]
    [DataRow(PdfImageFit.Fill, PdfImageAlignment.Center, "100 0 0 -50 10 70 cm")]
    [DataRow(PdfImageFit.Contain, PdfImageAlignment.Center, "70 0 0 -50 25 70 cm")]
    [DataRow(PdfImageFit.Contain, PdfImageAlignment.TopLeft, "70 0 0 -50 10 70 cm")]
    [DataRow(PdfImageFit.Contain, PdfImageAlignment.BottomRight, "70 0 0 -50 40 70 cm")]
    [DataRow(PdfImageFit.Cover, PdfImageAlignment.Center, "100 0 0 -71.42857 10 80.71429 cm")]
    [DataRow(PdfImageFit.Cover, PdfImageAlignment.Top, "100 0 0 -71.42857 10 91.42857 cm")]
    [DataRow(PdfImageFit.None, PdfImageAlignment.Center, "5.25 0 0 -3.75 57.375 46.875 cm")]
    [DataRow(PdfImageFit.ScaleDown, PdfImageAlignment.Left, "5.25 0 0 -3.75 10 46.875 cm")]
    public void FitsAndAlignsInTheBox(PdfImageFit fit, PdfImageAlignment alignment, string placement)
    {
        string content = Draw(g => g.DrawImage(Image, 10, 20, 100, 50, fit, alignment));
        StringAssert.Contains(content, placement + "\n/Im1 Do");
        bool clipped = fit == PdfImageFit.Cover;
        Assert.AreEqual(clipped, content.Contains(" re\nW\nn"), content);
    }

    [TestMethod]
    public void ScaleDownShrinksALargeImage()
    {
        string content = Draw(g => g.DrawImage(Image, 0, 0, 2.1, 10, PdfImageFit.ScaleDown));
        StringAssert.Contains(content, "2.1 0 0 -1.5 0 5.75 cm");
    }

    [TestMethod]
    public void OpacityFadesTheImageAndNothingElse()
    {
        PdfDocument doc = PdfDocument.Create();
        using (PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics())
        {
            g.DrawRectangle(null, new PdfSolidBrush(PdfColor.Rgb(1, 0, 0, 0.3)), 0, 0, 10, 10);
            g.DrawImage(Image, 0, 0, 10, 10);
            g.DrawImage(Image, 0, 0, 10, 10, PdfImageFit.Fill, opacity: 0.5);
        }

        var resources = (PdfDictionary)doc.Resolve(doc.Pages[0].Dictionary["Resources"])!;
        var states = (PdfDictionary)doc.Resolve(resources["ExtGState"])!;
        double[] alphas = [.. states.Select(s => ((PdfNumber)((PdfDictionary)doc.Resolve(s.Value)!)["ca"]!).ToDouble())];
        CollectionAssert.AreEquivalent(new[] { 0.3, 1, 0.5 }, alphas);

        // The fill's 0.3 is replaced by 1 for the plain drawing, so the image is not faded by accident.
        string content = Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(doc.Pages[0].Dictionary["Contents"])!));
        int first = content.IndexOf("/Im1 Do", StringComparison.Ordinal);
        string beforeFirst = content.Substring(0, first);
        string opaque = states.First(s => ((PdfNumber)((PdfDictionary)doc.Resolve(s.Value)!)["ca"]!).ToDouble() == 1).Key;
        StringAssert.Contains(beforeFirst, "/" + opaque + " gs");
    }

    [TestMethod]
    public void OpacityOutOfRangeIsRejected()
    {
        PdfDocument doc = PdfDocument.Create();
        using PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => g.DrawImage(Image, 0, 0, 10, 10, PdfImageFit.Fill, opacity: 1.5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => g.DrawImage(Image, 0, 0, 10, 10, PdfImageFit.Fill, opacity: double.NaN));
    }
}
