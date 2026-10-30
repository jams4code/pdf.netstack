using System.Text;
using System.Text.RegularExpressions;
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
    private static PdfImage MarkedScan(int turns = 0, int width = ScanWidth, int height = ScanHeight)
    {
        (byte[] stored, int w, int h) = ImageOps.Rotate(MarkedPixels(width, height), width, height, turns);
        return PdfImage.Load(Imaging.PngWriter.Write(w, h, 1, stored));
    }

    /// <summary>The marked scan's pixels: white, the block at 10 to 30 across and down.</summary>
    private static byte[] MarkedPixels(int width = ScanWidth, int height = ScanHeight)
    {
        var pixels = new byte[width * height];
        pixels.AsSpan().Fill(255);
        for (int y = 10; y < 30; y++) pixels.AsSpan((y * width) + 10, 20).Clear();
        return pixels;
    }

    /// <summary>A rectangle of the marked scan as an image of its own, as a scanner writing strips or tiles stores it.</summary>
    private static PdfImage MarkedPiece(int x, int y, int width, int height)
    {
        byte[] pixels = MarkedPixels();
        var piece = new byte[width * height];
        for (int row = 0; row < height; row++) Array.Copy(pixels, ((y + row) * ScanWidth) + x, piece, row * width, width);
        return PdfImage.Load(Imaging.PngWriter.Write(width, height, 1, piece));
    }

    /// <summary>
    /// Finds the black block, says whether it is where the top left is, and reads it as one word.
    /// A block in another corner is a page turned that many quarters clockwise from the top left,
    /// and reads as a line running down, left or up, as the detector and line model report one.
    /// </summary>
    private sealed class BlockEngine : IOcrEngine
    {
        public List<(int Width, int Height, bool BlockTopLeft, string Language)> Calls { get; } = [];

        public List<OcrBitmap> Images { get; } = [];

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

            lock (Calls)
            {
                Calls.Add((image.Width, image.Height, minX < image.Width / 2 && minY < image.Height / 2, request.Language));
                Images.Add(image);
            }

            if (maxX < 0) return new OcrImageResult([]);
            bool left = minX < image.Width / 2, top = minY < image.Height / 2;
            int quarter = top ? (left ? 0 : 1) : (left ? 3 : 2);
            PdfPoint[] corners = [new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)];
            PdfPoint[] quad = [.. Enumerable.Range(0, 4).Select(i => corners[(i + quarter) % 4])];
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

    /// <summary>The marked scan's pixels turned clockwise by <paramref name="turns"/> quarters, drawn upright filling a page of their size.</summary>
    private static void AddTurnedPage(PdfDocument d, int turns, int width = ScanWidth, int height = ScanHeight)
    {
        PdfImage scan = MarkedScan(turns, width, height);
        using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(scan.PixelWidth, scan.PixelHeight)).CreateGraphics();
        g.DrawImage(scan, 0, 0, scan.PixelWidth, scan.PixelHeight);
    }

    [TestMethod]
    public void ReadsScansTurnedWithNothingSayingSo()
    {
        PdfDocument doc = Scanned(d =>
        {
            for (int turns = 0; turns < 4; turns++) AddTurnedPage(d, turns);

            // Turned a half turn in the pixels, and the page shown a quarter turn clockwise: as shown, three quarters.
            PdfPage page = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight));
            using (PdfGraphics g = page.CreateGraphics()) g.DrawImage(MarkedScan(2), 0, 0, ScanWidth, ScanHeight);
            page.Rotate(90);
        });

        var engine = new BlockEngine();
        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { MaxDegreeOfParallelism = 1 }).MakeSearchable(doc);
        Assert.IsTrue(report.Pages.All(p => p.Status == PdfOcrPageStatus.Done), string.Join(", ", report.Pages.Select(p => p.Status + " " + p.Reason)));
        CollectionAssert.AreEqual(new[] { 0, 90, 180, 270, 270 }, report.Pages.Select(p => p.Orientation).ToArray());

        // An upright page is read once; a turned one once as it is, in four orientations, and once turned back.
        Assert.HasCount(1 + (4 * 6), engine.Calls);
        Assert.IsTrue(engine.Calls[0].BlockTopLeft);
        for (int page = 1; page < 5; page++)
        {
            List<(int Width, int Height, bool BlockTopLeft, string Language)> calls = engine.Calls.GetRange(1 + ((page - 1) * 6), 6);
            Assert.IsFalse(calls[0].BlockTopLeft, $"page {page} as it is");
            Assert.AreEqual(1, calls.Skip(1).Take(4).Count(c => c.BlockTopLeft), $"page {page}: one orientation stands it upright");
            Assert.IsTrue(calls[5].BlockTopLeft && calls[5].Width == ScanWidth && calls[5].Height == ScanHeight, $"page {page} turned back");
        }

        // The block where it is on each page, in page space, and the word running the way the text
        // runs on the page: across, down, back or up.
        (double X, double Y)[] centers = [(20, 280), (280, 180), (180, 20), (20, 20), (180, 20)];
        (double X, double Y)[] directions = [(1, 0), (0, -1), (-1, 0), (0, 1), (-1, 0)];
        for (int i = 0; i < centers.Length; i++)
        {
            PdfPoint[] quad = report.Pages[i].Lines[0].Words[0].Quad;
            Assert.AreEqual(centers[i].X, Center(quad).X, 0.5, $"page {i}");
            Assert.AreEqual(centers[i].Y, Center(quad).Y, 0.5, $"page {i}");
            double dx = quad[1].X - quad[0].X, dy = quad[1].Y - quad[0].Y, length = Math.Sqrt((dx * dx) + (dy * dy));
            Assert.AreEqual(directions[i].X, dx / length, 1e-9, $"page {i}");
            Assert.AreEqual(directions[i].Y, dy / length, 1e-9, $"page {i}");
        }

        StringAssert.Contains(Content(PdfDocument.Open(doc.ToArray(Unoptimized)), 1), "<0062006C006F0063006B>Tj");

        // Turned off, each page is read once as it is, and the line keeps the direction it was read in.
        var plain = new BlockEngine();
        PdfOcrReport asIs = new PdfOcr(plain, new PdfOcrOptions { DetectOrientation = false }).Recognize(Scanned(d => AddTurnedPage(d, 1)));
        Assert.HasCount(1, plain.Calls);
        Assert.AreEqual(0, asIs.Pages[0].Orientation);
        PdfPoint[] read = asIs.Pages[0].Lines[0].Quad;
        Assert.AreEqual(280, Center(read).X, 0.5);
        Assert.IsLessThan(read[0].Y, read[1].Y, "down the page, as the engine read it");
    }

    [TestMethod]
    public void TriesOrientationsOnAReducedCopy()
    {
        // 1200 by 1800 pixels: the orientations are tried at 1600 on the long side, the page read at full size.
        var engine = new BlockEngine();
        PdfOcrReport report = new PdfOcr(engine).Recognize(Scanned(d => AddTurnedPage(d, 3, 1200, 1800)));
        Assert.AreEqual(270, report.Pages[0].Orientation);
        Assert.HasCount(6, engine.Calls);
        Assert.IsTrue(engine.Calls.Skip(1).Take(4).All(c => Math.Max(c.Width, c.Height) == 1600), string.Join(", ", engine.Calls));
        Assert.AreEqual((1200, 1800, true), (engine.Calls[5].Width, engine.Calls[5].Height, engine.Calls[5].BlockTopLeft));
        PdfPoint center = Center(report.Pages[0].Lines[0].Quad);
        Assert.AreEqual(20, center.X, 0.5);
        Assert.AreEqual(20, center.Y, 0.5);

        // With no line read at all, nothing more is tried.
        var blank = new BlockEngine();
        PdfDocument empty = Scanned(d =>
        {
            using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics();
            g.DrawImage(MarkedPiece(0, 0, 10, 10), 0, 0, ScanWidth, ScanHeight);
        });
        Assert.AreEqual(PdfOcrPageStatus.Done, new PdfOcr(blank).Recognize(empty).Pages[0].Status);
        Assert.HasCount(1, blank.Calls);
    }

    /// <summary>Renders through a delegate and records each call.</summary>
    private sealed class Rasterizer(Func<PdfPage, int, double, OcrBitmap?> render) : IPdfPageRasterizer
    {
        public List<(int Index, double Dpi)> Calls { get; } = [];

        public OcrBitmap? Render(PdfPage page, int pageIndex, double dpi, CancellationToken cancellationToken)
        {
            Calls.Add((pageIndex, dpi));
            return render(page, pageIndex, dpi);
        }
    }

    /// <summary>A page as a renderer shows it, turned by /Rotate: white, with the block 10 to 30 points in from the top left.</summary>
    private static OcrBitmap MarkAsShown(PdfPage page, int pageIndex, double dpi)
    {
        bool sideways = page.Rotation % 180 != 0;
        double scale = dpi / 72;
        int width = (int)Math.Round((sideways ? page.CropBox.Height : page.CropBox.Width) * scale);
        int height = (int)Math.Round((sideways ? page.CropBox.Width : page.CropBox.Height) * scale);
        var pixels = new byte[width * height];
        pixels.AsSpan().Fill(255);
        int from = (int)Math.Round(10 * scale), to = (int)Math.Round(30 * scale);
        for (int y = from; y < to; y++) pixels.AsSpan((y * width) + from, to - from).Clear();
        return new OcrBitmap(width, height, pixels);
    }

    /// <summary>A page with a full page image in an encoding that is not decoded.</summary>
    private static void AddUndecodedPage(PdfDocument d)
    {
        PdfPage page = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight));
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
        page.Dictionary.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["J"] = d.AddObject(new PdfStream(dict, new byte[] { 1, 2, 3 })) } });
        page.Dictionary.Set("Contents", d.AddObject(PdfStream.FromData("q 200 0 0 300 0 0 cm /J Do Q"u8)));
    }

    /// <summary>A page with a black square drawn as a path, as text turned into outlines is.</summary>
    private static PdfPage AddVectorPage(PdfDocument d, double width = ScanWidth, double height = ScanHeight)
    {
        PdfPage page = d.Pages.Add(PdfRectangle.FromSize(width, height));
        page.Dictionary.Set("Contents", d.AddObject(PdfStream.FromData("0 g 10 10 20 20 re f"u8)));
        return page;
    }

    [TestMethod]
    public void RendersPagesWithoutAScanThroughTheRasterizer()
    {
        PdfImage scan = MarkedScan();
        PdfDocument doc = Scanned(d =>
        {
            // Vector art only, upright and shown turned a quarter.
            AddVectorPage(d);
            AddVectorPage(d, ScanHeight, ScanWidth).Rotate(90);

            // Text, and a scan the add-on reads itself: the rasterizer is not asked.
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics()) g.DrawString("Born digital", PdfStandardFont.Helvetica, 12, PdfColor.Black, 50, 50);
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics()) g.DrawImage(scan, 0, 0, ScanWidth, ScanHeight);

            AddUndecodedPage(d);
        });

        var rasterizer = new Rasterizer(MarkAsShown);
        var engine = new BlockEngine();
        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Rasterizer = rasterizer, RasterizerDpi = 144, MaxDegreeOfParallelism = 1 }).MakeSearchable(doc);
        CollectionAssert.AreEqual(
            new[] { PdfOcrPageStatus.Done, PdfOcrPageStatus.Done, PdfOcrPageStatus.SkippedHasText, PdfOcrPageStatus.Done, PdfOcrPageStatus.Done },
            report.Pages.Select(p => p.Status).ToArray(),
            string.Join(", ", report.Pages.Select(p => p.Reason)));
        CollectionAssert.AreEqual(new[] { (0, 144.0), (1, 144.0), (4, 144.0) }, rasterizer.Calls);
        Assert.IsTrue(engine.Calls.All(c => c.BlockTopLeft), string.Join(", ", engine.Calls));
        Assert.AreEqual((400, 600), (engine.Calls[0].Width, engine.Calls[0].Height), "rendered at 144 dpi");

        // The block's center in page space. On the page shown turned a quarter clockwise, the top
        // left as shown is page space's bottom left.
        (int Page, double X, double Y)[] expected = [(0, 20, 280), (1, 20, 20), (3, 20, 280), (4, 20, 280)];
        foreach ((int i, double x, double y) in expected)
        {
            PdfPoint center = Center(report.Pages[i].Lines[0].Words[0].Quad);
            Assert.AreEqual(x, center.X, 0.5, $"page {i}");
            Assert.AreEqual(y, center.Y, 0.5, $"page {i}");
        }

        StringAssert.Contains(Content(PdfDocument.Open(doc.ToArray(Unoptimized)), 0), "3 Tr");

        // Never finer than MaxOcrDpi, and lowered until the page fits the pixel limit before it is rendered.
        PdfDocument vector = Scanned(d => AddVectorPage(d));
        var fine = new Rasterizer(MarkAsShown);
        _ = new PdfOcr(new BlockEngine(), new PdfOcrOptions { Rasterizer = fine, RasterizerDpi = 1200 }).Recognize(vector);
        Assert.AreEqual(400, fine.Calls[0].Dpi);
        var limited = new Rasterizer(MarkAsShown);
        PdfOcrReport small = new PdfOcr(new BlockEngine(), new PdfOcrOptions { Rasterizer = limited, MaxImagePixels = 30_000 }).Recognize(vector);
        Assert.AreEqual(PdfOcrPageStatus.Done, small.Pages[0].Status, small.Pages[0].Reason);
        Assert.AreEqual(72 * Math.Sqrt(30_000.0 / (ScanWidth * ScanHeight)), limited.Calls[0].Dpi, 1e-9);
    }

    [TestMethod]
    public void ReportsWhatTheRasterizerCannotRender()
    {
        PdfDocument doc = Scanned(d =>
        {
            for (int i = 0; i < 4; i++) AddVectorPage(d);
        });

        var rasterizer = new Rasterizer((page, index, dpi) => index switch
        {
            0 => null,
            1 => throw new InvalidOperationException("no renderer for this page"),
            2 => new OcrBitmap(ScanHeight, ScanWidth, new byte[ScanWidth * ScanHeight]),
            _ => new OcrBitmap(2000, 3000, new byte[2000 * 3000]),
        });
        PdfOcrReport report = new PdfOcr(new BlockEngine(), new PdfOcrOptions { Rasterizer = rasterizer, RasterizerDpi = 72, MaxImagePixels = 1_000_000 }).Recognize(doc);
        CollectionAssert.AreEqual(
            new[] { PdfOcrPageStatus.SkippedNoScan, PdfOcrPageStatus.Failed, PdfOcrPageStatus.SkippedUnsupported, PdfOcrPageStatus.SkippedUnsupported },
            report.Pages.Select(p => p.Status).ToArray());
        StringAssert.Contains(report.Pages[1].Reason, "no renderer for this page");
        StringAssert.Contains(report.Pages[2].Reason, "not the shape of the page");
        StringAssert.Contains(report.Pages[3].Reason, "over the limit");

        // Cancellation is not a failure of the page: it ends the run.
        using var cancel = new CancellationTokenSource();
        var cancelling = new Rasterizer((_, _, _) =>
        {
            cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
            return null;
        });
        Assert.ThrowsExactly<OperationCanceledException>(() => new PdfOcr(new BlockEngine(), new PdfOcrOptions { Rasterizer = cancelling }).Recognize(doc, cancel.Token));
    }

    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", "Ocr", .. parts]);

    // The fixture page and its pieces are 200 dpi scans: 0.36 points a pixel.
    private const double FixturePoint = 72.0 / 200;

    /// <summary>
    /// Adds a page with the fixture scan: whole, as strips stacked top to bottom (drawn bottom
    /// first, as some writers do), or as tiles in rows and columns.
    /// </summary>
    private static void AddFixturePage(PdfDocument d, string? pieces)
    {
        PdfImage whole = PdfImage.Load(Fixture("page.png"));
        double width = whole.PixelWidth * FixturePoint, height = whole.PixelHeight * FixturePoint;
        using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(width, height)).CreateGraphics();
        if (pieces is null)
        {
            g.DrawImage(whole, 0, 0, width, height);
            return;
        }

        var placed = new List<(PdfImage Image, double X, double Y)>();
        if (pieces == "strips")
        {
            double top = 0;
            foreach (string file in Directory.GetFiles(Fixture("strips"), "*.png").OrderBy(f => f, StringComparer.Ordinal))
            {
                PdfImage strip = PdfImage.Load(file);
                placed.Add((strip, 0, top));
                top += strip.PixelHeight * FixturePoint;
            }

            placed.Reverse();
        }
        else
        {
            double top = 0;
            for (int row = 0; File.Exists(Fixture("tiles", $"tile-{row}-0.png")); row++)
            {
                double left = 0, rowHeight = 0;
                for (int column = 0; File.Exists(Fixture("tiles", $"tile-{row}-{column}.png")); column++)
                {
                    PdfImage tile = PdfImage.Load(Fixture("tiles", $"tile-{row}-{column}.png"));
                    placed.Add((tile, left, top));
                    left += tile.PixelWidth * FixturePoint;
                    rowHeight = tile.PixelHeight * FixturePoint;
                }

                top += rowHeight;
            }
        }

        Assert.IsGreaterThan(1, placed.Count);
        foreach ((PdfImage image, double x, double y) in placed) g.DrawImage(image, x, y, image.PixelWidth * FixturePoint, image.PixelHeight * FixturePoint);
    }

    [TestMethod]
    public void PutsAScanStoredInPiecesBackTogether()
    {
        PdfDocument doc = Scanned(d =>
        {
            AddFixturePage(d, null);
            AddFixturePage(d, "strips");
            AddFixturePage(d, "tiles");
        });
        Assert.HasCount(5, doc.Pages[1].GetImages());

        var engine = new BlockEngine();
        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { MaxDegreeOfParallelism = 1 }).Recognize(doc);
        Assert.IsTrue(report.Pages.All(p => p.Status == PdfOcrPageStatus.Done), string.Join(", ", report.Pages.Select(p => p.Status + " " + p.Reason)));

        // The engine sees the strips and the tiles as the one image they were cut from, pixel for pixel.
        Assert.HasCount(3, engine.Images);
        OcrBitmap whole = engine.Images[0];
        Assert.AreEqual(1166, whole.Width);
        Assert.AreEqual(1654, whole.Height);
        foreach (OcrBitmap composed in engine.Images.Skip(1))
        {
            Assert.AreEqual(whole.Width, composed.Width);
            Assert.AreEqual(whole.Height, composed.Height);
            Assert.IsTrue(whole.Pixels.AsSpan().SequenceEqual(composed.Pixels), "the pieces put together are the page");
        }

        // And what it finds maps back to the same place on every page.
        PdfPoint expected = Center(report.Pages[0].Lines[0].Quad);
        foreach (PdfOcrPage page in report.Pages.Skip(1))
        {
            PdfPoint center = Center(page.Lines[0].Quad);
            Assert.AreEqual(expected.X, center.X, 1e-6);
            Assert.AreEqual(expected.Y, center.Y, 1e-6);
        }
    }

    [TestMethod]
    public void MapsWordsFoundInPiecesBackToThePage()
    {
        PdfDocument doc = Scanned(d =>
        {
            // Three strips, the first cut through the block, away from the corner of a larger page.
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(300, 400)).CreateGraphics())
            {
                foreach ((int top, int rows) in new[] { (0, 25), (25, 75), (100, 200) }) g.DrawImage(MarkedPiece(0, top, ScanWidth, rows), 50, 40 + top, ScanWidth, rows);
            }

            // Four tiles meeting inside the block.
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics())
            {
                foreach ((int x, int y, int w, int h) in new[] { (0, 0, 20, 20), (20, 0, 180, 20), (0, 20, 20, 280), (20, 20, 180, 280) }) g.DrawImage(MarkedPiece(x, y, w, h), x, y, w, h);
            }

            // The strips drawn turned on a landscape page, and the page shown turned back.
            PdfPage page = d.Pages.Add(PdfRectangle.FromSize(ScanHeight, ScanWidth));
            using (PdfGraphics g = page.CreateGraphics())
            {
                g.TranslateTransform(ScanHeight, 0);
                g.RotateTransform(90);
                foreach ((int top, int rows) in new[] { (0, 25), (25, 75), (100, 200) }) g.DrawImage(MarkedPiece(0, top, ScanWidth, rows), 0, top, ScanWidth, rows);
            }

            page.Rotate(270);
        });

        var engine = new BlockEngine();
        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { MaxDegreeOfParallelism = 1 }).MakeSearchable(doc);
        Assert.IsTrue(report.Pages.All(p => p.Status == PdfOcrPageStatus.Done), string.Join(", ", report.Pages.Select(p => p.Status + " " + p.Reason)));
        Assert.IsTrue(engine.Calls.All(c => c.BlockTopLeft && c.Width == ScanWidth && c.Height == ScanHeight), string.Join(", ", engine.Calls));

        // The block's center, in page space (y up, before /Rotate).
        (double X, double Y)[] expected = [(70, 340), (20, ScanHeight - 20), (ScanHeight - 20, ScanWidth - 20)];
        for (int i = 0; i < expected.Length; i++)
        {
            PdfPoint center = Center(report.Pages[i].Lines[0].Words[0].Quad);
            Assert.AreEqual(expected[i].X, center.X, 0.5, $"page {i}");
            Assert.AreEqual(expected[i].Y, center.Y, 0.5, $"page {i}");
        }

        StringAssert.Contains(Content(PdfDocument.Open(doc.ToArray(Unoptimized)), 0), "3 Tr");
    }

    [TestMethod]
    public void PaintsATextMaskOverItsBackground()
    {
        // A light gray background, and over it a stencil mask at twice the resolution that paints the
        // block (samples 0 paint, ISO 32000-1, 8.9.6.2), as mixed raster content scans store text.
        var background = new byte[ScanWidth * ScanHeight];
        background.AsSpan().Fill(230);
        const int MaskWidth = 2 * ScanWidth, MaskHeight = 2 * ScanHeight, Stride = MaskWidth / 8;
        var mask = new byte[Stride * MaskHeight];
        mask.AsSpan().Fill(0xFF);
        for (int y = 20; y < 60; y++)
        {
            for (int x = 20; x < 60; x++) mask[(y * Stride) + (x / 8)] &= (byte)~(0x80 >> (x % 8));
        }

        PdfDocument doc = Scanned(d =>
        {
            PdfPage page = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight));
            var image = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(ScanWidth),
                ["Height"] = PdfNumber.FromInteger(ScanHeight),
                ["ColorSpace"] = new PdfName("DeviceGray"),
                ["BitsPerComponent"] = PdfNumber.FromInteger(8),
            };
            var stencil = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(MaskWidth),
                ["Height"] = PdfNumber.FromInteger(MaskHeight),
                ["ImageMask"] = PdfBoolean.True,
            };
            page.Dictionary.Set("Resources", new PdfDictionary
            {
                ["XObject"] = new PdfDictionary
                {
                    ["B"] = d.AddObject(new PdfStream(image, background)),
                    ["M"] = d.AddObject(new PdfStream(stencil, mask)),
                },
            });
            page.Dictionary.Set("Contents", d.AddObject(PdfStream.FromData("q 200 0 0 300 0 0 cm /B Do Q q 0 g 200 0 0 300 0 0 cm /M Do Q"u8)));
        });

        var engine = new BlockEngine();
        PdfOcrReport report = new PdfOcr(engine).Recognize(doc);
        Assert.AreEqual(PdfOcrPageStatus.Done, report.Pages[0].Status, report.Pages[0].Reason);

        // At the mask's resolution; the mask darkens where it paints and leaves the background elsewhere.
        OcrBitmap seen = engine.Images[0];
        Assert.AreEqual(MaskWidth, seen.Width);
        Assert.AreEqual(MaskHeight, seen.Height);
        Assert.AreEqual(0, seen.Pixels[(40 * MaskWidth) + 40]);
        Assert.AreEqual(230, seen.Pixels[(300 * MaskWidth) + 200]);
        PdfPoint center = Center(report.Pages[0].Lines[0].Quad);
        Assert.AreEqual(20, center.X, 0.5);
        Assert.AreEqual(ScanHeight - 20, center.Y, 0.5);
    }

    [TestMethod]
    public void LeavesPiecesItCannotPutTogetherAlone()
    {
        PdfImage strip = MarkedPiece(0, 0, ScanWidth, 100);
        PdfDocument doc = Scanned(d =>
        {
            // One image drawn ten times over the page: painting would cost ten canvases.
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics())
            {
                for (int i = 0; i < 10; i++) g.DrawImage(strip, 0, 0, ScanWidth, ScanHeight);
            }

            // Two pieces that only cover enough of the page together, one of them turned 10 degrees.
            using (PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics())
            {
                g.DrawImage(strip, 0, 0, ScanWidth, 100);
                g.RotateAtTransform(10, 100, 220);
                g.DrawImage(strip, 0, 170, ScanWidth, 100);
            }

            // More images than any scan is made of.
            PdfPage many = d.Pages.Add(PdfRectangle.FromSize(100, 100));
            var dot = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(1),
                ["Height"] = PdfNumber.FromInteger(1),
                ["ColorSpace"] = new PdfName("DeviceGray"),
                ["BitsPerComponent"] = PdfNumber.FromInteger(8),
            };
            many.Dictionary.Set("Resources", new PdfDictionary { ["XObject"] = new PdfDictionary { ["I"] = d.AddObject(new PdfStream(dot, new byte[] { 0 })) } });
            var content = new StringBuilder();
            for (int i = 0; i < 4097; i++) content.Append($"q 1 0 0 1 {i % 64} {i / 64} cm /I Do Q ");
            many.Dictionary.Set("Contents", d.AddObject(PdfStream.FromData(Encoding.ASCII.GetBytes(content.ToString()))));
        });

        PdfOcrReport report = new PdfOcr(new BlockEngine()).Recognize(doc);
        Assert.IsTrue(report.Pages.All(p => p.Status == PdfOcrPageStatus.SkippedUnsupported), string.Join(", ", report.Pages.Select(p => p.Status)));
        StringAssert.Contains(report.Pages[0].Reason, "overlap");
        StringAssert.Contains(report.Pages[1].Reason, "quarter turns");
        StringAssert.Contains(report.Pages[2].Reason, "4097 images");

        // A piece whose matrix overflows to infinity is left out; the strips beside it are still read.
        PdfDocument overflow = Scanned(d =>
        {
            PdfPage page = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight));
            using (PdfGraphics g = page.CreateGraphics())
            {
                foreach ((int top, int rows) in new[] { (0, 100), (100, 200) }) g.DrawImage(MarkedPiece(0, top, ScanWidth, rows), 0, top, ScanWidth, rows);
            }

            string huge = "1" + new string('0', 200);
            var dot = new PdfDictionary
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = PdfNumber.FromInteger(1),
                ["Height"] = PdfNumber.FromInteger(1),
                ["ColorSpace"] = new PdfName("DeviceGray"),
                ["BitsPerComponent"] = PdfNumber.FromInteger(8),
            };
            var resources = (PdfDictionary)d.Resolve(page.Dictionary["Resources"])!;
            ((PdfDictionary)d.Resolve(resources["XObject"])!).Set("Huge", d.AddObject(new PdfStream(dot, new byte[] { 0 })));
            page.Dictionary.Set("Contents", new PdfArray
            {
                page.Dictionary["Contents"]!,
                d.AddObject(PdfStream.FromData(Encoding.ASCII.GetBytes($"q {huge} 0 0 1 0 0 cm {huge} 0 0 1 0 0 cm 1 0 0 300 0 0 cm /Huge Do Q"))),
            });
        });
        Assert.HasCount(3, overflow.Pages[0].GetImages());
        PdfOcrPage read = new PdfOcr(new BlockEngine()).Recognize(overflow).Pages[0];
        Assert.AreEqual(PdfOcrPageStatus.Done, read.Status, read.Reason);
        Assert.AreEqual(20, Center(read.Lines[0].Quad).X, 0.5);

        // Pieces whose canvas, gaps included, is over the pixel limit, though their own pixels are not.
        PdfDocument apart = Scanned(d =>
        {
            using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(ScanWidth, ScanHeight)).CreateGraphics();
            g.DrawImage(MarkedPiece(0, 0, 10, 10), 0, 0, 50, 50);
            g.DrawImage(MarkedPiece(0, 0, 10, 10), 150, 250, 50, 50);
        });
        PdfOcrReport limited = new PdfOcr(new BlockEngine(), new PdfOcrOptions { MinimumCoverage = 0.05, MaxImagePixels = 1000 }).Recognize(apart);
        Assert.AreEqual(PdfOcrPageStatus.SkippedUnsupported, limited.Pages[0].Status);
        StringAssert.Contains(limited.Pages[0].Reason, "Put together");
        PdfOcrReport allowed = new PdfOcr(new BlockEngine(), new PdfOcrOptions { MinimumCoverage = 0.05 }).Recognize(apart);
        Assert.AreEqual(PdfOcrPageStatus.Done, allowed.Pages[0].Status, allowed.Pages[0].Reason);
    }

    [TestMethod]
    public void MeasuresWhatPiecesCover()
    {
        // Overlaps count once; a piece inside another adds nothing; empty rectangles add nothing.
        PdfRectangle[] pieces = [new(0, 0, 10, 10), new(5, 5, 15, 15), new(1, 1, 2, 2), new(20, 20, 20, 30)];
        Assert.AreEqual(175, Mosaic.UnionArea(pieces), 1e-9);
        Assert.AreEqual(0, Mosaic.UnionArea([]));

        // Tiles edge to edge, each listed twice, and strips across them all.
        List<PdfRectangle> grid = [.. Enumerable.Range(0, 2500).Select(i => new PdfRectangle(i % 50, i / 50, (i % 50) + 1, (i / 50) + 1))];
        grid.AddRange([.. grid]);
        grid.AddRange(Enumerable.Range(0, 10).Select(i => new PdfRectangle(-10, 60 + i, 60, 61 + i)));
        Assert.AreEqual(2500 + 700, Mosaic.UnionArea(grid), 1e-6);

        Assert.IsTrue(Mosaic.IsAxisAligned(new PdfMatrix(200, 0, 0, 300, 10, 20)));
        Assert.IsTrue(Mosaic.IsAxisAligned(new PdfMatrix(0, 200, -300, 0, 0, 0)), "a quarter turn");
        Assert.IsTrue(Mosaic.IsAxisAligned(new PdfMatrix(-200, 0, 0, 300, 0, 0)), "mirrored");
        Assert.IsFalse(Mosaic.IsAxisAligned(PdfMatrix.Multiply(PdfMatrix.CreateScale(200, 300), PdfMatrix.CreateRotation(30))));
        Assert.IsFalse(Mosaic.IsAxisAligned(new PdfMatrix(200, 0, 400, 0, 0, 0)), "flat");
    }

    /// <summary>
    /// The models: the folder <c>PDFNETSTACK_OCR_MODELS</c> names, else the ones the models package's
    /// build file copied next to the tests (when they were fetched into its models folder). The tests
    /// that need them are inconclusive without.
    /// </summary>
    private static string Models()
    {
        string? folder = Environment.GetEnvironmentVariable("PDFNETSTACK_OCR_MODELS");
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder!;
        string packaged = Path.Combine(AppContext.BaseDirectory, OnnxOcrEngine.PackagedModelFolder);
        if (File.Exists(Path.Combine(packaged, OnnxOcrEngine.DetectionModel))) return packaged;
        Assert.Inconclusive("Set PDFNETSTACK_OCR_MODELS to a folder with the PP-OCRv5 models, or fetch them into src/PdfNetStack.Ocr.Models/models (tools/fetch_ocr_models.py).");
        return "";
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

    [TestMethod]
    public void ReadsPagesScannedSidewaysWithTheModels()
    {
        string models = Models();
        string[] languages = ["latin", "arabic"];
        using OnnxOcrEngine engine = OnnxOcrEngine.Load(models, new OnnxOcrEngineOptions { Languages = languages });
        OcrBitmap upright = OcrBitmap.FromPdfBitmap(Scanned(d => AddFixturePage(d, null)).Pages[0].GetImages()[0].Decode()!);

        // The page's pixels turned a quarter, a half and three quarters clockwise, each drawn upright
        // on a page of its shape, with nothing in the PDF to say so.
        PdfDocument doc = Scanned(d =>
        {
            for (int turns = 1; turns < 4; turns++)
            {
                (byte[] pixels, int w, int h) = ImageOps.Rotate(upright.Pixels, upright.Width, upright.Height, turns);
                double width = w * FixturePoint, height = h * FixturePoint;
                using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(width, height)).CreateGraphics();
                g.DrawImage(PdfImage.Load(Imaging.PngWriter.Write(w, h, 1, pixels)), 0, 0, width, height);
            }
        });

        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = languages }).MakeSearchable(doc);
        CollectionAssert.AreEqual(new[] { 90, 180, 270 }, report.Pages.Select(p => p.Orientation).ToArray());
        PdfDocument saved = PdfDocument.Open(doc.ToArray());
        foreach (PdfOcrPage page in report.Pages)
        {
            Assert.AreEqual(PdfOcrPageStatus.Done, page.Status, page.Reason);
            string[] lines = [.. page.Lines.Select(l => l.Text)];
            foreach (string line in Expected.Append(Arabic)) CollectionAssert.Contains(lines, line, $"page {page.Index}: " + string.Join(" | ", lines));
            int[] order = [.. Expected.Append(Arabic).Select(l => Array.IndexOf(lines, l))];
            CollectionAssert.AreEqual(order.OrderBy(i => i).ToArray(), order, $"page {page.Index} in reading order: " + string.Join(" | ", lines));

            // The heading lies along the side the page's top was turned to: right, bottom, left.
            PdfPoint heading = Center(page.Lines.Single(l => l.Text == Expected[0]).Quad);
            PdfRectangle box = saved.Pages[page.Index].MediaBox;
            switch (page.Orientation)
            {
                case 90: Assert.IsGreaterThan(box.Width - 100, heading.X); break;
                case 180: Assert.IsLessThan(100, heading.Y); break;
                default: Assert.IsLessThan(100, heading.X); break;
            }

            // The invisible text runs along the turned lines, so the page searches as it reads.
            string text = saved.Pages[page.Index].ExtractText();
            foreach (string line in Expected.Append(Arabic)) StringAssert.Contains(text, line, $"page {page.Index}");
        }
    }

    [TestMethod]
    public void ReadsAScanStoredInPiecesWithTheModels()
    {
        string models = Models();
        string[] languages = ["latin", "arabic"];
        using OnnxOcrEngine engine = OnnxOcrEngine.Load(models, new OnnxOcrEngineOptions { Languages = languages });
        PdfDocument doc = Scanned(d =>
        {
            AddFixturePage(d, "strips");
            AddFixturePage(d, "tiles");
        });

        // Strip and tile edges run through the lines of text, which still read whole.
        PdfOcrReport report = new PdfOcr(engine, new PdfOcrOptions { Languages = languages }).MakeSearchable(doc);
        PdfDocument saved = PdfDocument.Open(doc.ToArray());
        foreach (PdfOcrPage page in report.Pages)
        {
            Assert.AreEqual(PdfOcrPageStatus.Done, page.Status, page.Reason);
            string[] lines = [.. page.Lines.Select(l => l.Text)];
            foreach (string line in Expected.Append(Arabic)) CollectionAssert.Contains(lines, line, string.Join(" | ", lines));
            Assert.IsGreaterThan(500, Center(page.Lines.Single(l => l.Text == Expected[0]).Quad).Y, "the heading is at the top of the page");

            string text = saved.Pages[page.Index].ExtractText();
            foreach (string line in Expected.Append(Arabic)) StringAssert.Contains(text, line);
        }
    }

    [TestMethod]
    public void LoadsTheModelsThePackagePutsNextToTheApplication()
    {
        // Without the package the error says what to add.
        string empty = Path.Combine(Path.GetTempPath(), "pdfnetstack-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var missing = Assert.ThrowsExactly<FileNotFoundException>(() => OnnxOcrEngine.LoadPackaged(empty, null));
            StringAssert.Contains(missing.Message, "PdfNetStack.Ocr.Models");
        }
        finally
        {
            Directory.Delete(empty, true);
        }

        // With it, the parameterless overload finds them under the base directory.
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, OnnxOcrEngine.PackagedModelFolder, OnnxOcrEngine.DetectionModel)))
        {
            Assert.Inconclusive("The models were not fetched into src/PdfNetStack.Ocr.Models/models, so the build did not copy them here.");
        }

        using OnnxOcrEngine engine = OnnxOcrEngine.Load(new OnnxOcrEngineOptions { Languages = ["latin", "arabic"] });
        PdfImage page = PdfImage.Load(Fixture("page.png"));
        OcrBitmap bitmap = OcrBitmap.FromPdfBitmap(Scanned(d =>
        {
            using PdfGraphics g = d.Pages.Add(PdfRectangle.FromSize(420, 595)).CreateGraphics();
            g.DrawImage(page, 0, 0, 420, 595);
        }).Pages[0].GetImages()[0].Decode()!);
        string[] lines = [.. engine.Recognize(bitmap, new OcrRequest(), default).Lines.Select(l => l.Text)];
        CollectionAssert.Contains(lines, Expected[0], string.Join(" | ", lines));
    }

    [TestMethod]
    public void PinsTheSameModelsEverywhere()
    {
        // The engine checks the hashes on load, the models package on pack, the fetch script on download.
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "PdfNetStack.sln"))) root = Path.GetDirectoryName(root) ?? throw new AssertFailedException("The repository root is not above the test output.");
        string project = File.ReadAllText(Path.Combine(root, "src", "PdfNetStack.Ocr.Models", "PdfNetStack.Ocr.Models.csproj"));
        string script = File.ReadAllText(Path.Combine(root, "tools", "fetch_ocr_models.py"));
        static List<KeyValuePair<string, string>> Pins(string text, string pattern) =>
            [.. Regex.Matches(text, pattern).Cast<Match>().Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value))];
        List<KeyValuePair<string, string>> packed = Pins(project, @"<OcrModel Include=""models/([^""]+)"" Sha256=""([0-9a-f]{64})""");
        List<KeyValuePair<string, string>> fetched = Pins(script, @"""[a-z]+/([^""]+\.onnx)"": ""([0-9a-f]{64})""");
        Assert.HasCount(4, packed);
        CollectionAssert.AreEquivalent(OnnxOcrEngine.ModelHashes.ToList(), packed);
        CollectionAssert.AreEquivalent(OnnxOcrEngine.ModelHashes.ToList(), fetched);
    }
}
