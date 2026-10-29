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
/// using var engine = OnnxOcrEngine.Load("models");
/// PdfDocument doc = PdfDocument.Open("scan.pdf");
/// PdfOcrReport report = new PdfOcr(engine).MakeSearchable(doc);
/// doc.Save("scan-searchable.pdf");
/// </code>
/// </example>
public sealed class PdfOcr
{
    // Share of a word box's height left out above and below the invisible text.
    private const double Inset = 0.12;

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
                (scans[k], PdfOcrPage? skipped) = Prepare(document.Pages[index], index);
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

    private (Scan? Scan, PdfOcrPage? Skipped) Prepare(PdfPage page, int index)
    {
        PdfOcrPage Skip(PdfOcrPageStatus status, string? reason) => new(index, status, reason, []);
        try
        {
            if (_options.SkipPagesWithText && TextFinder.HasText(page)) return (null, Skip(PdfOcrPageStatus.SkippedHasText, null));

            // The scan: the image covering most of the crop box.
            PdfRectangle crop = page.CropBox;
            double pageArea = Math.Max(1e-9, crop.Width * crop.Height);
            PdfPageImage? image = page.GetImages()
                .Select(i => (Image: i, Covered: Overlap(i.Bounds, crop) / pageArea))
                .Where(c => c.Covered >= _options.MinimumCoverage)
                .OrderByDescending(c => c.Covered)
                .Select(c => c.Image)
                .FirstOrDefault();
            if (image is null) return (null, Skip(PdfOcrPageStatus.SkippedNoScan, null));
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
            (double x, double y) = turns switch
            {
                1 => (p.Y, sh - p.X),
                2 => (sw - p.X, sh - p.Y),
                3 => (sw - p.Y, p.X),
                _ => (p.X, p.Y),
            };

            // Image space is the unit square, its first row at the top (ISO 32000-1, 8.9.4).
            return m.Transform(new PdfPoint(x / scale / fullWidth, 1 - (y / scale / fullHeight)));
        }

        return new Scan(index, new OcrBitmap(uw, uh, upright), ToPage);
    }

    private PdfOcrPage Read(Scan scan, CancellationToken cancellationToken)
    {
        try
        {
            List<OcrLine>? best = null;
            foreach (string language in _options.Languages)
            {
                OcrImageResult result = _engine.Recognize(scan.Bitmap, new OcrRequest { Language = language }, cancellationToken);
                best = best is null ? [.. result.Lines] : Merge(best, result.Lines);
            }

            List<OcrLine> lines = [.. (best ?? []).Where(l => l.Confidence >= _options.MinimumConfidence).Select(l => ToPage(l, scan.ToPage))];
            return new PdfOcrPage(scan.Index, PdfOcrPageStatus.Done, null, lines);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new PdfOcrPage(scan.Index, PdfOcrPageStatus.Failed, e.Message, []);
        }
    }

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
