using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>The standard rubber stamps (ISO 32000-1, 12.5.6.12).</summary>
public enum PdfStampName
{
    /// <summary>APPROVED, in green.</summary>
    Approved,

    /// <summary>EXPERIMENTAL, in blue.</summary>
    Experimental,

    /// <summary>NOT APPROVED, in red.</summary>
    NotApproved,

    /// <summary>AS IS, in blue.</summary>
    AsIs,

    /// <summary>EXPIRED, in red.</summary>
    Expired,

    /// <summary>NOT FOR PUBLIC RELEASE, in red.</summary>
    NotForPublicRelease,

    /// <summary>CONFIDENTIAL, in red.</summary>
    Confidential,

    /// <summary>FINAL, in green.</summary>
    Final,

    /// <summary>SOLD, in blue.</summary>
    Sold,

    /// <summary>DEPARTMENTAL, in blue.</summary>
    Departmental,

    /// <summary>FOR COMMENT, in blue.</summary>
    ForComment,

    /// <summary>TOP SECRET, in red.</summary>
    TopSecret,

    /// <summary>DRAFT, in blue.</summary>
    Draft,

    /// <summary>FOR PUBLIC RELEASE, in green.</summary>
    ForPublicRelease,
}

/// <summary>The shape at an end of a line or polyline annotation (ISO 32000-1, table 176).</summary>
public enum PdfLineEnding
{
    /// <summary>No shape.</summary>
    None,

    /// <summary>A square centered on the end point.</summary>
    Square,

    /// <summary>A circle centered on the end point.</summary>
    Circle,

    /// <summary>A square turned 45 degrees, centered on the end point.</summary>
    Diamond,

    /// <summary>Two short lines meeting at the end point, pointing away from the line.</summary>
    OpenArrow,

    /// <summary>A triangle pointing away from the line, filled with the interior color.</summary>
    ClosedArrow,

    /// <summary>A short line across the end point, at right angles to the line.</summary>
    Butt,

    /// <summary>Two short lines meeting at the end point, pointing back along the line.</summary>
    ReverseOpenArrow,

    /// <summary>A triangle pointing back along the line, filled with the interior color.</summary>
    ReverseClosedArrow,

    /// <summary>A short line through the end point, 30 degrees clockwise from the perpendicular.</summary>
    Slash,
}

/// <summary>The icon a file attachment annotation shows.</summary>
public enum PdfAttachmentIcon
{
    /// <summary>A push pin.</summary>
    PushPin,

    /// <summary>A paper clip.</summary>
    Paperclip,

    /// <summary>A small bar chart.</summary>
    Graph,

    /// <summary>A luggage tag.</summary>
    Tag,
}

/// <summary>How a new annotation looks. Options an annotation kind has no use for are ignored.</summary>
public sealed class PdfAnnotationOptions
{
    /// <summary>The color of the line, border, stamp or icon; null for the kind's default.</summary>
    public PdfColor? Color { get; init; }

    /// <summary>The fill of squares, circles, polygons, stamps and closed line endings; null for none.</summary>
    public PdfColor? InteriorColor { get; init; }

    /// <summary>Lines, ink, shapes: the stroke width in points. 0 draws no stroke. Default 1.</summary>
    public double BorderWidth { get; init; } = 1;

    /// <summary>Lines, ink, shapes: alternating dash and gap lengths in points; empty for a solid stroke.</summary>
    public IReadOnlyList<double> DashPattern { get; init; } = [];

    /// <summary>Opacity of the whole annotation, from 0 (invisible) to 1 (opaque). Default 1.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>The comment a viewer shows in the annotation's pop up, or null.</summary>
    public string? Comment { get; init; }

    /// <summary>The author, or null.</summary>
    public string? Author { get; init; }

    /// <summary>The subject, or null.</summary>
    public string? Subject { get; init; }

