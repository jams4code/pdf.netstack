using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class GraphicsTests
{
    private static readonly PdfSaveOptions Plain = new() { Optimize = false };

    /// <summary>All content streams of the first page, decoded and concatenated.</summary>
    private static string Content(PdfDocument doc, int page = 0)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.ASCII.GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(Plain);
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    [TestMethod]
    public void CanvasFlipsToTopLeftOrigin()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 500, 400));
        using (PdfGraphics g = page.CreateGraphics())
        {
            Assert.AreEqual(500, g.Width);
            Assert.AreEqual(400, g.Height);
            g.DrawRectangle(null, PdfColor.Rgb(1, 0, 0), 10, 20, 30, 40);
        }

        string content = Content(Reopen(doc));
        StringAssert.StartsWith(content, "q\n1 0 0 -1 0 400 cm\n");
        StringAssert.Contains(content, "1 0 0 rg\n10 20 30 40 re\nf\n");
        StringAssert.EndsWith(content, "Q\n");
    }

    [TestMethod]
    public void StateIsWrittenOnlyWhenItChanges()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        var pen = new PdfPen(PdfColor.Rgb(0, 0, 1), 2) { LineCap = PdfLineCap.Round, DashPattern = [3, 1.5] };
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawLine(pen, 0, 0, 100, 0);
            g.DrawLine(pen, 0, 10, 100, 10);
            g.DrawLine(pen with { Width = 0.5 }, 0, 20, 100, 20);
        }

        string content = Content(doc);
        Assert.AreEqual(1, Count(content, " RG\n"));
        Assert.AreEqual(1, Count(content, "2 w\n"));
        Assert.AreEqual(1, Count(content, ".5 w\n"));
        Assert.AreEqual(1, Count(content, "1 J\n"));
        Assert.AreEqual(1, Count(content, "[3 1.5] 0 d\n"));
        Assert.AreEqual(3, Count(content, "S\n"));
    }

    [TestMethod]
    public void SavedStateIsRestored()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            using (g.Save())
            {
                g.TranslateTransform(50, 60);
                g.RotateTransform(90);
                Assert.AreEqual(new PdfMatrix(0, 1, -1, 0, 50, 60), g.Transform);
                g.DrawRectangle(null, PdfColor.Gray(0.5), 0, 0, 10, 10);
            }

            Assert.AreEqual(PdfMatrix.Identity, g.Transform);

            // The fill color was set inside the saved state, so it must be set again after it.
            g.DrawRectangle(null, PdfColor.Gray(0.5), 0, 0, 10, 10);
            g.Save();
            g.Save();
        }

        string content = Content(doc);
        Assert.AreEqual(2, Count(content, ".5 g\n"));
        StringAssert.Contains(content, "1 0 0 1 50 60 cm\n0 1 -1 0 0 0 cm\n");
        Assert.AreEqual(Count(content, "q\n"), Count(content, "Q\n"), "unbalanced saves are closed on dispose");
    }

    [TestMethod]
    public void OpacityUsesSharedGraphicsStates()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawRectangle(null, PdfColor.Rgb(1, 0, 0, 0.5), 0, 0, 10, 10);
            g.DrawRectangle(null, PdfColor.Rgb(0, 1, 0), 0, 0, 10, 10);
            g.DrawRectangle(null, PdfColor.Rgb(0, 0, 1, 0.5), 0, 0, 10, 10);
            g.DrawLine(new PdfPen(PdfColor.Black.WithAlpha(0.25)), 0, 0, 1, 1);
        }

        var resources = (PdfDictionary)doc.Resolve(page.Dictionary["Resources"])!;
        var states = (PdfDictionary)doc.Resolve(resources["ExtGState"])!;
        Assert.HasCount(3, states, "fill .5, fill 1, stroke .25; fill .5 reused");
        string content = Content(doc);
        Assert.AreEqual(4, Count(content, " gs\n"));
    }

    [TestMethod]
    public void GradientsBecomeShadings()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        using (PdfGraphics g = page.CreateGraphics())
        {
            var linear = new PdfLinearGradientBrush(new PdfPoint(0, 0), new PdfPoint(100, 0),
                [new(0, PdfColor.FromHex("#1E3A8A")), new(0.5, PdfColor.White), new(0.5, PdfColor.Black), new(1, PdfColor.Gray(0.5))]);
            g.DrawRoundedRectangle(PdfPen.Black, linear, 10, 10, 100, 50, 8);
            g.DrawEllipse(null, new PdfRadialGradientBrush(new PdfPoint(50, 150), 40, PdfColor.White, PdfColor.FromHex("#14B8A6")), 10, 110, 80, 80);
        }

        PdfDocument reopened = Reopen(doc);
        var resources = (PdfDictionary)reopened.Resolve(reopened.Pages[0].Dictionary["Resources"])!;
        var shadings = (PdfDictionary)reopened.Resolve(resources["Shading"])!;
        Assert.HasCount(2, shadings);
        var first = (PdfDictionary)reopened.Resolve(shadings["Sh1"])!;
        Assert.AreEqual("2", first["ShadingType"]!.ToString());
        Assert.AreEqual("DeviceRGB", first.GetName("ColorSpace"), "mixed gray and RGB stops convert to RGB");
        var function = (PdfDictionary)reopened.Resolve(first["Function"])!;
        Assert.AreEqual("3", function["FunctionType"]!.ToString());
        var bounds = (PdfArray)reopened.Resolve(function["Bounds"])!;
        Assert.IsGreaterThan(((PdfNumber)bounds[0]).ToDouble(), ((PdfNumber)bounds[1]).ToDouble(), "hard stop bounds must increase");

        string content = Content(reopened);
        StringAssert.Contains(content, "W\nn\n/Sh1 sh\n");
        StringAssert.Contains(content, "/Sh2 sh\n");
        StringAssert.Contains(content, "\nS\n", "the outline of the gradient shape is stroked separately");
    }

    [TestMethod]
    public void ExistingContentIsWrappedOrUnderlaid()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Dictionary.Set("Contents", doc.AddObject(PdfStream.FromData("1 0 0 1 300 300 cm\n"u8)));

        using (PdfGraphics g = page.CreateGraphics())
        {
            g.DrawRectangle(null, PdfColor.Black, 0, 0, 1, 1);
        }

        using (PdfGraphics g = page.CreateGraphics(PdfGraphicsLayer.Background))
        {
            g.DrawRectangle(null, PdfColor.White, 0, 0, 1, 1);
        }

        string content = Content(Reopen(doc));
        int background = content.IndexOf("1 g\n", StringComparison.Ordinal);
        int original = content.IndexOf("300 300 cm", StringComparison.Ordinal);
        int foreground = content.IndexOf("0 0 1 1 re\nf\nQ\n", original, StringComparison.Ordinal);
        Assert.IsTrue(background >= 0 && background < original && original < foreground, content);
        StringAssert.Contains(content, "q\n1 0 0 1 300 300 cm\nQ\n", "the old content is enclosed in q and Q");
    }

    [TestMethod]
    public void SharedResourcesAreNotChanged()
    {
        PdfDocument doc = PdfDocument.Create();
        var shared = new PdfDictionary { ["ExtGState"] = new PdfDictionary() };
        PdfReference sharedRef = doc.AddObject(shared);
        PdfPage a = doc.Pages.Add(PdfRectangle.A4);
        PdfPage b = doc.Pages.Add(PdfRectangle.A4);
        a.Dictionary.Set("Resources", sharedRef);
        b.Dictionary.Set("Resources", sharedRef);

        using (PdfGraphics g = a.CreateGraphics())
        {
            g.DrawRectangle(null, PdfColor.Black.WithAlpha(0.5), 0, 0, 1, 1);
        }

        Assert.IsEmpty((PdfDictionary)shared["ExtGState"]!);
        Assert.AreSame(sharedRef, b.Dictionary["Resources"]);
        Assert.IsInstanceOfType<PdfDictionary>(a.Dictionary["Resources"]);
    }

    [TestMethod]
    public void UndisposedGraphicsAreWrittenOnSave()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfGraphics g = doc.Pages.Add(PdfRectangle.A4).CreateGraphics();
        g.DrawEllipse(PdfPen.Black, null, 0, 0, 50, 50);

        StringAssert.Contains(Content(Reopen(doc)), " c\n");
        Assert.ThrowsExactly<ObjectDisposedException>(() => g.DrawLine(PdfPen.Black, 0, 0, 1, 1));
    }

    [TestMethod]
    public void PathGeometry()
    {
        // Radii that do not fit are scaled together: 60 + 60 on a 100 wide side becomes 50 + 50.
        var w = new ContentWriter();
        new PdfPath().AddRoundedRectangle(0, 0, 100, 100, 60).Write(w);
        string s = Encoding.ASCII.GetString(w.Written.ToArray());
        StringAssert.StartsWith(s, "50 0 m\n50 0 l\n");

        // A full circle as four quarter curves, ending where it started.
        w = new ContentWriter();
        PdfPath circle = new PdfPath().AddCircle(10, 10, 10);
        circle.Write(w);
        Assert.AreEqual(4, Count(Encoding.ASCII.GetString(w.Written.ToArray()), " c\n"));
        Assert.AreEqual(new PdfPoint(20, 10), circle.CurrentPoint);

        // Arcs split into quarter turns at most.
        w = new ContentWriter();
        new PdfPath().AddArc(0, 0, 10, 10, 0, 270).Write(w);
        Assert.AreEqual(3, Count(Encoding.ASCII.GetString(w.Written.ToArray()), " c\n"));

        Assert.ThrowsExactly<InvalidOperationException>(() => new PdfPath().LineTo(1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfPath().MoveTo(double.NaN, 0));
    }

    [TestMethod]
    public void NumbersAreCompact()
    {
        var w = new ContentWriter();
        w.Number(0).Number(-0.000001).Number(0.5).Number(-0.25).Number(12.000004).Number(1234567.891).Number(1e17).Op("x");
        Assert.AreEqual("0 0 .5 -.25 12 1234567.891 100000000000000000 x\n", Encoding.ASCII.GetString(w.Written.ToArray()));
    }

    [TestMethod]
    public void ColorsAndPensValidate()
    {
        Assert.AreEqual(PdfColor.FromRgb(0x1E, 0x3A, 0x8A), PdfColor.FromHex("#1E3A8A"));
        Assert.AreEqual(PdfColor.FromRgb(255, 255, 255, 0.5), PdfColor.FromHex("fff8").WithAlpha(0.5));
        Assert.AreEqual(PdfColor.Rgb(1.0, 0, 0), PdfColor.Rgb(1, 0, 0), "integer arguments mean 0 to 1, not 0 to 255");
        Assert.AreEqual(0x80 / 255.0, PdfColor.FromHex("#00000080").Alpha);
        Assert.ThrowsExactly<FormatException>(() => PdfColor.FromHex("#12"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfColor.Rgb(1.5, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PdfPen(PdfColor.Black, -1));
        Assert.ThrowsExactly<ArgumentException>(() => new PdfPen(PdfColor.Black) { DashPattern = [0, 0] });
        Assert.ThrowsExactly<NotSupportedException>(() => new PdfLinearGradientBrush(default, new PdfPoint(1, 0), PdfColor.Black, PdfColor.White.WithAlpha(0.5)));
        Assert.AreEqual(PdfColor.Rgb(0, 0, 0), PdfColor.Cmyk(0, 0, 0, 1).ToRgb());
    }

    private static int Count(string text, string part)
    {
        int n = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
