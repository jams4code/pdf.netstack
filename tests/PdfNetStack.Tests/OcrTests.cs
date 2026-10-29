using System.Text;
using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;
using PdfNetStack.Ocr;
using PdfNetStack.Ocr.Processing;

namespace PdfNetStack.Tests;

[TestClass]
public sealed class OcrTests
{
    // These tests read the drawing operators as written, before the optimizer compacts them.
    private static readonly PdfSaveOptions Unoptimized = new() { Optimize = false };

    private const int ScanWidth = 200;
    private const int ScanHeight = 300;

    /// <summary>
    /// A white page with a black block in its top left corner, to tell which way is up, stored
    /// turned clockwise by <paramref name="turns"/> quarters as a scanner may store it.
    /// </summary>
    private static PdfImage MarkedScan(int turns = 0)
    {
        var pixels = new byte[ScanWidth * ScanHeight];
        pixels.AsSpan().Fill(255);
        for (int y = 10; y < 30; y++) pixels.AsSpan((y * ScanWidth) + 10, 20).Clear();
        (byte[] stored, int w, int h) = ImageOps.Rotate(pixels, ScanWidth, ScanHeight, turns);
        return PdfImage.Load(Imaging.PngWriter.Write(w, h, 1, stored));
    }

    /// <summary>Finds the black block, says whether it is where the top left is, and reads it as one word.</summary>
    private sealed class BlockEngine : IOcrEngine
    {
        public List<(int Width, int Height, bool BlockTopLeft, string Language)> Calls { get; } = [];

        public double Confidence { get; set; } = 0.99;

        public Func<string, string> Text { get; set; } = _ => "block";

