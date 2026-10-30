using PdfNetStack.Content;
using PdfNetStack.Drawing;
using PdfNetStack.Objects;
using PdfNetStack.Ocr.Processing;

namespace PdfNetStack.Ocr;

/// <summary>Settings for <see cref="PdfOcr"/>.</summary>
public sealed class PdfOcrOptions
{
    /// <summary>
    /// Scripts to read: <c>latin</c>, <c>arabic</c>. With several, each line keeps the reading
    /// its recognizer is most confident in. The engine must have each one loaded.
    /// </summary>
    public IReadOnlyList<string> Languages { get; init; } = ["latin"];

    /// <summary>Leave pages that already show text alone, so OCR never runs twice over a page.</summary>
    public bool SkipPagesWithText { get; init; } = true;

    /// <summary>Lines read with less confidence than this are left out.</summary>
    public double MinimumConfidence { get; init; } = 0.5;

    /// <summary>Pages recognized at once. Each holds its decoded scan in memory.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Scans with more pixels are skipped rather than decoded: about 11,500 by 11,500.</summary>
    public long MaxImagePixels { get; init; } = 1L << 27;

    /// <summary>Scans finer than this are averaged down to it before recognition.</summary>
    public double MaxOcrDpi { get; init; } = 400;

    /// <summary>Share of the crop box an image must cover to be read as the page's scan.</summary>
    public double MinimumCoverage { get; init; } = 0.5;

    /// <summary>
    /// Find scans whose text is turned a quarter or half turn with nothing in the PDF saying so,
    /// as when a page is fed into a scanner sideways, and read them turned back. Only pages where
    /// most of the text read does not run left to right cost more: a reduced copy is read in each
    /// of the four orientations, then the page once more in the best one.
    /// </summary>
    public bool DetectOrientation { get; init; } = true;

    /// <summary>
    /// Renders pages that have no scan to read: text drawn as vector outlines, vector art with
    /// images too small to be a scan, or scans in encodings or placements that are not decoded.
    /// Null (the default) leaves such pages unread. PdfNetStack has no renderer; see
    /// <see cref="IPdfPageRasterizer"/>.
    /// </summary>
    public IPdfPageRasterizer? Rasterizer { get; init; }

    /// <summary>The resolution pages are rendered at by <see cref="Rasterizer"/>, at most <see cref="MaxOcrDpi"/>.</summary>
    public double RasterizerDpi { get; init; } = 300;
}

/// <summary>
/// Renders a page to pixels for OCR, for pages <see cref="PdfOcr"/> cannot read from their images.
/// PdfNetStack does not render; an application supplies a renderer, for example one built on
/// PDFium, through <see cref="PdfOcrOptions.Rasterizer"/>.
/// </summary>
public interface IPdfPageRasterizer
{
    /// <summary>
    /// Renders a page as it is shown: its crop box, turned clockwise by its /Rotate, on white.
    /// Called one page at a time, on the thread running <see cref="PdfOcr"/>, before the page's
    /// text layer is added, so a renderer may also read the original file at
    /// <paramref name="pageIndex"/>.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="pageIndex">Its zero based index in the document.</param>
    /// <param name="dpi">The resolution to render at, in pixels per inch; already lowered to keep the page within <see cref="PdfOcrOptions.MaxImagePixels"/>.</param>
    /// <param name="cancellationToken">Stops the rendering.</param>
    /// <returns>The page in 8 bit gray, or null to leave it unread.</returns>
    OcrBitmap? Render(PdfPage page, int pageIndex, double dpi, CancellationToken cancellationToken);
}

/// <summary>What happened to a page.</summary>
public enum PdfOcrPageStatus
{
    /// <summary>The scan was read; any text found is in <see cref="PdfOcrPage.Lines"/>.</summary>
    Done,

    /// <summary>The page already shows text.</summary>
    SkippedHasText,

    /// <summary>No image covers enough of the page to be its scan.</summary>
    SkippedNoScan,

    /// <summary>The scan's encoding, size or placement is not supported; see <see cref="PdfOcrPage.Reason"/>.</summary>
    SkippedUnsupported,

