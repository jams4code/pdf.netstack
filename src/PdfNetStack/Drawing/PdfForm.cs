using PdfNetStack.Objects;

namespace PdfNetStack.Drawing;

/// <summary>
/// A reusable drawing (a form XObject, ISO 32000-1, 8.10): drawn once, placed any number of times
/// with <see cref="PdfGraphics.DrawForm(PdfForm, double, double, double, double)"/>. Use it for
/// stamps, letterheads and repeated logos, or with <see cref="FromPage"/> to place a page of any
/// document, scaled and positioned, on another page.
/// </summary>
public sealed class PdfForm
{
    private byte[] _content = [];

    private PdfForm(PdfDocument document, double width, double height, PdfDictionary dictionary)
    {
        Document = document;
        Width = width;
        Height = height;
        Reference = document.AddObject(new PdfStream(dictionary, ReadOnlyMemory<byte>.Empty));
    }

    /// <summary>The document the form belongs to.</summary>
    public PdfDocument Document { get; }

    /// <summary>Width in points.</summary>
    public double Width { get; }

    /// <summary>Height in points.</summary>
    public double Height { get; }

    internal PdfReference Reference { get; }

    internal PdfStream Stream => (PdfStream)Document.Objects[Reference.Number];

    /// <summary>An empty form to draw into with <see cref="CreateGraphics"/>.</summary>
    public static PdfForm Create(PdfDocument document, double width, double height)
    {
        Compat.ThrowIfNull(document, nameof(document));
        if (!(width > 0) || !(height > 0) || double.IsInfinity(width) || double.IsInfinity(height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A form needs a positive size.");
        }

        return new PdfForm(document, width, height, FormDictionary(new PdfRectangle(0, 0, width, height)));
    }

    /// <summary>
    /// A form showing a page's visible area (its crop box), upright as the page's rotation
    /// shows it. The page may belong to another document; its fonts and images are copied over.
    /// Annotations are not part of the page's content and are not included.
    /// </summary>
    /// <exception cref="InvalidOperationException">The page's document does not permit copying its content.</exception>
    public static PdfForm FromPage(PdfDocument document, PdfPage page)
    {
        Compat.ThrowIfNull(document, nameof(document));
        Compat.ThrowIfNull(page, nameof(page));
        PdfDocument source = page.Document;
        PdfRectangle box = page.CropBox;
        int rotation = ((page.Rotation % 360) + 360) % 360;
        bool sideways = rotation is 90 or 270;
        double w = box.Width, h = box.Height;
        double llx = box.Left, lly = box.Bottom;

        // Moves the box to the origin and turns it as a viewer shows the page.
        PdfMatrix matrix = rotation switch
        {
            90 => new PdfMatrix(0, -1, 1, 0, -lly, llx + w),
            180 => new PdfMatrix(-1, 0, 0, -1, llx + w, lly + h),
            270 => new PdfMatrix(0, 1, -1, 0, lly + h, -llx),
            _ => new PdfMatrix(1, 0, 0, 1, -llx, -lly),
        };

        PdfDictionary dict = FormDictionary(box);
        dict.Set("Matrix", PdfArray.Of(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F));
        if (source.Resolve(page.Dictionary["Resources"]) is PdfDictionary resources)
        {
            dict.Set("Resources", ReferenceEquals(source, document) ? resources : document.ImporterFor(source).ImportObject(resources));
        }

        var form = new PdfForm(document, sideways ? h : w, sideways ? w : h, dict);
        using var content = new MemoryStream();
        foreach (PdfObject part in Contents(source, page))
        {
            if (source.Resolve(part) is not PdfStream stream) continue;
            byte[] data = source.GetStreamData(stream);
            content.Write(data, 0, data.Length);
            content.WriteByte((byte)'\n');
        }

        form.AppendContent(content.ToArray());
        return form;
    }

    /// <summary>A canvas on the form, top left origin, in points. Dispose it to write the drawing.</summary>
    public PdfGraphics CreateGraphics() => new(this);

    internal void AppendContent(ReadOnlySpan<byte> content)
    {
        var joined = new byte[_content.Length + content.Length];
        _content.CopyTo(joined, 0);
        content.CopyTo(joined.AsSpan(_content.Length));
        _content = joined;
        Document.Objects[Reference.Number] = PdfStream.FromData(_content, Stream.Dictionary);
    }

    private static PdfDictionary FormDictionary(PdfRectangle bbox) => new()
    {
        ["Type"] = new PdfName("XObject"),
        ["Subtype"] = new PdfName("Form"),
        ["BBox"] = PdfArray.Of(bbox.Left, bbox.Bottom, bbox.Right, bbox.Top),
    };

    private static IEnumerable<PdfObject> Contents(PdfDocument source, PdfPage page) => page.Dictionary["Contents"] switch
    {
        null => [],
        PdfReference r when source.Resolve(r) is PdfArray a => a,
        PdfArray a => a,
        PdfObject single => [single],
    };
}
