using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Tests;

/// <summary>Stamps, ink, shapes, lines, polygons and file attachments.</summary>
[TestClass]
public sealed class MarkupAnnotationTests
{
    private static PdfDocument Reopen(PdfDocument doc)
    {
        byte[] bytes = doc.ToArray(new PdfSaveOptions { Optimize = false });
        Assert.IsFalse(PdfFile.Open(bytes, 64 << 20).WasReconstructed);
        return PdfDocument.Open(bytes);
    }

    private static double[] Numbers(PdfDocument doc, PdfObject? o) => [.. ((PdfArray)doc.Resolve(o)!).Select(v => doc.Resolve(v) is PdfNumber n ? n.ToDouble() : double.NaN)];

    private static PdfStream Appearance(PdfDocument doc, PdfAnnotation a) =>
        (PdfStream)doc.Resolve(((PdfDictionary)doc.Resolve(a.Dictionary["AP"])!)["N"])!;

    private static string AppearanceText(PdfDocument doc, PdfAnnotation a) => Encoding.ASCII.GetString(doc.GetStreamData(Appearance(doc, a)));

    private static string Name(PdfDocument doc, PdfObject? o) => ((PdfName)doc.Resolve(o)!).Value;

    [TestMethod]
    public void StandardStampsCarryTheirNameAndText()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        page.Annotations.AddStamp(PdfStampName.Approved, 400, 40, 150, 45, new PdfAnnotationOptions { Comment = "Checked", Author = "Reviewer", Subject = "Approval" });
        page.Annotations.AddStamp(PdfStampName.NotForPublicRelease, 50, 40, 300, 45);

        PdfDocument back = Reopen(doc);
        PdfAnnotation[] stamps = [.. back.Pages[0].Annotations];
        Assert.AreEqual("Stamp", stamps[0].Subtype);
        Assert.AreEqual("Approved", Name(back, stamps[0].Dictionary["Name"]));
        Assert.AreEqual("NotForPublicRelease", Name(back, stamps[1].Dictionary["Name"]));
        Assert.AreEqual(new PdfRectangle(400, 715, 550, 760), stamps[0].Rectangle);
        Assert.AreEqual("Checked", stamps[0].Contents);
        Assert.AreEqual("Reviewer", ((PdfString)back.Resolve(stamps[0].Dictionary["T"])!).ToText());
        Assert.AreEqual("Approval", ((PdfString)back.Resolve(stamps[0].Dictionary["Subj"])!).ToText());