    /// <summary>Reading failed; see <see cref="PdfOcrPage.Reason"/>.</summary>
    Failed,
}

/// <summary>The result for one page. Coordinates are in PDF user space: points, y up, before /Rotate.</summary>
/// <param name="Index">Zero based page index.</param>
/// <param name="Status">What happened.</param>
/// <param name="Reason">Why a page was skipped or failed, or null.</param>
/// <param name="Lines">The text found, in reading order.</param>
public sealed record PdfOcrPage(int Index, PdfOcrPageStatus Status, string? Reason, IReadOnlyList<OcrLine> Lines)
{
    /// <summary>The page's text, lines joined with newlines.</summary>
    public string Text => string.Join("\n", Lines.Select(l => l.Text));

    /// <summary>
    /// How far the text on the scan was found turned clockwise, in degrees, beyond what the page's
    /// /Rotate and the scan's placement say: 0, 90, 180 or 270. The page was read turned back.
    /// See <see cref="PdfOcrOptions.DetectOrientation"/>.
    /// </summary>
    public int Orientation { get; init; }
}

/// <summary>The result for a document.</summary>
/// <param name="Pages">One entry per page, in order.</param>
public sealed record PdfOcrReport(IReadOnlyList<PdfOcrPage> Pages)
{
    /// <summary>All the text found, pages separated by blank lines.</summary>
    public string Text => string.Join("\n\n", Pages.Where(p => p.Lines.Count > 0).Select(p => p.Text));
}

/// <summary>
/// Makes scanned PDFs searchable: finds the image that is each page's scan, reads it with an
/// <see cref="IOcrEngine"/>, and lays the words over it as invisible text, so the page looks the
/// same but selects, copies and searches.
/// </summary>
/// <example>
/// <code>
/// using var engine = OnnxOcrEngine.Load();
/// PdfDocument doc = PdfDocument.Open("scan.pdf");
/// PdfOcrReport report = new PdfOcr(engine).MakeSearchable(doc);
/// doc.Save("scan-searchable.pdf");
/// </code>
/// </example>
public sealed class PdfOcr
{
    // Share of a word box's height left out above and below the invisible text.
    private const double Inset = 0.12;

    // Images on one page beyond this are not looked at: a scan has a few, or a few hundred strips.
    private const int MaxPieces = 4096;

    private readonly IOcrEngine _engine;
    private readonly PdfOcrOptions _options;

    /// <summary>Uses <paramref name="engine"/>; the caller keeps ownership and disposes it.</summary>
    /// <param name="engine">The recognizer.</param>
    /// <param name="options">Languages, limits and skipping; defaults when null.</param>
    public PdfOcr(IOcrEngine engine, PdfOcrOptions? options = null)
    {
        Compat.ThrowIfNull(engine, nameof(engine));
        _engine = engine;
        _options = options ?? new PdfOcrOptions();
    }

    /// <summary>Reads every scanned page and adds invisible text to it.</summary>
    /// <param name="document">The document, changed in place; save it afterwards.</param>
    /// <param name="cancellationToken">Stops between pages and lines.</param>
    public PdfOcrReport MakeSearchable(PdfDocument document, CancellationToken cancellationToken = default) => Process(document, true, cancellationToken);

    /// <summary>Reads every scanned page without changing the document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="cancellationToken">Stops between pages and lines.</param>
    public PdfOcrReport Recognize(PdfDocument document, CancellationToken cancellationToken = default) => Process(document, false, cancellationToken);

    private PdfOcrReport Process(PdfDocument document, bool write, CancellationToken cancellationToken)
    {
        Compat.ThrowIfNull(document, nameof(document));
        var results = new PdfOcrPage[document.Pages.Count];
        int chunk = Math.Max(1, _options.MaxDegreeOfParallelism);

        // The document is not thread safe: scans are found and decoded, and text written, one page
        // at a time. Only recognition, which never touches the document, runs in parallel.
        for (int start = 0; start < document.Pages.Count; start += chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(chunk, document.Pages.Count - start);
            var scans = new Scan?[count];
            for (int k = 0; k < count; k++)
            {
                int index = start + k;
                (scans[k], PdfOcrPage? skipped) = Prepare(document.Pages[index], index, cancellationToken);
                if (skipped is not null) results[index] = skipped;
            }

            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = chunk, CancellationToken = cancellationToken }, k =>
            {
                if (scans[k] is Scan scan) results[start + k] = Read(scan, cancellationToken);
            });