    /// <summary>Lines and polylines: the shape at the first point.</summary>
    public PdfLineEnding StartEnding { get; init; }

    /// <summary>Lines and polylines: the shape at the last point.</summary>
    public PdfLineEnding EndEnding { get; init; }

    /// <summary>Standard stamps: the font of the text. Helvetica Bold when null; PDF/A output needs an embedded font.</summary>
    public PdfFont? Font { get; init; }
}

/// <content>
/// Markup annotations (ISO 32000-1, 12.5.6): stamps, ink, squares, circles, lines, polygons,
/// polylines and file attachments. Each carries its colors, border and opacity in the dictionary
/// and a normal appearance drawn from them.
/// </content>
public sealed partial class PdfAnnotationCollection
{
    private const int MaxPoints = 1_000_000;

    /// <summary>A standard rubber stamp: its name in capitals, framed, in the stamp's color.</summary>
    /// <param name="stamp">Which stamp.</param>
    /// <param name="x">Left edge, in points from the page's left.</param>
    /// <param name="y">Top edge, in points from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="options">Color, fill, opacity, font and comment; defaults when null.</param>
    /// <example>
    /// <code>
    /// page.Annotations.AddStamp(PdfStampName.Approved, 400, 40, 150, 45, new PdfAnnotationOptions { Opacity = 0.8 });
    /// </code>
    /// </example>
    public PdfAnnotation AddStamp(PdfStampName stamp, double x, double y, double width, double height, PdfAnnotationOptions? options = null)
    {
        options = Checked(options);
        if (stamp is < PdfStampName.Approved or > PdfStampName.ForPublicRelease) throw new ArgumentOutOfRangeException(nameof(stamp));
        PdfColor color = options.Color ?? StampColor(stamp);
        PdfDictionary annotation = Markup("Stamp", x, y, width, height, color, options);
        annotation.Set("Name", new PdfName(stamp.ToString()));
        string label = StampLabel(stamp);
        PdfFont font = options.Font ?? PdfStandardFont.HelveticaBold;
        Appearance(annotation, width, height, g =>
        {
            double frame = Math.Min(width, height) * 0.07;
            g.DrawRoundedRectangle(new PdfPen(color, frame), options.InteriorColor, frame / 2, frame / 2, width - frame, height - frame, Math.Min(width, height) * 0.15);
            double inner = Math.Min(width, height) * 0.2;
            double unit = font.MeasureString(label, 1);
            double size = Math.Min(height * 0.55, unit > 0 ? Math.Max(0.1, width - 2 * inner) / unit : height);
            double baseline = (height - (font.Ascent + font.Descent) * size) / 2 + font.Ascent * size;
            g.DrawString(label, font, size, color, (width - font.MeasureString(label, size)) / 2, baseline);
        }, options.Opacity);
        return Add(annotation);
    }

    /// <summary>A custom rubber stamp showing <paramref name="appearance"/> scaled into the box, such as a logo or a signature image.</summary>
    /// <param name="x">Left edge, in points from the page's left.</param>
    /// <param name="y">Top edge, in points from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="appearance">A form of this document.</param>
    /// <param name="name">The stamp name viewers list it under; "Custom" when null.</param>
    /// <param name="options">Opacity and comment; defaults when null.</param>
    /// <exception cref="ArgumentException">The form belongs to another document.</exception>
    public PdfAnnotation AddStamp(double x, double y, double width, double height, PdfForm appearance, string? name = null, PdfAnnotationOptions? options = null)
    {
        Compat.ThrowIfNull(appearance, nameof(appearance));
        if (!ReferenceEquals(appearance.Document, Doc)) throw new ArgumentException("The form belongs to another document. Use PdfForm.FromPage to bring a page over.", nameof(appearance));
        return AddStamp(x, y, width, height, g => g.DrawForm(appearance, 0, 0, width, height), name, options);
    }