        // Green for approval, red for restrictions; the text shows once the stamp is page content.
        double[] green = Numbers(back, stamps[0].Dictionary["C"]);
        Assert.AreEqual(0x15 / 255.0, green[0], 1e-3);
        Assert.AreEqual(0x80 / 255.0, green[1], 1e-3);
        Assert.AreEqual(0x3D / 255.0, green[2], 1e-3);
        Assert.AreEqual(0xB9 / 255.0, Numbers(back, stamps[1].Dictionary["C"])[0], 1e-3);
        back.FlattenAnnotations();
        string text = back.Pages[0].ExtractText();
        StringAssert.Contains(text, "APPROVED");
        StringAssert.Contains(text, "NOT FOR PUBLIC RELEASE");
    }

    [TestMethod]
    public void EveryStandardStampHasALabel()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 2000));
        PdfStampName[] all = [.. Enumerable.Range(0, 14).Select(i => (PdfStampName)i)];
        Assert.AreEqual(PdfStampName.ForPublicRelease, all[^1]);
        for (int i = 0; i < all.Length; i++) page.Annotations.AddStamp(all[i], 20, 20 + i * 60, 300, 40);
        doc.FlattenAnnotations();
        string text = page.ExtractText();
        foreach (string label in new[] { "EXPERIMENTAL", "AS IS", "EXPIRED", "CONFIDENTIAL", "FINAL", "SOLD", "DEPARTMENTAL", "FOR COMMENT", "TOP SECRET", "DRAFT", "FOR PUBLIC RELEASE" })
        {
            StringAssert.Contains(text, label);
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => page.Annotations.AddStamp((PdfStampName)99, 0, 0, 10, 10));
    }

    [TestMethod]
    public void OpacityFadesTheWholeAppearance()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfAnnotation square = page.Annotations.AddSquare(100, 100, 80, 40, new PdfAnnotationOptions { Opacity = 0.5, InteriorColor = PdfColor.FromHex("#FDE68A"), BorderWidth = 3 });

        PdfDocument back = Reopen(doc);
        PdfAnnotation a = back.Pages[0].Annotations.Single();
        Assert.AreEqual(0.5, ((PdfNumber)back.Resolve(a.Dictionary["CA"])!).ToDouble());

        // The outer appearance sets the alpha and paints a transparency group holding the drawing.
        PdfStream outer = Appearance(back, a);
        var resources = (PdfDictionary)back.Resolve(outer.Dictionary["Resources"])!;
        var states = (PdfDictionary)back.Resolve(resources["ExtGState"])!;
        double[] alphas = [.. states.Select(s => (PdfDictionary)back.Resolve(s.Value)!).Select(s => ((PdfNumber)back.Resolve(s["ca"] ?? s["CA"])!).ToDouble())];
        CollectionAssert.AreEqual(new[] { 0.5, 0.5 }, alphas);
        var group = (PdfStream)back.Resolve(((PdfDictionary)back.Resolve(resources["XObject"])!).Single().Value)!;
        Assert.AreEqual("Transparency", Name(back, ((PdfDictionary)back.Resolve(group.Dictionary["Group"])!)["S"]));
        Assert.IsNotNull(square.Dictionary["CA"]);

        // Opaque annotations draw straight into the appearance.
        PdfAnnotation opaque = page.Annotations.AddSquare(100, 200, 80, 40);
        Assert.IsNull(opaque.Dictionary["CA"]);
        Assert.IsNull(Appearance(doc, opaque).Dictionary["Group"]);
    }

    [TestMethod]
    public void CustomStampsFromAFormOrADrawing()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        PdfForm logo = PdfForm.Create(doc, 60, 20);
        using (PdfGraphics g = logo.CreateGraphics()) g.DrawString("LOGO", PdfStandardFont.HelveticaBold, 14, PdfColor.Black, 4, 15);
        PdfAnnotation fromForm = page.Annotations.AddStamp(300, 50, 120, 40, logo, "Logo");
        PdfAnnotation drawn = page.Annotations.AddStamp(300, 120, 120, 40, g =>
        {
            Assert.AreEqual(120, g.Width);
            Assert.AreEqual(40, g.Height);
            g.DrawRectangle(new PdfPen(PdfColor.FromHex("#7C3AED"), 2), null, 1, 1, 118, 38);
            g.DrawString("RECEIVED", PdfStandardFont.HelveticaBold, 18, PdfColor.FromHex("#7C3AED"), 12, 27);
        }, null, new PdfAnnotationOptions { Opacity = 0.75 });

        Assert.AreEqual("Logo", Name(doc, fromForm.Dictionary["Name"]));
        Assert.AreEqual("Custom", Name(doc, drawn.Dictionary["Name"]));
        StringAssert.Contains(AppearanceText(doc, fromForm), "Do");
        Assert.AreEqual(0.75, ((PdfNumber)drawn.Dictionary["CA"]!).ToDouble());

        PdfForm foreign = PdfForm.Create(PdfDocument.Create(), 10, 10);
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddStamp(0, 0, 10, 10, foreign));
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddStamp(0, 0, 10, 10, _ => { }, ""));

        PdfDocument back = Reopen(doc);
        back.FlattenAnnotations();
        string text = back.Pages[0].ExtractText();
        StringAssert.Contains(text, "LOGO");
        StringAssert.Contains(text, "RECEIVED");
    }

    [TestMethod]
    public void InkKeepsEveryStroke()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        PdfPoint[] first = [new(100, 100), new(120, 90), new(140, 110)];
        PdfPoint[] second = [new(100, 130), new(160, 130)];
        PdfPoint[] dot = [new(170, 95)];
        page.Annotations.AddInk([first, second, dot], new PdfAnnotationOptions { Color = PdfColor.FromHex("#2563EB"), BorderWidth = 2 });

        PdfDocument back = Reopen(doc);
        PdfAnnotation ink = back.Pages[0].Annotations.Single();
        Assert.AreEqual("Ink", ink.Subtype);
        var list = (PdfArray)back.Resolve(ink.Dictionary["InkList"])!;
        Assert.HasCount(3, list);
        CollectionAssert.AreEqual(new double[] { 100, 700, 120, 710, 140, 690 }, Numbers(back, list[0]));
        CollectionAssert.AreEqual(new double[] { 170, 705 }, Numbers(back, list[2]));

        // The rectangle holds the points plus half the stroke and a point of margin.
        Assert.AreEqual(new PdfRectangle(98, 668, 172, 712), ink.Rectangle);
        Assert.AreEqual(2, ((PdfNumber)((PdfDictionary)back.Resolve(ink.Dictionary["BS"])!)["W"]!).ToDouble());
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddInk([]));
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddInk([Array.Empty<PdfPoint>()]));
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddInk([[new PdfPoint(double.NaN, 0)]]));
    }

    [TestMethod]
    public void SquaresAndCirclesHaveBorderAndFill()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        page.Annotations.AddSquare(50, 60, 100, 40, new PdfAnnotationOptions { InteriorColor = PdfColor.FromHex("#FEF3C7"), BorderWidth = 2, DashPattern = [4, 2] });
        page.Annotations.AddCircle(200, 60, 80, 80, new PdfAnnotationOptions { Color = PdfColor.Rgb(0, 0, 1), BorderWidth = 0 });

        PdfDocument back = Reopen(doc);
        PdfAnnotation square = back.Pages[0].Annotations[0], circle = back.Pages[0].Annotations[1];
        Assert.AreEqual("Square", square.Subtype);
        Assert.AreEqual("Circle", circle.Subtype);
        Assert.AreEqual(new PdfRectangle(50, 700, 150, 740), square.Rectangle);
        CollectionAssert.AreEqual(new double[] { 0, 0, 1 }, Numbers(back, circle.Dictionary["C"]));
        Assert.IsNotNull(square.Dictionary["IC"]);
        Assert.IsNull(circle.Dictionary["IC"]);
        var bs = (PdfDictionary)back.Resolve(square.Dictionary["BS"])!;
        Assert.AreEqual("D", Name(back, bs["S"]));
        CollectionAssert.AreEqual(new double[] { 4, 2 }, Numbers(back, bs["D"]));

        // The border sits inside the box; a zero border draws no stroke.
        string squareContent = AppearanceText(back, square);
        StringAssert.Contains(squareContent, "1 1 98 38 re");
        StringAssert.Contains(squareContent, "[4 2] 0 d");
        string circleContent = AppearanceText(back, circle);
        Assert.IsFalse(circleContent.Contains(" S\n") || circleContent.Contains(" B\n"), circleContent);
    }

    [TestMethod]
    public void LinesCarryTheirEndsAndEndings()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        var options = new PdfAnnotationOptions
        {
            BorderWidth = 2,
            StartEnding = PdfLineEnding.Circle,
            EndEnding = PdfLineEnding.ClosedArrow,
            InteriorColor = PdfColor.FromHex("#DC2626"),
        };
        page.Annotations.AddLine(new PdfPoint(100, 300), new PdfPoint(250, 220), options);
        page.Annotations.AddLine(new PdfPoint(100, 400), new PdfPoint(300, 400));

        PdfDocument back = Reopen(doc);
        PdfAnnotation line = back.Pages[0].Annotations[0], plain = back.Pages[0].Annotations[1];
        Assert.AreEqual("Line", line.Subtype);
        CollectionAssert.AreEqual(new double[] { 100, 500, 250, 580 }, Numbers(back, line.Dictionary["L"]));
        var ends = (PdfArray)back.Resolve(line.Dictionary["LE"])!;
        CollectionAssert.AreEqual(new[] { "Circle", "ClosedArrow" }, ends.Select(e => ((PdfName)e).Value).ToArray());
        Assert.IsNull(plain.Dictionary["LE"]);

        // Endings of 4 times the width (8 points) fit inside the rectangle.
        PdfRectangle r = line.Rectangle;
        Assert.AreEqual(100 - 10, r.Left, 1e-9);
        Assert.AreEqual(250 + 10, r.Right, 1e-9);
        Assert.AreEqual(new PdfRectangle(98.5, 398.5, 301.5, 401.5), plain.Rectangle);

        foreach (PdfLineEnding ending in Enumerable.Range(0, 10).Select(i => (PdfLineEnding)i))
        {
            PdfAnnotation a = page.Annotations.AddLine(new PdfPoint(10, 10), new PdfPoint(60, 60), new PdfAnnotationOptions { StartEnding = ending, EndEnding = ending });
            string expected = ending switch { PdfLineEnding.ReverseOpenArrow => "ROpenArrow", PdfLineEnding.ReverseClosedArrow => "RClosedArrow", _ => ending.ToString() };
            if (ending != PdfLineEnding.None) Assert.AreEqual(expected, ((PdfName)((PdfArray)a.Dictionary["LE"]!)[1]).Value);
        }

        // Coinciding ends draw the line without endings rather than failing.
        page.Annotations.AddLine(new PdfPoint(10, 10), new PdfPoint(10, 10), options);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => page.Annotations.AddLine(default, new PdfPoint(1, 1), new PdfAnnotationOptions { EndEnding = (PdfLineEnding)42 }));
    }

    [TestMethod]
    public void PolygonsAndPolylines()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        PdfPoint[] triangle = [new(100, 100), new(200, 100), new(150, 20)];
        page.Annotations.AddPolygon(triangle, new PdfAnnotationOptions { InteriorColor = PdfColor.FromHex("#DBEAFE"), Color = PdfColor.FromHex("#1D4ED8") });
        page.Annotations.AddPolyline([new(300, 100), new(350, 50), new(400, 100), new(450, 50)], new PdfAnnotationOptions { EndEnding = PdfLineEnding.OpenArrow });

        PdfDocument back = Reopen(doc);
        PdfAnnotation polygon = back.Pages[0].Annotations[0], polyline = back.Pages[0].Annotations[1];
        Assert.AreEqual("Polygon", polygon.Subtype);
        Assert.AreEqual("PolyLine", polyline.Subtype);
        CollectionAssert.AreEqual(new double[] { 100, 700, 200, 700, 150, 780 }, Numbers(back, polygon.Dictionary["Vertices"]));
        Assert.IsNull(polygon.Dictionary["LE"]);
        CollectionAssert.AreEqual(new[] { "None", "OpenArrow" }, ((PdfArray)back.Resolve(polyline.Dictionary["LE"])!).Select(e => ((PdfName)e).Value).ToArray());
        StringAssert.Contains(AppearanceText(back, polygon), "h\n");

        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddPolygon([new(0, 0), new(1, 1)]));
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddPolyline([new(0, 0)]));
    }

    [TestMethod]
    public void FileAttachmentsEmbedTheFile()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(new PdfRectangle(0, 0, 600, 800));
        byte[] data = Encoding.UTF8.GetBytes("item,amount\nbook,12.50\n");
        page.Annotations.AddFileAttachment(500, 100, "totals.csv", data, "text/csv", PdfAttachmentIcon.Paperclip, new PdfAnnotationOptions { Comment = "Source figures" });
        foreach (PdfAttachmentIcon icon in Enumerable.Range(0, 4).Select(i => (PdfAttachmentIcon)i))
        {
            page.Annotations.AddFileAttachment(20 + 30 * (int)icon, 200, icon + ".txt", [1, 2, 3], icon: icon);
        }

        PdfDocument back = Reopen(doc);
        PdfAnnotation a = back.Pages[0].Annotations[0];
        Assert.AreEqual("FileAttachment", a.Subtype);
        Assert.AreEqual("Paperclip", Name(back, a.Dictionary["Name"]));
        Assert.AreEqual(new PdfRectangle(500, 680, 520, 700), a.Rectangle);
        Assert.AreEqual("Source figures", a.Contents);
        PdfAttachment file = a.Attachment!;
        Assert.AreEqual("totals.csv", file.FileName);
        Assert.AreEqual("text/csv", file.MimeType);
        Assert.AreEqual("Source figures", file.Description);
        CollectionAssert.AreEqual(data, file.GetData());

        // Without a comment the file name is the annotation's text; the attachment list stays empty.
        Assert.AreEqual("PushPin.txt", back.Pages[0].Annotations[1].Contents);
        Assert.IsEmpty(back.Attachments);
        Assert.IsNotNull(back.Pages[0].Annotations[1].Attachment);
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddFileAttachment(0, 0, "", []));
    }

    [TestMethod]
    public void OptionsAreChecked()
    {
        PdfPage page = PdfDocument.Create().Pages.Add(PdfRectangle.A4);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => page.Annotations.AddSquare(0, 0, 10, 10, new PdfAnnotationOptions { Opacity = 1.5 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => page.Annotations.AddSquare(0, 0, 10, 10, new PdfAnnotationOptions { Opacity = double.NaN }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => page.Annotations.AddSquare(0, 0, 10, 10, new PdfAnnotationOptions { BorderWidth = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => page.Annotations.AddSquare(0, 0, 10, 10, new PdfAnnotationOptions { BorderWidth = double.PositiveInfinity }));
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddSquare(0, 0, 10, 10, new PdfAnnotationOptions { DashPattern = [0, 0] }));
        Assert.ThrowsExactly<ArgumentException>(() => page.Annotations.AddSquare(0, 0, 10, 10, new PdfAnnotationOptions { DashPattern = [-1] }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => page.Annotations.AddCircle(0, 0, 0, 10));

        // A border wider than the shape is narrowed to fit it.
        PdfAnnotation thin = page.Annotations.AddSquare(0, 0, 4, 4, new PdfAnnotationOptions { BorderWidth = 50 });
        StringAssert.Contains(AppearanceText(page.Document, thin), "2 w");
    }

    [TestMethod]
    public void EveryNewAnnotationRendersInAViewerAfterSaving()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        page.Annotations.AddStamp(PdfStampName.Draft, 50, 50, 120, 40);
        page.Annotations.AddInk([[new(50, 120), new(90, 140)]]);
        page.Annotations.AddSquare(50, 160, 40, 30);
        page.Annotations.AddCircle(100, 160, 40, 30);
        page.Annotations.AddLine(new(50, 220), new(150, 220), new PdfAnnotationOptions { EndEnding = PdfLineEnding.ClosedArrow });
        page.Annotations.AddPolygon([new(50, 260), new(90, 260), new(70, 240)]);
        page.Annotations.AddPolyline([new(100, 260), new(140, 240), new(180, 260)]);
        page.Annotations.AddFileAttachment(200, 50, "a.txt", [65]);

        // With the optimizer on, as by default: every annotation and its appearance survives.
        PdfDocument back = PdfDocument.Open(doc.ToArray());
        PdfAnnotation[] all = [.. back.Pages[0].Annotations];
        CollectionAssert.AreEqual(new[] { "Stamp", "Ink", "Square", "Circle", "Line", "Polygon", "PolyLine", "FileAttachment" }, all.Select(a => a.Subtype).ToArray());
        foreach (PdfAnnotation a in all)
        {
            Assert.IsInstanceOfType<PdfStream>(back.Resolve(((PdfDictionary)back.Resolve(a.Dictionary["AP"])!)["N"]), a.Subtype);
            Assert.AreEqual(4, ((PdfNumber)back.Resolve(a.Dictionary["F"])!).IntegerValue, a.Subtype);
        }
    }
}