        public OcrImageResult Recognize(OcrBitmap image, OcrRequest request, CancellationToken cancellationToken)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    if (image.Pixels[(y * image.Width) + x] >= 128) continue;
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x + 1), Math.Max(maxY, y + 1));
                }
            }

            lock (Calls) Calls.Add((image.Width, image.Height, minX < image.Width / 2 && minY < image.Height / 2, request.Language));
            if (maxX < 0) return new OcrImageResult([]);
            PdfPoint[] quad = [new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)];
            string text = Text(request.Language);
            return new OcrImageResult([new OcrLine(text, quad, Confidence, [new OcrWord(text, quad, Confidence)], false)]);
        }

        public void Dispose()
        {
        }
    }

    private static PdfDocument Scanned(Action<PdfDocument> build)
    {
        PdfDocument doc = PdfDocument.Create();
        build(doc);
        return PdfDocument.Open(doc.ToArray(Unoptimized));
    }

    private static PdfPoint Center(PdfPoint[] quad) => new(quad.Average(p => p.X), quad.Average(p => p.Y));

    private static string Content(PdfDocument doc, int page)
    {
        PdfObject? contents = doc.Resolve(doc.Pages[page].Dictionary["Contents"]);
        IEnumerable<PdfObject> parts = contents is PdfArray a ? a : [contents!];
        return string.Concat(parts.Select(p => Encoding.GetEncoding("ISO-8859-1").GetString(doc.GetStreamData((PdfStream)doc.Resolve(p)!))));
    }

    [TestMethod]
    public void FindsTheSmallestRectangleAroundPoints()
    {
        // A 40 by 10 rectangle turned 30 degrees, sampled densely, plus its inside.
        double angle = Math.PI / 6, c = Math.Cos(angle), s = Math.Sin(angle);
        var points = new List<PdfPoint>();
        for (int u = 0; u <= 40; u++)
        {
            for (int v = 0; v <= 10; v++) points.Add(new PdfPoint(100 + (u * c) - (v * s), 50 + (u * s) + (v * c)));
        }

        (PdfPoint[] corners, double width, double height) = Geometry.MinAreaRectangle(points);
        Assert.AreEqual(40, Math.Max(width, height), 1e-6);
        Assert.AreEqual(10, Math.Min(width, height), 1e-6);
        Assert.HasCount(4, corners);

        // On an exact grid the hull drops every point but the corners.
        List<PdfPoint> grid = [.. Enumerable.Range(0, 50).Select(i => new PdfPoint(i % 10, i / 10))];
        Assert.HasCount(4, Geometry.ConvexHull(grid));

        PdfPoint[] ordered = Geometry.Ordered([new(10, 20), new(0, 0), new(10, 0), new(0, 20)]);
        CollectionAssert.AreEqual(new PdfPoint[] { new(0, 0), new(10, 0), new(10, 20), new(0, 20) }, ordered);
        Assert.IsTrue(Geometry.Inside(ordered, 5, 5));
        Assert.IsFalse(Geometry.Inside(ordered, 15, 5));
    }

    [TestMethod]
    public void TurnsAProbabilityMapIntoBoxesInReadingOrder()
    {
        // Two text lines on a 100 by 60 map, the lower one first in memory order on the right.
        const int W = 100, H = 60;
        var map = new float[W * H];
        void Fill(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++) map[(y * W) + x] = 0.9f;
            }
        }

        Fill(10, 40, 90, 48);
        Fill(10, 10, 60, 18);
        map[(2 * W) + 2] = 0.9f; // a speck: too small to be text

        List<PdfPoint[]> boxes = TextBoxes.Find(map, W, H, 0.3, 0.6, 1.5, 2, 2, 2 * W, 2 * H);
        Assert.HasCount(2, boxes);

        // Reading order, image scale (2x), and grown past the region by the unclip margin.
        Assert.IsLessThan(boxes[1][0].Y, boxes[0][0].Y);
        Assert.IsLessThan(20, boxes[0][0].X);
        Assert.IsGreaterThan(118, boxes[0][1].X);
        Assert.IsLessThan(20, boxes[0][0].Y);
        Assert.IsGreaterThan(34, boxes[0][3].Y);
    }

    [TestMethod]
    public void DecodesCtcAndOrdersArabic()
    {
        // Columns: a a blank b space c; classes: blank, a, b, c, space.
        int[] best = [1, 1, 0, 2, 4, 3];
        var scores = new float[best.Length * 5];
        for (int t = 0; t < best.Length; t++) scores[(t * 5) + best[t]] = 0.9f;
        List<ReadChar> chars = Ctc.Decode(scores, best.Length, 5, ["a", "b", "c"]);
        Assert.AreEqual("ab c", string.Concat(chars.Select(c => c.Value)));
        Assert.AreEqual(4, chars[2].Column);

        // Visual to logical: Arabic letters reversed, numbers and Latin kept in their order.
        Assert.AreEqual("مرحبا", Ctc.Logical("ابحرم"));
        Assert.AreEqual("المبلغ 12.50", Ctc.Logical("12.50 غلبملا"));
        Assert.IsTrue(Ctc.HasRightToLeft("PDF م"));
        Assert.IsFalse(Ctc.HasRightToLeft("PDF ١٢"));
    }

    [TestMethod]
    public void RotatesAndCropsImages()
    {
        byte[] pixels = [.. Enumerable.Range(0, 12).Select(i => (byte)i)];
        (byte[] once, int w, int h) = ImageOps.Rotate(pixels, 4, 3, 1);
        Assert.AreEqual(3, w);
        Assert.AreEqual(4, h);
        Assert.AreEqual(8, once[0], "the bottom left corner turns to the top left");
        byte[] back = pixels;
        int bw = 4, bh = 3;
        for (int i = 0; i < 4; i++) (back, bw, bh) = ImageOps.Rotate(back, bw, bh, 1);
        CollectionAssert.AreEqual(pixels, back);

        // An axis aligned crop at the same scale is the sub image.
        byte[] crop = ImageOps.Crop(pixels, 4, 3, [new(1, 1), new(3, 1), new(3, 3), new(1, 3)], 2, 2);
        CollectionAssert.AreEqual(new byte[] { 5, 6, 9, 10 }, crop);
    }

    [TestMethod]
    public void ReadsScansUprightWhereverThePageTurnsThem()
    {
        PdfImage scan = MarkedScan();
        PdfDocument doc = Scanned(d =>
        {
            // Stored upright and drawn as stored.
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics()) g.DrawImage(scan, 0, 0, ScanWidth, ScanHeight);

            // Stored a quarter turn counterclockwise, drawn a quarter turn clockwise: upright on the page.
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics())
            {
                g.TranslateTransform(ScanWidth, 0);
                g.RotateTransform(90);
                g.DrawImage(MarkedScan(3), 0, 0, ScanHeight, ScanWidth);
            }

            // Stored upright, drawn turned on a landscape page, and the page shown turned back.
            PdfPage page = d.Pages.Add(PdfRectangle.FromSize(ScanHeight, ScanWidth));
            using (PdfGraphics g = page.CreateGraphics())
            {
                g.TranslateTransform(ScanHeight, 0);
                g.RotateTransform(90);
                g.DrawImage(scan, 0, 0, ScanWidth, ScanHeight);
            }

            page.Rotate(270);
        });

        var engine = new BlockEngine();
        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { MaxDegreeOfParallelism = 1 }).MakeSearchable(doc);
        Assert.HasCount(3, report.Pages);
        Assert.IsTrue(report.Pages.All(p => p.Status == PdfOcrPageStatus.Done), string.Join(", ", report.Pages.Select(p => p.Status + " " + p.Reason)));

        // The engine saw each page as a reader does: upright, block top left.
        Assert.IsTrue(engine.Calls.All(c => c.BlockTopLeft && c.Width == ScanWidth && c.Height == ScanHeight), string.Join(", ", engine.Calls));

        // Mapped back to page space (y up, before /Rotate): the block's center, 20 points in from
        // the corner that is the top left as the page is shown.
        foreach (int i in new[] { 0, 1 })
        {
            PdfPoint center = Center(report.Pages[i].Lines[0].Words[0].Quad);
            Assert.AreEqual(20, center.X, 0.5, $"page {i}");
            Assert.AreEqual(ScanHeight - 20, center.Y, 0.5, $"page {i}");
        }

        // Shown turned 270 degrees clockwise, the top left of the view is page space's top right.
        PdfPoint shown = Center(report.Pages[2].Lines[0].Quad);
        Assert.AreEqual(ScanHeight - 20, shown.X, 0.5);
        Assert.AreEqual(ScanWidth - 20, shown.Y, 0.5);

        // The words are written as invisible text, and a second pass leaves the pages alone.
        PdfDocument saved = PdfDocument.Open(doc.ToArray(Unoptimized));
        StringAssert.Contains(Content(saved, 0), "3 Tr");
        StringAssert.Contains(Content(saved, 0), "<0062006C006F0063006B>Tj");
        PdfOcrReport again = new PdfOcr(engine).MakeSearchable(saved);
        Assert.IsTrue(again.Pages.All(p => p.Status == PdfOcrPageStatus.SkippedHasText));
    }

    [TestMethod]
    public void SkipsPagesItShouldNotRead()
    {
        PdfImage scan = MarkedScan();
        PdfDocument doc = Scanned(d =>
        {
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawString("Born digital", PdfStandardFont.Helvetica, 12, PdfColor.Black, 50, 50);
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.A4).CreateGraphics()) g.DrawImage(scan, 50, 50, 40, 60);

            // A full page image in an encoding that is not decoded.
            PdfPage jpx = d.Pages.Add(PdfRectangle.FromSize(100, 100));
            var dict = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(100),
                ["Height"] = PdfNumber.FromInteger(100),
                ["ColorSpace"] = new PdfName("DeviceGray"),
                ["BitsPerComponent"] = PdfNumber.FromInteger(1),
                ["Filter"] = new PdfName("UnknownDecode"),
            };
            jpx.Dictionary.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["J"] = d.AddObject(new PdfStream(dict, new byte[] { 1, 2, 3 })) } });
            jpx.Dictionary.Set("Contents", d.AddObject(PdfStream.FromData("q 100 0 0 100 0 0 cm /J Do Q"u8)));
        });

        PdfOcrReport report = new PdfOcr(new BlockEngine()).Recognize(doc);
        CollectionAssert.AreEqual(
            new[] { PdfOcrPageStatus.SkippedHasText, PdfOcrPageStatus.SkippedNoScan, PdfOcrPageStatus.SkippedUnsupported },
            report.Pages.Select(p => p.Status).ToArray());
        StringAssert.Contains(report.Pages[2].Reason, "UnknownDecode");

        // Without skipping, the text page has no scan either.
        PdfOcrReport all = new PdfOcr(new BlockEngine(), new PdfOcrOptions { SkipPagesWithText = false }).Recognize(doc);
        Assert.AreEqual(PdfOcrPageStatus.SkippedNoScan, all.Pages[0].Status);
    }

    [TestMethod]
    public void KeepsTheMostConfidentReadingAndOnlyWritesWhenAsked()
    {
        PdfImage scan = MarkedScan();
        PdfDocument doc = Scanned(d =>
        {
            using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics();
            g.DrawImage(scan, 0, 0, ScanWidth, ScanHeight);
        });
        string before = Content(doc, 0);

        // Each language reads the same box; the more confident reading wins.
        var engine = new BlockEngine { Text = language => language };
        var options = new PdfOcrOptions { Languages = ["latin", "arabic"] };
        engine.Confidence = 0.8;
        PdfOcrReport report = new PdfOcr(engine, options).Recognize(doc);
        Assert.AreEqual("latin", report.Pages[0].Text, "a tie keeps the first reading");
        Assert.AreEqual(before, Content(doc, 0), "Recognize leaves the document as it was");
        CollectionAssert.AreEquivalent(new[] { "latin", "arabic" }, engine.Calls.Select(c => c.Language).ToArray());

        // Lines under the confidence floor are dropped.
        PdfOcrReport doubtful = new PdfOcr(new BlockEngine { Confidence = 0.3 }).Recognize(doc);
        Assert.AreEqual(PdfOcrPageStatus.Done, doubtful.Pages[0].Status);
        Assert.IsEmpty(doubtful.Pages[0].Lines);
    }

    /// <summary>The models, if <c>PDFNETSTACK_OCR_MODELS</c> names their folder; the tests that need them are inconclusive without.</summary>
    private static string Models()
    {
        string? folder = Environment.GetEnvironmentVariable("PDFNETSTACK_OCR_MODELS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) Assert.Inconclusive("Set PDFNETSTACK_OCR_MODELS to a folder with the PP-OCRv5 models (tools/fetch_ocr_models.py).");
        return folder!;
    }

    private static readonly string[] Expected =
    [
        "Invoice 2026-114",
        "Total amount due: 1,250.00 EUR by 30 November",
        "The quick brown fox jumps over the lazy dog.",
    ];

    private const string Arabic = "مرحبا بالعالم، هذا نص عربي";

    [TestMethod]
    public void ReadsLatinAndArabicWithTheModels()
    {
        string models = Models();
        using OnnxOcrEngine engine = OnnxOcrEngine.Load(models, new OnnxOcrEngineOptions { Languages = ["latin", "arabic"] });
        PdfImage page = PdfImage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", "page.png"));
        PdfDocument doc = Scanned(d =>
        {
            using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(420, 595)).CreateGraphics();
            g.DrawImage(page, 0, 0, 420, 595);
        });

        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = ["latin", "arabic"] }).MakeSearchable(doc);
        string[] lines = [.. report.Pages[0].Lines.Select(l => l.Text)];
        foreach (string line in Expected) CollectionAssert.Contains(lines, line, string.Join(" | ", lines));
        CollectionAssert.Contains(lines, Arabic, string.Join(" | ", lines));
        Assert.IsTrue(report.Pages[0].Lines.Single(l => l.Text == Arabic).RightToLeft);

        // The heading sits at the top of the page, in page space.
        PdfPoint heading = Center(report.Pages[0].Lines.Single(l => l.Text == Expected[0]).Quad);
        Assert.IsGreaterThan(500, heading.Y);

        // The page is now searchable: its text extracts back, Arabic in logical order.
        string text = PdfDocument.Open(doc.ToArray()).Pages[0].ExtractText();
        foreach (string line in Expected) StringAssert.Contains(text, line);
        StringAssert.Contains(text, Arabic);
    }

    [TestMethod]
    public void TurnsUpsideDownLinesWithTheModels()
    {
        string models = Models();
        using OnnxOcrEngine engine = OnnxOcrEngine.Load(models);
        PdfDocument doc = Scanned(d =>
        {
            using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(420, 595)).CreateGraphics();
            g.DrawImage(PdfImage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", "page.png")), 0, 0, 420, 595);
        });
        OcrBitmap upright = OcrBitmap.FromPdfBitmap(doc.Pages[0].GetImages()[0].Decode()!);

        // The pixels themselves upside down, with nothing in the PDF to say so: only the line model can tell.
        (byte[] turned, int w, int h) = ImageOps.Rotate(upright.Pixels, upright.Width, upright.Height, 2);
        string[] lines = [.. engine.Recognize(new OcrBitmap(w, h, turned), new OcrRequest(), default).Lines.Select(l => l.Text)];
        foreach (string line in Expected) CollectionAssert.Contains(lines, line, string.Join(" | ", lines));
    }
}