    /// <summary>A custom rubber stamp drawn by <paramref name="draw"/> on a canvas of the stamp's size, top left origin.</summary>
    /// <param name="x">Left edge, in points from the page's left.</param>
    /// <param name="y">Top edge, in points from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="draw">Draws the stamp.</param>
    /// <param name="name">The stamp name viewers list it under; "Custom" when null.</param>
    /// <param name="options">Opacity and comment; defaults when null.</param>
    /// <example>
    /// <code>
    /// page.Annotations.AddStamp(400, 40, 120, 40, g =>
    /// {
    ///     g.DrawRectangle(new PdfPen(PdfColor.FromHex("#7C3AED"), 2), null, 1, 1, 118, 38);
    ///     g.DrawString("RECEIVED", PdfStandardFont.HelveticaBold, 18, PdfColor.FromHex("#7C3AED"), 12, 27);
    /// }, "Received");
    /// </code>
    /// </example>
    public PdfAnnotation AddStamp(double x, double y, double width, double height, Action<PdfGraphics> draw, string? name = null, PdfAnnotationOptions? options = null)
    {
        Compat.ThrowIfNull(draw, nameof(draw));
        options = Checked(options);
        if (name is { Length: 0 }) throw new ArgumentException("A stamp name must not be empty.", nameof(name));
        PdfDictionary annotation = Markup("Stamp", x, y, width, height, options.Color, options);
        annotation.Set("Name", new PdfName(name ?? "Custom"));
        Appearance(annotation, width, height, draw, options.Opacity);
        return Add(annotation);
    }

    /// <summary>Freehand ink: one or more strokes, each a list of points from the page's top left corner.</summary>
    /// <param name="strokes">The strokes, each with at least one point.</param>
    /// <param name="options">Color (red by default), width, dashes, opacity and comment; defaults when null.</param>
    /// <exception cref="ArgumentException">There is no stroke, a stroke has no point, or a point is not finite.</exception>
    public PdfAnnotation AddInk(IEnumerable<IEnumerable<PdfPoint>> strokes, PdfAnnotationOptions? options = null)
    {
        Compat.ThrowIfNull(strokes, nameof(strokes));
        options = Checked(options);
        List<PdfPoint[]> list = [.. strokes.Select(s => Points(s, 1, nameof(strokes)))];
        if (list.Count == 0) throw new ArgumentException("Ink needs at least one stroke.", nameof(strokes));
        if (list.Sum(s => (long)s.Length) > MaxPoints) throw new ArgumentException("Ink has too many points.", nameof(strokes));
        PdfColor color = options.Color ?? DefaultShapeColor;
        (double x, double y, double w, double h) = Bounds(list.SelectMany(s => s), options.BorderWidth / 2 + 1);
        PdfDictionary annotation = Markup("Ink", x, y, w, h, color, options);
        annotation.Set("BS", BorderStyle(options));
        annotation.Set("InkList", new PdfArray([.. list.Select(s => (PdfObject)PdfCoordinates(s))]));
        PdfPen? pen = Pen(color, options, round: true);
        Appearance(annotation, w, h, g =>
        {
            if (pen is null) return;
            foreach (PdfPoint[] stroke in list)
            {
                var path = new PdfPath().MoveTo(stroke[0].X - x, stroke[0].Y - y);
                if (stroke.Length == 1) path.LineTo(stroke[0].X - x, stroke[0].Y - y);
                for (int i = 1; i < stroke.Length; i++) path.LineTo(stroke[i].X - x, stroke[i].Y - y);
                g.DrawPath(pen, null, path);
            }
        }, options.Opacity);
        return Add(annotation);
    }

    /// <summary>A rectangle (a Square annotation), its border drawn inside the box.</summary>
    /// <param name="x">Left edge, in points from the page's left.</param>
    /// <param name="y">Top edge, in points from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="options">Border color (red by default), fill, width, dashes, opacity and comment; defaults when null.</param>
    public PdfAnnotation AddSquare(double x, double y, double width, double height, PdfAnnotationOptions? options = null) =>
        Shape("Square", x, y, width, height, options);