            for (int k = 0; k < count; k++)
            {
                if (write && scans[k] is not null && results[start + k] is { Status: PdfOcrPageStatus.Done } page) Write(document.Pages[start + k], page);
            }
        }

        return new PdfOcrReport(results);
    }

    /// <summary>A page's scan, upright and at OCR resolution, with the way back to page space.</summary>
    private sealed record Scan(int Index, OcrBitmap Bitmap, Func<PdfPoint, PdfPoint> ToPage);

    private (Scan? Scan, PdfOcrPage? Skipped) Prepare(PdfPage page, int index, CancellationToken cancellationToken)
    {
        (Scan? scan, PdfOcrPage? skipped) = FindScan(page, index, cancellationToken);
        return _options.Rasterizer is not null && skipped is { Status: PdfOcrPageStatus.SkippedNoScan or PdfOcrPageStatus.SkippedUnsupported }
            ? Rasterize(page, index, skipped, cancellationToken)
            : (scan, skipped);
    }

    private (Scan? Scan, PdfOcrPage? Skipped) FindScan(PdfPage page, int index, CancellationToken cancellationToken)
    {
        PdfOcrPage Skip(PdfOcrPageStatus status, string? reason) => new(index, status, reason, []);
        try
        {
            if (_options.SkipPagesWithText && TextFinder.HasText(page)) return (null, Skip(PdfOcrPageStatus.SkippedHasText, null));

            PdfRectangle crop = page.CropBox;
            double pageArea = Math.Max(1e-9, crop.Width * crop.Height);
            List<PdfPageImage> images = [.. page.GetImages().Where(i => Overlap(i.Bounds, crop) > 0)];
            if (images.Count > MaxPieces) return (null, Skip(PdfOcrPageStatus.SkippedUnsupported, $"The page draws {images.Count} images, over the limit of {MaxPieces}."));

            // A scan stored in pieces (strips, tiles, a text mask over a background) is put back together.
            List<PdfPageImage> pieces = [.. images.Where(i => i.Width > 0 && i.Height > 0 && Mosaic.IsAxisAligned(i.Transform))];
            if (pieces.Count > 1 && Covered(pieces, crop) / pageArea >= _options.MinimumCoverage) return Compose(index, pieces, crop, page.Rotation, cancellationToken);

            // Otherwise the scan is the image covering most of the crop box.
            PdfPageImage? image = images
                .Select(i => (Image: i, Covered: Overlap(i.Bounds, crop) / pageArea))
                .Where(c => c.Covered >= _options.MinimumCoverage)
                .OrderByDescending(c => c.Covered)
                .Select(c => c.Image)
                .FirstOrDefault();
            if (image is null)
            {
                return images.Count > 1 && Covered(images, crop) / pageArea >= _options.MinimumCoverage
                    ? (null, Skip(PdfOcrPageStatus.SkippedUnsupported, "The scan is made of several images, not all upright or turned by quarter turns."))
                    : (null, Skip(PdfOcrPageStatus.SkippedNoScan, null));
            }

            if ((long)image.Width * image.Height > _options.MaxImagePixels) return (null, Skip(PdfOcrPageStatus.SkippedUnsupported, $"The scan has {image.Width} by {image.Height} pixels, over the limit."));

            PdfMatrix m = image.Transform;
            if ((m.A * m.D) - (m.B * m.C) < 0) return (null, Skip(PdfOcrPageStatus.SkippedUnsupported, "The scan is drawn mirrored."));
            PdfBitmap? decoded = image.Decode();
            if (decoded is null) return (null, Skip(PdfOcrPageStatus.SkippedUnsupported, $"Images encoded with {image.Encoding} are not decoded."));
            return (Upright(index, OcrBitmap.FromPdfBitmap(decoded), image, page.Rotation), null);
        }
        catch (PdfFormatException e)
        {
            return (null, Skip(PdfOcrPageStatus.Failed, e.Message));
        }
    }

    /// <summary>
    /// A scan in pieces, painted in drawing order into one gray canvas in page space at the finest
    /// resolution among them (no finer than <see cref="PdfOcrOptions.MaxOcrDpi"/>), then turned
    /// upright by the page's /Rotate. Sizes are checked before anything is decoded, and one piece
    /// is decoded at a time: memory is the canvas plus one piece.
    /// </summary>
    private (Scan? Scan, PdfOcrPage? Skipped) Compose(int index, List<PdfPageImage> pieces, PdfRectangle crop, int pageRotation, CancellationToken cancellationToken)
    {
        (Scan?, PdfOcrPage?) Skip(string reason) => (null, new PdfOcrPage(index, PdfOcrPageStatus.SkippedUnsupported, reason, []));
        long samples = pieces.Sum(p => (long)p.Width * p.Height);
        if (samples > _options.MaxImagePixels) return Skip($"The scan's {pieces.Count} images have {samples} pixels together, over the limit.");

        PdfRectangle area = Clip(new PdfRectangle(pieces.Min(p => p.Bounds.Left), pieces.Min(p => p.Bounds.Bottom), pieces.Max(p => p.Bounds.Right), pieces.Max(p => p.Bounds.Top)), crop);
        double dpi = Math.Min(_options.MaxOcrDpi, pieces.Max(p => Math.Max(p.HorizontalResolution, p.VerticalResolution)));
        double columns = Math.Max(1, Math.Round(area.Width * dpi / 72)), rows = Math.Max(1, Math.Round(area.Height * dpi / 72));
        if (columns * rows > _options.MaxImagePixels) return Skip($"Put together, the scan would have {(long)columns} by {(long)rows} pixels, over the limit.");
        int width = (int)columns, height = (int)rows;

        // Pieces drawn over each other many times would make painting cost many canvases.
        if (pieces.Sum(p => Mosaic.Footprint(p.Bounds, area, width, height)) > 4L * width * height) return Skip("The scan's images overlap too much to be put together.");

        var canvas = new byte[(long)width * height];
        canvas.AsSpan().Fill(255);
        foreach (PdfPageImage piece in pieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfBitmap? decoded = piece.Decode();
            if (decoded is null) return Skip($"Images encoded with {piece.Encoding} are not decoded.");
            Mosaic.Paint(canvas, width, height, area, OcrBitmap.FromPdfBitmap(decoded), piece.Transform, piece.IsMask);
        }

        // The page is shown turned clockwise by /Rotate; turning the canvas the same way shows it as read.
        int turns = (((pageRotation / 90) % 4) + 4) % 4;
        (byte[] upright, int uw, int uh) = ImageOps.Rotate(canvas, width, height, turns);
        double scaleX = width / area.Width, scaleY = height / area.Height;
        PdfPoint ToPage(PdfPoint p)
        {
            (double x, double y) = Unturn(p, turns, width, height);
            return new PdfPoint(area.Left + (x / scaleX), area.Top - (y / scaleY));
        }

        return (new Scan(index, new OcrBitmap(uw, uh, upright), ToPage), null);
    }

    /// <summary>
    /// The page rendered by <see cref="PdfOcrOptions.Rasterizer"/>, for a page with no scan to read.
    /// The rendering shows the crop box turned by /Rotate, so it is already upright; a pixel maps
    /// back through that turn to the crop box. A rasterizer that declines keeps the page as skipped.
    /// </summary>
    private (Scan? Scan, PdfOcrPage? Skipped) Rasterize(PdfPage page, int index, PdfOcrPage skipped, CancellationToken cancellationToken)
    {
        (Scan?, PdfOcrPage?) Skip(PdfOcrPageStatus status, string reason) => (null, new PdfOcrPage(index, status, reason, []));
        PdfRectangle crop = page.CropBox;
        if (!(crop.Width > 0 && crop.Height > 0)) return (null, skipped);

        // Lowered, before anything is rendered, until the page fits the pixel limit.
        double dpi = Math.Min(_options.RasterizerDpi, _options.MaxOcrDpi);
        double pixels = crop.Width * crop.Height * (dpi / 72) * (dpi / 72);
        if (pixels > _options.MaxImagePixels) dpi *= Math.Sqrt(_options.MaxImagePixels / pixels);
        if (!(dpi > 0)) return (null, skipped);

        OcrBitmap? bitmap;
        try
        {
            bitmap = _options.Rasterizer!.Render(page, index, dpi, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Skip(PdfOcrPageStatus.Failed, "The rasterizer failed: " + e.Message);
        }

        if (bitmap is null) return (null, skipped);
        if ((long)bitmap.Width * bitmap.Height > _options.MaxImagePixels) return Skip(PdfOcrPageStatus.SkippedUnsupported, $"The rasterizer returned {bitmap.Width} by {bitmap.Height} pixels, over the limit.");

        // The size before the turn, which must have the crop box's shape, give or take rounding.
        int turns = (((page.Rotation / 90) % 4) + 4) % 4;
        (int width, int height) = turns % 2 == 1 ? (bitmap.Height, bitmap.Width) : (bitmap.Width, bitmap.Height);
        if (Math.Abs(width - (height * crop.Width / crop.Height)) > 1 + (0.01 * width))
        {
            return Skip(PdfOcrPageStatus.SkippedUnsupported, $"The rasterizer returned {bitmap.Width} by {bitmap.Height} pixels, not the shape of the page as shown.");
        }

        double scaleX = width / crop.Width, scaleY = height / crop.Height;
        PdfPoint ToPage(PdfPoint p)
        {
            (double x, double y) = Unturn(p, turns, width, height);
            return new PdfPoint(crop.Left + (x / scaleX), crop.Top - (y / scaleY));
        }

        return (new Scan(index, bitmap, ToPage), null);
    }

    /// <summary>The area the images cover together inside the crop box.</summary>
    private static double Covered(List<PdfPageImage> images, PdfRectangle crop) => Mosaic.UnionArea([.. images.Select(i => Clip(i.Bounds, crop))]);

    private static PdfRectangle Clip(PdfRectangle a, PdfRectangle b) =>
        new(Math.Max(a.Left, b.Left), Math.Max(a.Bottom, b.Bottom), Math.Min(a.Right, b.Right), Math.Min(a.Top, b.Top));

    /// <summary>A point of an image turned clockwise by <paramref name="turns"/> quarters, back in the image before the turn.</summary>
    private static (double X, double Y) Unturn(PdfPoint p, int turns, int width, int height) => turns switch
    {
        1 => (p.Y, height - p.X),
        2 => (width - p.X, height - p.Y),
        3 => (width - p.Y, p.X),
        _ => (p.X, p.Y),
    };

    private static double Overlap(PdfRectangle a, PdfRectangle b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom));

    /// <summary>
    /// Scales the scan down to the OCR resolution and turns it so text stands upright as the page
    /// is shown: the image's x axis on the page, less the page's /Rotate, gives the quarter turns.
    /// </summary>
    private Scan Upright(int index, OcrBitmap bitmap, PdfPageImage image, int pageRotation)
    {
        int width = bitmap.Width, height = bitmap.Height;
        byte[] pixels = bitmap.Pixels;
        double dpi = Math.Max(image.HorizontalResolution, image.VerticalResolution), scale = 1;
        if (dpi > _options.MaxOcrDpi)
        {
            scale = _options.MaxOcrDpi / dpi;
            int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
            pixels = ImageOps.Shrink(pixels, width, height, w, h);
            (width, height) = (w, h);
        }

        // The image's x axis as the page is shown, counterclockwise: the page draws the stored
        // pixels turned that far, so turning them clockwise by the opposite shows them as read.
        PdfMatrix m = image.Transform;
        double angle = (Math.Atan2(m.B, m.A) * 180 / Math.PI) - pageRotation;
        int turns = (((int)Math.Round(-angle / 90) % 4) + 4) % 4;
        (byte[] upright, int uw, int uh) = ImageOps.Rotate(pixels, width, height, turns);

        // From the upright image back to the scaled one, to the image's own pixels, to the page.
        int sw = width, sh = height;
        double fullWidth = bitmap.Width, fullHeight = bitmap.Height;
        PdfPoint ToPage(PdfPoint p)
        {
            (double x, double y) = Unturn(p, turns, sw, sh);

            // Image space is the unit square, its first row at the top (ISO 32000-1, 8.9.4).
            return m.Transform(new PdfPoint(x / scale / fullWidth, 1 - (y / scale / fullHeight)));
        }

        return new Scan(index, new OcrBitmap(uw, uh, upright), ToPage);
    }

    private PdfOcrPage Read(Scan scan, CancellationToken cancellationToken)
    {
        try
        {
            (List<OcrLine> best, string language) = ReadLines(scan.Bitmap, cancellationToken);
            Func<PdfPoint, PdfPoint> toPage = scan.ToPage;
            int turns = 0;
            if (_options.DetectOrientation && Turned(best))
            {
                turns = Orientation(scan.Bitmap, language, cancellationToken);
                if (turns != 0)
                {
                    int width = scan.Bitmap.Width, height = scan.Bitmap.Height;
                    (byte[] pixels, int w, int h) = ImageOps.Rotate(scan.Bitmap.Pixels, width, height, turns);
                    best = ReadLines(new OcrBitmap(w, h, pixels), cancellationToken).Lines;
                    toPage = p =>
                    {
                        (double x, double y) = Unturn(p, turns, width, height);
                        return scan.ToPage(new PdfPoint(x, y));
                    };
                }
            }

            List<OcrLine> lines = [.. best.Where(l => l.Confidence >= _options.MinimumConfidence).Select(l => ToPage(l, toPage))];
            return new PdfOcrPage(scan.Index, PdfOcrPageStatus.Done, null, lines) { Orientation = ((4 - turns) % 4) * 90 };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new PdfOcrPage(scan.Index, PdfOcrPageStatus.Failed, e.Message, []);
        }
    }

    /// <summary>The lines in every language, each kept in its most confident reading, and the language that read the most.</summary>
    private (List<OcrLine> Lines, string Language) ReadLines(OcrBitmap bitmap, CancellationToken cancellationToken)
    {
        List<OcrLine>? best = null;
        string bestLanguage = "latin";
        double most = -1;
        foreach (string language in _options.Languages)
        {
            OcrImageResult result = _engine.Recognize(bitmap, new OcrRequest { Language = language }, cancellationToken);
            best = best is null ? [.. result.Lines] : Merge(best, result.Lines);
            double read = result.Lines.Sum(Weight);
            if (read > most) (most, bestLanguage) = (read, language);
        }

        return (best ?? [], bestLanguage);
    }

    // Longest side of the copy read in each orientation: about 140 dpi for an A4 page.
    private const int OrientationSide = 1600;

    /// <summary>
    /// Whether most of the text, weighed by length and confidence, does not run left to right: the
    /// detector found the lines taller than wide, or the line orientation model turned them.
    /// </summary>
    private static bool Turned(List<OcrLine> lines)
    {
        double upright = 0, turned = 0;
        foreach (OcrLine line in lines)
        {
            if (Quarter(line) == 0) upright += Weight(line);
            else turned += Weight(line);
        }

        return turned > upright;
    }

    /// <summary>
    /// The quarter turns clockwise that stand the scan's text upright. A copy reduced to at most
    /// <see cref="OrientationSide"/> pixels a side is read in each of the four orientations, in the
    /// language that read the most; the one with the most text read left to right, weighed by
    /// confidence, wins. A tie keeps the scan as it is.
    /// </summary>
    private int Orientation(OcrBitmap scan, string language, CancellationToken cancellationToken)
    {
        OcrBitmap small = scan;
        int longest = Math.Max(scan.Width, scan.Height);
        if (longest > OrientationSide)
        {
            double scale = (double)OrientationSide / longest;
            int w = Math.Max(1, (int)Math.Round(scan.Width * scale)), h = Math.Max(1, (int)Math.Round(scan.Height * scale));
            small = new OcrBitmap(w, h, ImageOps.Shrink(scan.Pixels, scan.Width, scan.Height, w, h));
        }

        var request = new OcrRequest { Language = language };
        int best = 0;
        double bestScore = 0;
        for (int turns = 0; turns < 4; turns++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (byte[] pixels, int w, int h) = ImageOps.Rotate(small.Pixels, small.Width, small.Height, turns);
            double score = _engine.Recognize(new OcrBitmap(w, h, pixels), request, cancellationToken).Lines.Where(l => Quarter(l) == 0).Sum(Weight);
            if (score > bestScore) (best, bestScore) = (turns, score);
        }

        return best;
    }

    /// <summary>The way a line reads, in quarter turns clockwise from left to right (image space, y down).</summary>
    private static int Quarter(OcrLine line)
    {
        if (line.Quad is not { Length: >= 2 }) return 0;
        PdfPoint a = line.Quad[0], b = line.Quad[1];
        double angle = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
        return (((int)Math.Round(angle / 90) % 4) + 4) % 4;
    }

    private static double Weight(OcrLine line) => line.Confidence * line.Text.Length;

    /// <summary>
    /// Per line, the more confident reading. Detection does not depend on the script, so the same
    /// boxes come back for each; a line found by only one recognizer is kept as it is.
    /// </summary>
    private static List<OcrLine> Merge(List<OcrLine> current, IReadOnlyList<OcrLine> other)
    {
        var merged = new List<OcrLine>(current);
        foreach (OcrLine line in other)
        {
            int match = merged.FindIndex(l => Geometry.Distance(l.Quad[0], line.Quad[0]) < 1 && Geometry.Distance(l.Quad[2], line.Quad[2]) < 1);
            if (match < 0) merged.Add(line);
            else if (line.Confidence > merged[match].Confidence) merged[match] = line;
        }

        return merged;
    }

    private static OcrLine ToPage(OcrLine line, Func<PdfPoint, PdfPoint> map) => line with
    {
        Quad = [.. line.Quad.Select(map)],
        Words = [.. line.Words.Select(w => w with { Quad = [.. w.Quad.Select(map)] })],
    };

    /// <summary>Each word as invisible text over the scan, stretched to its box.</summary>
    private static void Write(PdfPage page, PdfOcrPage result)
    {
        PdfRectangle media = page.MediaBox;
        using PdfGraphics graphics = page.CreateGraphics();
        foreach (OcrWord word in result.Lines.SelectMany(l => l.Words))
        {
            // Detection boxes carry a margin around the glyphs, so boxes of close lines overlap and
            // text extractors would merge them. The text keeps to the middle three quarters.
            PdfPoint[] q = word.Quad;
            PdfPoint[] inner = [Geometry.Along(q, 0, Inset), Geometry.Along(q, 1, Inset), Geometry.Along(q, 1, 1 - Inset), Geometry.Along(q, 0, 1 - Inset)];

            // Page space, y up, to the canvas, y down from the media box's top left.
            PdfPoint Canvas(PdfPoint p) => new(p.X - media.Left, media.Top - p.Y);
            PdfPoint topLeft = Canvas(inner[0]), topRight = Canvas(inner[1]), bottomLeft = Canvas(inner[3]);
            double width = Geometry.Distance(topLeft, topRight), height = Geometry.Distance(topLeft, bottomLeft);
            double angle = Math.Atan2(topRight.Y - topLeft.Y, topRight.X - topLeft.X) * 180 / Math.PI;
            graphics.DrawInvisibleText(word.Text, topLeft.X, topLeft.Y, width, height, Math.Abs(angle) < 0.01 ? 0 : angle);
        }
    }

    /// <summary>Finds whether a page shows any text at all.</summary>
    private sealed class TextFinder : ContentInterpreter
    {
        private TextFinder(PdfDocument document)
            : base(document)
        {
        }

        private bool Found { get; set; }

        public static bool HasText(PdfPage page)
        {
            var finder = new TextFinder(page.Document);
            finder.Run(page);
            return finder.Found;
        }

        protected override void OnShowText(List<PdfObject> items, PdfDictionary? resources) => Found = true;
    }
}