    /// <summary>An ellipse (a Circle annotation) filling the box, its border drawn inside it.</summary>
    /// <param name="x">Left edge, in points from the page's left.</param>
    /// <param name="y">Top edge, in points from the page's top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="options">Border color (red by default), fill, width, dashes, opacity and comment; defaults when null.</param>
    public PdfAnnotation AddCircle(double x, double y, double width, double height, PdfAnnotationOptions? options = null) =>
        Shape("Circle", x, y, width, height, options);

    /// <summary>A straight line between two points, in points from the page's top left corner, with optional line endings.</summary>
    /// <param name="start">The first point.</param>
    /// <param name="end">The last point.</param>
    /// <param name="options">Color (red by default), width, dashes, endings, interior color for closed endings, opacity and comment; defaults when null.</param>
    /// <example>
    /// <code>
    /// page.Annotations.AddLine(new PdfPoint(100, 300), new PdfPoint(250, 220), new PdfAnnotationOptions
    /// {
    ///     BorderWidth = 2,
    ///     EndEnding = PdfLineEnding.ClosedArrow,
    ///     InteriorColor = PdfColor.FromHex("#DC2626"),
    /// });
    /// </code>
    /// </example>
    public PdfAnnotation AddLine(PdfPoint start, PdfPoint end, PdfAnnotationOptions? options = null)
    {
        options = Checked(options);
        PdfPoint[] points = Points([start, end], 2, nameof(start));
        PdfDictionary annotation = Path("Line", points, options, closed: false);
        annotation.Set("L", PdfCoordinates(points));
        return Add(annotation);
    }

    /// <summary>A closed polygon through the vertices, in points from the page's top left corner.</summary>
    /// <param name="vertices">At least three vertices.</param>
    /// <param name="options">Border color (red by default), fill, width, dashes, opacity and comment; defaults when null.</param>
    public PdfAnnotation AddPolygon(IEnumerable<PdfPoint> vertices, PdfAnnotationOptions? options = null)
    {
        Compat.ThrowIfNull(vertices, nameof(vertices));
        options = Checked(options);
        PdfPoint[] points = Points(vertices, 3, nameof(vertices));
        PdfDictionary annotation = Path("Polygon", points, options, closed: true);
        annotation.Set("Vertices", PdfCoordinates(points));
        return Add(annotation);
    }

    /// <summary>Connected lines through the vertices, in points from the page's top left corner, with optional line endings.</summary>
    /// <param name="vertices">At least two vertices.</param>
    /// <param name="options">Color (red by default), width, dashes, endings, interior color for closed endings, opacity and comment; defaults when null.</param>
    public PdfAnnotation AddPolyline(IEnumerable<PdfPoint> vertices, PdfAnnotationOptions? options = null)
    {
        Compat.ThrowIfNull(vertices, nameof(vertices));
        options = Checked(options);
        PdfPoint[] points = Points(vertices, 2, nameof(vertices));
        PdfDictionary annotation = Path("PolyLine", points, options, closed: false);
        annotation.Set("Vertices", PdfCoordinates(points));
        return Add(annotation);
    }

    /// <summary>
    /// A file embedded in the page behind an icon at (x, y): viewers open or save it from there.
    /// Unlike <see cref="PdfDocument.Attachments"/>, the file belongs to this spot on the page.
    /// </summary>
    /// <param name="x">Left edge of the 20 point icon, from the page's left.</param>
    /// <param name="y">Top edge of the icon, from the page's top.</param>
    /// <param name="fileName">The file name it is saved under.</param>
    /// <param name="data">The file's bytes.</param>
    /// <param name="mimeType">The MIME type, such as "text/csv", or null.</param>
    /// <param name="icon">The icon.</param>
    /// <param name="options">Icon color, opacity, author and comment (also the file's description); defaults when null.</param>
    public PdfAnnotation AddFileAttachment(double x, double y, string fileName, byte[] data, string? mimeType = null, PdfAttachmentIcon icon = PdfAttachmentIcon.PushPin, PdfAnnotationOptions? options = null)
    {
        Compat.ThrowIfNull(fileName, nameof(fileName));
        Compat.ThrowIfNull(data, nameof(data));
        if (fileName.Length == 0) throw new ArgumentException("An attachment needs a file name.", nameof(fileName));
        if (icon is < PdfAttachmentIcon.PushPin or > PdfAttachmentIcon.Tag) throw new ArgumentOutOfRangeException(nameof(icon));
        options = Checked(options);
        const double size = 20;
        PdfColor color = options.Color ?? PdfColor.FromHex("#2563EB");
        PdfDictionary annotation = Markup("FileAttachment", x, y, size, size, color, options);
        annotation.Set("Name", new PdfName(icon.ToString()));
        if (options.Comment is null) annotation.Set("Contents", PdfString.FromText(fileName));
        PdfDictionary spec = PdfAttachmentCollection.FileSpecification(Doc, fileName, data, mimeType, options.Comment, null);
        annotation.Set("FS", Doc.AddObject(spec));
        Appearance(annotation, size, size, g => DrawIcon(g, icon, color), options.Opacity);
        return Add(annotation);
    }

    private static PdfColor DefaultShapeColor => PdfColor.FromHex("#DC2626");

    private PdfAnnotation Shape(string subtype, double x, double y, double width, double height, PdfAnnotationOptions? options)
    {
        options = Checked(options);
        PdfColor color = options.Color ?? DefaultShapeColor;
        PdfDictionary annotation = Markup(subtype, x, y, width, height, color, options);
        annotation.Set("BS", BorderStyle(options));
        if (options.InteriorColor is PdfColor fill) annotation.Set("IC", PdfArray.Of(fill.ToRgb().Components()));
        PdfPen? pen = Pen(color, options, round: false);
        double bw = pen is null ? 0 : Math.Min(options.BorderWidth, Math.Min(width, height) / 2);
        if (pen is not null) pen = pen with { Width = bw };
        Appearance(annotation, width, height, g =>
        {
            double w = Math.Max(0, width - bw), h = Math.Max(0, height - bw);
            if (subtype == "Square") g.DrawRectangle(pen, options.InteriorColor, bw / 2, bw / 2, w, h);
            else g.DrawEllipse(pen, options.InteriorColor, bw / 2, bw / 2, w, h);
        }, options.Opacity);
        return Add(annotation);
    }

    /// <summary>A line, polygon or polyline: rectangle around the points, border, endings and appearance.</summary>
    private PdfDictionary Path(string subtype, PdfPoint[] points, PdfAnnotationOptions options, bool closed)
    {
        PdfColor color = options.Color ?? DefaultShapeColor;
        bool endings = !closed && (options.StartEnding != PdfLineEnding.None || options.EndEnding != PdfLineEnding.None);
        double ending = EndingSize(options.BorderWidth);
        (double x, double y, double w, double h) = Bounds(points, options.BorderWidth / 2 + (endings ? ending : 0) + 1);
        PdfDictionary annotation = Markup(subtype, x, y, w, h, color, options);
        annotation.Set("BS", BorderStyle(options));
        if (options.InteriorColor is PdfColor fill) annotation.Set("IC", PdfArray.Of(fill.ToRgb().Components()));
        if (endings) annotation.Set("LE", PdfArray.Of(new PdfName(EndingName(options.StartEnding)), new PdfName(EndingName(options.EndEnding))));
        PdfPen? pen = Pen(color, options, round: !closed);
        PdfPoint[] local = [.. points.Select(p => new PdfPoint(p.X - x, p.Y - y))];
        Appearance(annotation, w, h, g =>
        {
            var path = new PdfPath().AddPolygon(local, closed);
            if (closed) g.DrawPath(pen, options.InteriorColor, path);
            else if (pen is not null) g.DrawPath(pen, null, path);
            if (!endings) return;

            // A solid pen for the endings: a dash pattern would break up the small shapes.
            PdfPen? solid = pen is null ? null : pen with { DashPattern = [], LineJoin = PdfLineJoin.Miter };
            if (Direction(local, fromEnd: false) is PdfPoint startDirection) DrawEnding(g, solid, options.InteriorColor, local[0], startDirection, options.StartEnding, ending);
            if (Direction(local, fromEnd: true) is PdfPoint endDirection) DrawEnding(g, solid, options.InteriorColor, local[^1], endDirection, options.EndEnding, ending);
        }, options.Opacity);
        return annotation;
    }

    /// <summary>The unit vector pointing out of the path at its first or last point, or null when every point coincides.</summary>
    private static PdfPoint? Direction(PdfPoint[] points, bool fromEnd)
    {
        PdfPoint tip = fromEnd ? points[^1] : points[0];
        for (int k = 1; k < points.Length; k++)
        {
            PdfPoint other = fromEnd ? points[points.Length - 1 - k] : points[k];
            double dx = tip.X - other.X, dy = tip.Y - other.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 1e-9) return new PdfPoint(dx / length, dy / length);
        }

        return null;
    }

    /// <summary>Draws a line ending at <paramref name="tip"/>; <paramref name="d"/> points away from the line.</summary>
    private static void DrawEnding(PdfGraphics g, PdfPen? pen, PdfColor? fill, PdfPoint tip, PdfPoint d, PdfLineEnding kind, double size)
    {
        var n = new PdfPoint(-d.Y, d.X);
        double half = size / 2;
        PdfPoint At(double along, double across) => new(tip.X + d.X * along + n.X * across, tip.Y + d.Y * along + n.Y * across);
        switch (kind)
        {
            case PdfLineEnding.OpenArrow when pen is not null:
                g.DrawLines(pen, [At(-size, half), tip, At(-size, -half)]);
                break;
            case PdfLineEnding.ReverseOpenArrow when pen is not null:
                g.DrawLines(pen, [At(size, half), tip, At(size, -half)]);
                break;
            case PdfLineEnding.ClosedArrow:
                g.DrawPolygon(pen, fill, [At(-size, half), tip, At(-size, -half)]);
                break;
            case PdfLineEnding.ReverseClosedArrow:
                g.DrawPolygon(pen, fill, [At(size, half), tip, At(size, -half)]);
                break;
            case PdfLineEnding.Square:
                g.DrawPolygon(pen, fill, [At(-half, -half), At(half, -half), At(half, half), At(-half, half)]);
                break;
            case PdfLineEnding.Diamond:
                g.DrawPolygon(pen, fill, [At(-half, 0), At(0, -half), At(half, 0), At(0, half)]);
                break;
            case PdfLineEnding.Circle:
                g.DrawPath(pen, fill, new PdfPath().AddCircle(tip.X, tip.Y, half));
                break;
            case PdfLineEnding.Butt when pen is not null:
                g.DrawLine(pen, At(0, -half).X, At(0, -half).Y, At(0, half).X, At(0, half).Y);
                break;
            case PdfLineEnding.Slash when pen is not null:
                // On the y down canvas, turning the perpendicular by +30 degrees is clockwise.
                double cos = Math.Cos(Math.PI / 6), sin = Math.Sin(Math.PI / 6);
                var s = new PdfPoint(n.X * cos - n.Y * sin, n.X * sin + n.Y * cos);
                g.DrawLine(pen, tip.X - s.X * half, tip.Y - s.Y * half, tip.X + s.X * half, tip.Y + s.Y * half);
                break;
        }
    }

    private static double EndingSize(double borderWidth) => Math.Max(6, 4 * borderWidth);

    private static string EndingName(PdfLineEnding ending) => ending switch
    {
        PdfLineEnding.ReverseOpenArrow => "ROpenArrow",
        PdfLineEnding.ReverseClosedArrow => "RClosedArrow",
        _ => ending.ToString(),
    };

    /// <summary>The markup entries every kind shares: color, opacity, comment, author and subject.</summary>
    private PdfDictionary Markup(string subtype, double x, double y, double width, double height, PdfColor? color, PdfAnnotationOptions options)
    {
        PdfDictionary annotation = Base(subtype, x, y, width, height, color);
        if (options.Comment is string comment) annotation.Set("Contents", PdfString.FromText(comment));
        if (options.Author is string author) annotation.Set("T", PdfString.FromText(author));
        if (options.Subject is string subject) annotation.Set("Subj", PdfString.FromText(subject));
        if (options.Opacity < 1) annotation.Set("CA", PdfNumber.FromDouble(options.Opacity));
        return annotation;
    }

    private static PdfDictionary BorderStyle(PdfAnnotationOptions options)
    {
        var bs = new PdfDictionary
        {
            ["Type"] = new PdfName("Border"),
            ["W"] = PdfNumber.FromDouble(options.BorderWidth),
            ["S"] = new PdfName(options.DashPattern.Count > 0 ? "D" : "S"),
        };
        if (options.DashPattern.Count > 0) bs.Set("D", PdfArray.Of([.. options.DashPattern]));
        return bs;
    }

    private static PdfPen? Pen(PdfColor color, PdfAnnotationOptions options, bool round) =>
        options.BorderWidth > 0
            ? new PdfPen(color, options.BorderWidth)
            {
                DashPattern = options.DashPattern,
                LineCap = round ? PdfLineCap.Round : PdfLineCap.Butt,
                LineJoin = PdfLineJoin.Round,
            }
            : null;

    /// <summary>The box around the points, grown by <paramref name="pad"/> on every side.</summary>
    private static (double X, double Y, double Width, double Height) Bounds(IEnumerable<PdfPoint> points, double pad)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (PdfPoint p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return (minX - pad, minY - pad, maxX - minX + 2 * pad, maxY - minY + 2 * pad);
    }

    /// <summary>The points as a flat array of PDF user space coordinates.</summary>
    private PdfArray PdfCoordinates(PdfPoint[] points)
    {
        PdfRectangle box = _page.MediaBox;
        var array = new PdfArray();
        foreach (PdfPoint p in points)
        {
            array.Add(PdfNumber.FromDouble(box.Left + p.X));
            array.Add(PdfNumber.FromDouble(box.Top - p.Y));
        }

        return array;
    }

    private static PdfPoint[] Points(IEnumerable<PdfPoint> points, int minimum, string parameter)
    {
        Compat.ThrowIfNull(points, parameter);
        PdfPoint[] array = [.. points.Take(MaxPoints + 1)];
        if (array.Length < minimum) throw new ArgumentException($"At least {minimum} point{(minimum == 1 ? "" : "s")} needed.", parameter);
        if (array.Length > MaxPoints) throw new ArgumentException("Too many points.", parameter);
        if (array.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y)))
        {
            throw new ArgumentException("Points must be finite.", parameter);
        }

        return array;
    }

    private static PdfAnnotationOptions Checked(PdfAnnotationOptions? options)
    {
        options ??= new PdfAnnotationOptions();
        if (!(options.BorderWidth >= 0) || double.IsInfinity(options.BorderWidth)) throw new ArgumentOutOfRangeException(nameof(options), "The border width must be finite and not negative.");
        if (!(options.Opacity >= 0 && options.Opacity <= 1)) throw new ArgumentOutOfRangeException(nameof(options), "The opacity must be between 0 and 1.");
        if (options.DashPattern is null || options.DashPattern.Any(v => !(v >= 0) || double.IsInfinity(v)) || (options.DashPattern.Count > 0 && options.DashPattern.All(v => v == 0)))
        {
            throw new ArgumentException("Dash lengths must be finite, not negative and not all zero.", nameof(options));
        }

        if (options.StartEnding is < PdfLineEnding.None or > PdfLineEnding.Slash || options.EndEnding is < PdfLineEnding.None or > PdfLineEnding.Slash)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown line ending.");
        }

        return options;
    }

    private static PdfColor StampColor(PdfStampName stamp) => stamp switch
    {
        PdfStampName.Approved or PdfStampName.Final or PdfStampName.ForPublicRelease => PdfColor.FromHex("#15803D"),
        PdfStampName.NotApproved or PdfStampName.Expired or PdfStampName.NotForPublicRelease or PdfStampName.Confidential or PdfStampName.TopSecret => PdfColor.FromHex("#B91C1C"),
        _ => PdfColor.FromHex("#1D4ED8"),
    };

    private static string StampLabel(PdfStampName stamp) => stamp switch
    {
        PdfStampName.NotApproved => "NOT APPROVED",
        PdfStampName.AsIs => "AS IS",
        PdfStampName.NotForPublicRelease => "NOT FOR PUBLIC RELEASE",
        PdfStampName.ForComment => "FOR COMMENT",
        PdfStampName.TopSecret => "TOP SECRET",
        PdfStampName.ForPublicRelease => "FOR PUBLIC RELEASE",
        _ => stamp.ToString().ToUpperInvariant(),
    };

    /// <summary>The 20 point icons of file attachments.</summary>
    private static void DrawIcon(PdfGraphics g, PdfAttachmentIcon icon, PdfColor color)
    {
        var outline = new PdfPen(PdfColor.Gray(0.25), 0.8) { LineJoin = PdfLineJoin.Round };
        switch (icon)
        {
            case PdfAttachmentIcon.Paperclip:
                var clip = new PdfPen(color, 1.6) { LineCap = PdfLineCap.Round };
                g.DrawPath(clip, null, new PdfPath().MoveTo(12.5, 6).LineTo(12.5, 14).AddArc(10, 14, 2.5, 2.5, 0, 180).LineTo(7.5, 4.5)
                    .AddArc(10.25, 4.5, 2.75, 2.75, 180, 180).LineTo(13, 15.5).AddArc(9.5, 15.5, 3.5, 3.5, 0, 180).LineTo(6, 7));
                break;
            case PdfAttachmentIcon.Graph:
                g.DrawRectangle(outline, PdfColor.White, 1.5, 1.5, 17, 17);
                g.DrawRectangle(null, color, 4, 10, 3, 6.5);
                g.DrawRectangle(null, color, 8.5, 5, 3, 11.5);
                g.DrawRectangle(null, color, 13, 8, 3, 8.5);
                break;
            case PdfAttachmentIcon.Tag:
                g.DrawPolygon(outline, color, [new PdfPoint(1.5, 10), new PdfPoint(7.5, 3), new PdfPoint(18.5, 3), new PdfPoint(18.5, 17), new PdfPoint(7.5, 17)]);
                g.DrawEllipse(outline, PdfColor.White, 5, 8.5, 3, 3);
                break;
            default:
                g.DrawRoundedRectangle(outline, color, 6, 1.5, 8, 3.5, 1);
                g.DrawPolygon(outline, color, [new PdfPoint(7.5, 5), new PdfPoint(12.5, 5), new PdfPoint(13, 10.5), new PdfPoint(7, 10.5)]);
                g.DrawRoundedRectangle(outline, color, 4.5, 10.5, 11, 2.5, 1);
                g.DrawLine(new PdfPen(PdfColor.Gray(0.25), 1.2) { LineCap = PdfLineCap.Round }, 10, 13, 10, 18.5);
                break;
        }
    }
}
