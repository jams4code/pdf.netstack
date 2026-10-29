
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack.Drawing;

/// <summary>How drawing combines with what is under it (ISO 32000-1, 11.3.5; the CSS mix-blend-mode values).</summary>
public enum PdfBlendMode
{
    /// <summary>Paint covers the backdrop.</summary>
    Normal,

    /// <summary>Darkens: like ink on paper, or a highlighter.</summary>
    Multiply,

    /// <summary>Lightens: the inverse of multiply.</summary>
    Screen,

    /// <summary>Multiply or screen, depending on the backdrop.</summary>
    Overlay,

    /// <summary>The darker of paint and backdrop.</summary>
    Darken,

    /// <summary>The lighter of paint and backdrop.</summary>
    Lighten,

    /// <summary>Brightens the backdrop toward the paint.</summary>
    ColorDodge,

    /// <summary>Darkens the backdrop toward the paint.</summary>
    ColorBurn,

    /// <summary>Multiply or screen, depending on the paint.</summary>
    HardLight,

    /// <summary>A softer hard light.</summary>
    SoftLight,

    /// <summary>The absolute difference.</summary>
    Difference,

    /// <summary>A lower contrast difference.</summary>
    Exclusion,

    /// <summary>The paint's hue with the backdrop's saturation and luminosity.</summary>
    Hue,

    /// <summary>The paint's saturation.</summary>
    Saturation,

    /// <summary>The paint's hue and saturation.</summary>
    Color,

    /// <summary>The paint's luminosity.</summary>
    Luminosity,
}

/// <summary>Where new drawing goes relative to a page's existing content.</summary>
public enum PdfGraphicsLayer
{
    /// <summary>On top of the existing content: stamps, annotations drawn as content, headers.</summary>
    Foreground,

    /// <summary>Underneath the existing content: backgrounds, letterheads, watermarks behind text.</summary>
    Background,
}

/// <summary>
/// A drawing surface on a page. Coordinates are in points with the origin at the top left corner
/// of the media box and y pointing down. Dispose it, or save the document, to write the drawing
/// into the page. Not thread safe.
/// </summary>
/// <remarks>
/// On an existing page, the page's own content is wrapped in a save and restore pair so that
/// graphics state it leaves behind cannot affect this drawing. The page's /Rotate is not applied:
/// coordinates are those of the unrotated page.
/// </remarks>
public sealed class PdfGraphics : IDisposable
{
    private readonly PdfDocument _doc;
    private readonly PdfPage? _page;
    private readonly PdfForm? _form;
    private readonly PdfDictionary _resourceOwner;
    private readonly PdfGraphicsLayer _layer;
    private readonly PdfMatrix _flip;
    private readonly ContentWriter _w = new();
    private readonly Stack<State> _saved = new();
    private readonly Dictionary<(string Key, double Value), string> _alphaStates = new();
    private readonly HashSet<string> _ownCategories = new(StringComparer.Ordinal);
    private readonly Dictionary<FontResource, string> _fontNames = new();
    private PdfDictionary? _resources;
    private State _state = new();
    private bool _disposed;

    internal PdfGraphics(PdfPage page, PdfGraphicsLayer layer)
        : this(page.Owner, page.Dictionary, page.MediaBox)
    {
        _page = page;
        _layer = layer;
    }

    internal PdfGraphics(PdfForm form)
        : this(form.Document, form.Stream.Dictionary, new PdfRectangle(0, 0, form.Width, form.Height))
    {
        _form = form;
    }

    private PdfGraphics(PdfDocument doc, PdfDictionary resourceOwner, PdfRectangle box)
    {
        _doc = doc;
        _resourceOwner = resourceOwner;
        Width = box.Width;
        Height = box.Height;

        // Flip to a top left origin with y down. Everything below is in canvas coordinates.
        _flip = new PdfMatrix(1, 0, 0, -1, box.Left, box.Top);
        _w.Op("q").Matrix(_flip).Op("cm");
        doc.RegisterGraphics(this);
    }

    /// <summary>The page being drawn on, or null when drawing into a <see cref="PdfForm"/>.</summary>
    public PdfPage? Page => _page;

    /// <summary>The form being drawn into, or null when drawing on a page.</summary>
    public PdfForm? Form => _form;

    /// <summary>Width of the drawing area in points: the media box, or the form's width.</summary>
    public double Width { get; }

    /// <summary>Height of the drawing area in points.</summary>
    public double Height { get; }

    /// <summary>How further drawing blends with what is under it. Saved and restored with the graphics state.</summary>
    public PdfBlendMode BlendMode
    {
        get => _state.BlendMode;
        set
        {
            ThrowIfDisposed();
            if (_state.BlendMode == value) return;
            if (!_alphaStates.TryGetValue(("BM", (double)value), out string? name))
            {
                var gs = new PdfDictionary { ["Type"] = new PdfName("ExtGState"), ["BM"] = new PdfName(value.ToString()) };
                name = AddResource("ExtGState", "Gs", gs);
                _alphaStates[("BM", (double)value)] = name;
            }

            _w.Name(name).Op("gs");
            _state.BlendMode = value;
        }
    }

    /// <summary>
    /// Adds a link over a rectangle in current canvas coordinates; under a transform, the link
    /// covers the transformed rectangle's bounds. Only on pages.
    /// </summary>
    public PdfAnnotation AddLink(double x, double y, double width, double height, Uri uri)
    {
        (double lx, double ly, double w, double h) = Bounds(x, y, width, height);
        return (_page ?? throw new InvalidOperationException("Links can only be added on a page.")).Annotations.AddLink(lx, ly, w, h, uri);
    }

    /// <summary>Adds a link to a page of this document over a rectangle in current canvas coordinates.</summary>
    public PdfAnnotation AddLink(double x, double y, double width, double height, PdfPage target, double? top = null)
    {
        (double lx, double ly, double w, double h) = Bounds(x, y, width, height);
        return (_page ?? throw new InvalidOperationException("Links can only be added on a page.")).Annotations.AddLink(lx, ly, w, h, target, top);
    }

    private (double X, double Y, double Width, double Height) Bounds(double x, double y, double width, double height)
    {
        ThrowIfDisposed();
        PdfPoint[] corners =
        [
            _state.Matrix.Transform(new PdfPoint(x, y)), _state.Matrix.Transform(new PdfPoint(x + width, y)),
            _state.Matrix.Transform(new PdfPoint(x, y + height)), _state.Matrix.Transform(new PdfPoint(x + width, y + height)),
        ];
        double left = corners.Min(c => c.X), top = corners.Min(c => c.Y);
        return (left, top, corners.Max(c => c.X) - left, corners.Max(c => c.Y) - top);
    }

    /// <summary>The current transform from user coordinates to canvas coordinates.</summary>
    public PdfMatrix Transform => _state.Matrix;

    /// <summary>
    /// Saves the graphics state: transform, clip, colors and pen settings. Dispose the result, or
    /// call <see cref="Restore"/>, to return to it.
    /// </summary>
    public PdfGraphicsState Save()
    {
        ThrowIfDisposed();
        _saved.Push(_state);
        _state = _state.Clone();
        _w.Op("q");
        return new PdfGraphicsState(this, _saved.Count);
    }

    /// <summary>Restores the most recently saved state.</summary>
    /// <exception cref="InvalidOperationException">Nothing is saved.</exception>
    public void Restore()
    {
        ThrowIfDisposed();
        if (_saved.Count == 0) throw new InvalidOperationException("Restore without a matching Save.");
        _state = _saved.Pop();
        _w.Op("Q");
    }

    internal void RestoreTo(int depth)
    {
        if (_disposed) return;
        while (_saved.Count >= depth) Restore();
    }

    /// <summary>Moves the origin.</summary>
    public void TranslateTransform(double dx, double dy) => MultiplyTransform(PdfMatrix.CreateTranslation(dx, dy));

    /// <summary>Scales about the origin.</summary>
    public void ScaleTransform(double sx, double sy) => MultiplyTransform(PdfMatrix.CreateScale(sx, sy));

    /// <summary>Rotates about the origin, clockwise for positive angles.</summary>
    public void RotateTransform(double degrees) => MultiplyTransform(PdfMatrix.CreateRotation(degrees));

    /// <summary>Rotates about (x, y), clockwise for positive angles.</summary>
    public void RotateAtTransform(double degrees, double x, double y)
    {
        TranslateTransform(x, y);
        RotateTransform(degrees);
        TranslateTransform(-x, -y);
    }

    /// <summary>Applies <paramref name="matrix"/> before the current transform.</summary>
    public void MultiplyTransform(PdfMatrix matrix)
    {
        ThrowIfDisposed();
        if (matrix == PdfMatrix.Identity) return;
        _state.Matrix = PdfMatrix.Multiply(matrix, _state.Matrix);
        _w.Matrix(matrix).Op("cm");
    }

    /// <summary>Restricts all further drawing to the inside of <paramref name="path"/>, until the next restore.</summary>
    public void IntersectClip(PdfPath path)
    {
        ThrowIfDisposed();
        Compat.ThrowIfNull(path, nameof(path));
        if (path.IsEmpty) return;
        path.Write(_w);
        _w.Op(path.FillRule == PdfFillRule.EvenOdd ? "W*" : "W").Op("n");
    }

    /// <summary>Restricts all further drawing to a rectangle, until the next restore.</summary>
    public void IntersectClip(double x, double y, double width, double height) =>
        IntersectClip(new PdfPath().AddRectangle(x, y, width, height));

    /// <summary>Draws a straight line.</summary>
    public void DrawLine(PdfPen pen, double x1, double y1, double x2, double y2) =>
        DrawPath(pen, null, new PdfPath().MoveTo(x1, y1).LineTo(x2, y2));

    /// <summary>Draws connected straight lines through the given points.</summary>
    public void DrawLines(PdfPen pen, IEnumerable<PdfPoint> points) =>
        DrawPath(pen, null, new PdfPath().AddPolygon(points, close: false));

    /// <summary>Draws a cubic Bezier curve.</summary>
    public void DrawBezier(PdfPen pen, PdfPoint start, PdfPoint control1, PdfPoint control2, PdfPoint end) =>
        DrawPath(pen, null, new PdfPath().MoveTo(start.X, start.Y).CurveTo(control1.X, control1.Y, control2.X, control2.Y, end.X, end.Y));

    /// <summary>Draws a rectangle, stroked with <paramref name="pen"/> and filled with <paramref name="brush"/>; either may be null.</summary>
    public void DrawRectangle(PdfPen? pen, PdfBrush? brush, double x, double y, double width, double height) =>
        DrawPath(pen, brush, new PdfPath().AddRectangle(x, y, width, height));

    /// <summary>Draws a rectangle with circular corners.</summary>
    public void DrawRoundedRectangle(PdfPen? pen, PdfBrush? brush, double x, double y, double width, double height, double radius) =>
        DrawPath(pen, brush, new PdfPath().AddRoundedRectangle(x, y, width, height, radius));

    /// <summary>Draws a rectangle with elliptical corners, sized as CSS border-radius does.</summary>
    public void DrawRoundedRectangle(PdfPen? pen, PdfBrush? brush, double x, double y, double width, double height, PdfCornerRadii radii) =>
        DrawPath(pen, brush, new PdfPath().AddRoundedRectangle(x, y, width, height, radii));

    /// <summary>Draws an ellipse inscribed in the given rectangle.</summary>
    public void DrawEllipse(PdfPen? pen, PdfBrush? brush, double x, double y, double width, double height) =>
        DrawPath(pen, brush, new PdfPath().AddEllipse(x, y, width, height));

    /// <summary>Draws a closed polygon.</summary>
    public void DrawPolygon(PdfPen? pen, PdfBrush? brush, IEnumerable<PdfPoint> points, PdfFillRule fillRule = PdfFillRule.NonZero) =>
        DrawPath(pen, brush, new PdfPath { FillRule = fillRule }.AddPolygon(points));

    /// <summary>Draws an elliptical arc of the ellipse inscribed in the given rectangle. Angles in degrees, clockwise.</summary>
    public void DrawArc(PdfPen pen, double x, double y, double width, double height, double startAngle, double sweepAngle) =>
        DrawPath(pen, null, new PdfPath().AddArc(x + width / 2, y + height / 2, width / 2, height / 2, startAngle, sweepAngle));

    /// <summary>Draws a pie slice of the ellipse inscribed in the given rectangle. Angles in degrees, clockwise.</summary>
    public void DrawPie(PdfPen? pen, PdfBrush? brush, double x, double y, double width, double height, double startAngle, double sweepAngle)
    {
        double cx = x + width / 2, cy = y + height / 2;
        DrawPath(pen, brush, new PdfPath().MoveTo(cx, cy).AddArc(cx, cy, width / 2, height / 2, startAngle, sweepAngle).Close());
    }

    /// <summary>Strokes <paramref name="path"/> with <paramref name="pen"/> and fills it with <paramref name="brush"/>; either may be null.</summary>
    public void DrawPath(PdfPen? pen, PdfBrush? brush, PdfPath path)
    {
        ThrowIfDisposed();
        Compat.ThrowIfNull(path, nameof(path));
        if (path.IsEmpty || (pen is null && brush is null)) return;
        bool evenOdd = path.FillRule == PdfFillRule.EvenOdd;

        if (brush is PdfGradientBrush gradient)
        {
            string shading = AddShading(gradient);
            using (Save())
            {
                SetAlpha("ca", gradient.Alpha, ref _state.FillAlpha);
                path.Write(_w);
                _w.Op(evenOdd ? "W*" : "W").Op("n").Name(shading).Op("sh");
            }

            brush = null;
            if (pen is null) return;
        }

        if (brush is PdfSolidBrush solid) SetFill(solid.Color);
        if (pen is not null) SetPen(pen);
        path.Write(_w);
        _w.Op((pen, brush) switch
        {
            (not null, not null) => evenOdd ? "B*" : "B",
            (null, _) => evenOdd ? "f*" : "f",
            _ => "S",
        });
    }

    /// <summary>
    /// Draws one line of text filled with <paramref name="brush"/>. (<paramref name="x"/>,
    /// <paramref name="y"/>) is the start of the baseline; add <see cref="PdfFont.Ascent"/> times
    /// the size to <paramref name="y"/> to place the top of the line instead.
    /// </summary>
    /// <exception cref="ArgumentException">The font has no glyph for a character of the text.</exception>
    public void DrawString(string text, PdfFont font, double size, PdfBrush brush, double x, double y) =>
        DrawString(text, font, size, null, brush, x, y);

    /// <summary>
    /// Draws one line of text, its glyph outlines stroked with <paramref name="pen"/> and filled
    /// with <paramref name="brush"/>; either may be null. (<paramref name="x"/>, <paramref name="y"/>)
    /// is the start of the baseline. Control and format characters, such as tabs and zero width
    /// joiners, are skipped.
    /// </summary>
    /// <exception cref="ArgumentException">The font has no glyph for a character of the text.</exception>
    public void DrawString(string text, PdfFont font, double size, PdfPen? pen, PdfBrush? brush, double x, double y)
    {
        ThrowIfDisposed();
        Compat.ThrowIfNull(text, nameof(text));
        Compat.ThrowIfNull(font, nameof(font));
        if (!(size > 0) || double.IsInfinity(size)) throw new ArgumentOutOfRangeException(nameof(size), "Font size must be positive.");
        if (pen is null && brush is null) return;
        List<ShapedGlyph> glyphs = font.Shape(text);
        if (glyphs.Count == 0) return;
        DrawGlyphs(glyphs, font, size, pen, brush, x, y, text);
    }

    /// <summary>
    /// Lays out a paragraph at <paramref name="width"/> points, wrapping and aligning its lines,
    /// and draws it with its top left corner at (<paramref name="x"/>, <paramref name="y"/>).
    /// Returns the layout, whose <see cref="PdfTextLayout.Height"/> tells how much room it took.
    /// </summary>
    public PdfTextLayout DrawText(PdfParagraph paragraph, double x, double y, double width)
    {
        PdfTextLayout layout = PdfTextLayout.Create(paragraph, width);
        DrawText(layout, x, y);
        return layout;
    }

    /// <summary>Draws text in one style, wrapped to <paramref name="width"/> points; returns the layout.</summary>
    public PdfTextLayout DrawText(string text, PdfTextStyle style, double x, double y, double width, PdfTextAlignment alignment = PdfTextAlignment.Start) =>
        DrawText(new PdfParagraph { Alignment = alignment }.Add(text, style), x, y, width);

    /// <summary>Draws a laid out paragraph with its top left corner at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void DrawText(PdfTextLayout layout, double x, double y)
    {
        Compat.ThrowIfNull(layout, nameof(layout));
        DrawText(layout, x, y, 0, layout.Lines.Count);
    }

    /// <summary>
    /// Draws <paramref name="count"/> lines of a layout from line <paramref name="first"/>, the
    /// first of them at (<paramref name="x"/>, <paramref name="y"/>): for text that continues on
    /// the next page or column.
    /// </summary>
    public void DrawText(PdfTextLayout layout, double x, double y, int first, int count)
    {
        ThrowIfDisposed();
        Compat.ThrowIfNull(layout, nameof(layout));
        if (first < 0 || count < 0 || first + count > layout.Lines.Count) throw new ArgumentOutOfRangeException(nameof(count), "The lines are outside the layout.");
        if (count == 0) return;
        double offset = layout.Lines[first].Top;
        for (int l = first; l < first + count; l++)
        {
            PdfTextLine line = layout.Lines[l];
            double baseline = y + line.Baseline - offset;
            foreach (LineRun run in line.Runs)
            {
                if (run.Glyphs.Count == 0) continue;
                double left = x + line.Left + run.X;
                PdfTextStyle style = run.Style;
                DrawGlyphs(run.Glyphs, run.Font, style.Size, style.Pen, style.Brush, left, baseline, run.Text);
                PdfBrush decoration = style.Brush ?? (PdfBrush)(style.Pen?.Color ?? PdfColor.Black);
                if (style.Underline)
                {
                    double thickness = run.Font.UnderlineThickness * style.Size;
                    DrawRectangle(null, decoration, left, baseline + run.Font.UnderlinePosition * style.Size - thickness / 2, run.Width, thickness);
                }

                if (style.Strikethrough)
                {
                    double thickness = run.Font.StrikeoutThickness * style.Size;
                    DrawRectangle(null, decoration, left, baseline - run.Font.StrikeoutPosition * style.Size - thickness / 2, run.Width, thickness);
                }
            }
        }
    }

    /// <summary>Draws shaped glyphs, in visual order, with the baseline starting at (x, y). The HTML engine draws its own lines through this.</summary>
    internal void DrawGlyphs(List<ShapedGlyph> glyphs, PdfFont font, double size, PdfPen? pen, PdfBrush? brush, double x, double y, string text)
    {
        if (pen is null && brush is null) return;
        FontResource resource = _doc.GetFontResource(font);
        if (!_fontNames.TryGetValue(resource, out string? name))
        {
            name = AddResource("Font", "F", resource.Reference);
            _fontNames[resource] = name;
        }

        // Right to left text, ligatures and positioned marks do not copy back in order from the
        // glyphs alone; ActualText carries the original text for copying and search.
        bool actualText = NeedsActualText(text, glyphs);
        if (actualText) BeginActualText(text);
        try
        {
            if (brush is PdfGradientBrush gradient)
            {
                // Render mode 7 adds the glyph outlines to the clip; the shading then paints through it.
                string shading = AddShading(gradient);
                using (Save())
                {
                    SetAlpha("ca", gradient.Alpha, ref _state.FillAlpha);
                    WriteText(resource, name, size, glyphs, x, y, 7);
                    _w.Name(shading).Op("sh");
                }

                if (pen is null) return;
                brush = null;
            }

            if (brush is PdfSolidBrush solid) SetFill(solid.Color);
            if (pen is not null) SetPen(pen);
            WriteText(resource, name, size, glyphs, x, y, (pen, brush) switch
            {
                (not null, not null) => 2,
                (null, _) => 0,
                _ => 1,
            });
        }
        finally
        {
            if (actualText) _w.Op("EMC");
        }
    }

    private static bool NeedsActualText(string text, List<ShapedGlyph> glyphs)
    {
        int visible = 0;
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            if (PdfFont.IsInvisible(text, i)) continue;
            visible++;
            if (Text.UnicodeData.Bidi(char.ConvertToUtf32(text, i)) is Text.BidiClass.R or Text.BidiClass.AL or Text.BidiClass.AN) return true;
        }

        return visible != glyphs.Count || glyphs.Any(g => g.XOffset != 0 || g.YOffset != 0);
    }

    private void BeginActualText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsControl(c)) sb.Append(c);
        }

        byte[] utf16 = System.Text.Encoding.BigEndianUnicode.GetBytes(sb.ToString());
        var bytes = new byte[utf16.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        utf16.CopyTo(bytes, 2);
        _w.Name("Span").Raw("<</ActualText").HexString(bytes).Raw(">>").Op("BDC");
    }

    /// <summary>Draws an image at its natural size: its pixels at the resolution it records, else 96 per inch.</summary>
    public void DrawImage(PdfImage image, double x, double y)
    {
        Compat.ThrowIfNull(image, nameof(image));
        DrawImage(image, x, y, image.Width, image.Height);
    }

    /// <summary>
    /// Draws an image stretched to the rectangle, upright as its EXIF orientation says. Painting
    /// follows the current transform and clip; transparency comes from the image's alpha channel.
    /// </summary>
    public void DrawImage(PdfImage image, double x, double y, double width, double height)
    {
        ThrowIfDisposed();
        Compat.ThrowIfNull(image, nameof(image));
        string name = AddResource("XObject", "Im", _doc.GetImageReference(image));
        using (Save())
        {
            _w.Matrix(image.Placement(x, y, width, height)).Op("cm").Name(name).Op("Do");
        }
    }

    /// <summary>Draws a form at its own size, its top left corner at (x, y).</summary>
    public void DrawForm(PdfForm form, double x, double y)
    {
        Compat.ThrowIfNull(form, nameof(form));
        DrawForm(form, x, y, form.Width, form.Height);
    }

    /// <summary>Draws a form scaled into the rectangle: a stamp, a template, a page shown in miniature.</summary>
    /// <exception cref="ArgumentException">The form belongs to another document.</exception>
    public void DrawForm(PdfForm form, double x, double y, double width, double height)
    {
        ThrowIfDisposed();
        Compat.ThrowIfNull(form, nameof(form));
        if (!ReferenceEquals(form.Document, _doc)) throw new ArgumentException("The form belongs to another document. Use PdfForm.FromPage to bring a page over.", nameof(form));
        if (ReferenceEquals(form, _form)) throw new ArgumentException("A form cannot draw itself.", nameof(form));
        string name = AddResource("XObject", "Fm", form.Reference);
        using (Save())
        {
            // The form's space has y up from its bottom left corner.
            _w.Matrix(new PdfMatrix(width / form.Width, 0, 0, -height / form.Height, x, y + height)).Op("cm").Name(name).Op("Do");
        }
    }

    /// <summary>
    /// Paints an XObject placed by a matrix in PDF user space (y up), whatever the canvas
    /// transform: for appearance streams, whose placement the specification defines that way.
    /// </summary>
    internal void DrawPdfSpaceXObject(PdfReference xobject, PdfMatrix placement)
    {
        ThrowIfDisposed();
        string name = AddResource("XObject", "Fm", xobject);
        PdfMatrix ctm = PdfMatrix.Multiply(_state.Matrix, _flip);
        using (Save())
        {
            _w.Matrix(PdfMatrix.Multiply(placement, ctm.Invert())).Op("cm").Name(name).Op("Do");
        }
    }

    /// <summary>Writes the drawing into the page. Further drawing is not possible.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _doc.UnregisterGraphics(this);
        while (_saved.Count > 0)
        {
            _saved.Pop();
            _w.Op("Q");
        }

        _w.Op("Q");
        Attach();
    }

    private void Attach()
    {
        if (_form is not null)
        {
            _form.AppendContent(_w.Written);
            return;
        }

        PdfDocument doc = _doc;
        PdfDictionary page = _page!.Dictionary;
        var existing = new List<PdfObject>();
        switch (doc.Resolve(page["Contents"]))
        {
            case PdfStream when page["Contents"] is PdfReference r:
                existing.Add(r);
                break;
            case PdfArray a:
                existing.AddRange(a);
                break;
        }

        if (existing.Count == 0)
        {
            page.Set("Contents", doc.AddObject(PdfStream.FromData(_w.Written)));
            return;
        }

        var contents = new PdfArray();
        if (_layer == PdfGraphicsLayer.Background)
        {
            contents.Add(doc.AddObject(PdfStream.FromData(_w.Written)));
            foreach (PdfObject o in existing) contents.Add(o);
        }
        else
        {
            // q before the old content, Q after it, so its state cannot leak into the new drawing.
            contents.Add(doc.AddObject(PdfStream.FromData("q\n"u8, compress: false)));
            foreach (PdfObject o in existing) contents.Add(o);
            var closing = new ContentWriter().Op("Q").Bytes(_w.Written);
            contents.Add(doc.AddObject(PdfStream.FromData(closing.Written)));
        }

        page.Set("Contents", contents);
    }

    /// <summary>
    /// One text object. The text matrix flips y back up so glyphs stand upright on the y down
    /// canvas. Where a glyph's advance differs from the width the viewer will use (kerning), a
    /// TJ adjustment makes up the difference.
    /// </summary>
    private void WriteText(FontResource resource, string name, double size, List<ShapedGlyph> glyphs, double x, double y, int mode)
    {
        _w.Op("BT");
        if (_state.FontName != name || _state.FontSize != size)
        {
            _w.Name(name).Number(size).Op("Tf");
            _state.FontName = name;
            _state.FontSize = size;
        }

        if (_state.TextMode != mode)
        {
            _w.Number(mode).Op("Tr");
            _state.TextMode = mode;
        }

        _w.Numbers(1, 0, 0, -1).Numbers(x, y).Op("Tm");

        // Each glyph sits at its pen position plus its offset. TJ numbers (thousandths of the font
        // size, subtracted from the position) move to the offset before a glyph, then make the
        // viewer's advance, the width written for the glyph, come out at the shaped advance.
        // Vertical offsets, as for Arabic marks, use the text rise.
        var run = new List<byte>(glyphs.Count * 2);
        var parts = new List<(byte[] Codes, double Adjust)>();
        double pending = 0;
        foreach (ShapedGlyph g in glyphs)
        {
            double rise = Math.Round(g.YOffset, 3);
            if (rise != _state.TextRise)
            {
                Flush();
                _w.Number(rise / 1000 * size).Op("Ts");
                _state.TextRise = rise;
            }

            pending = Math.Round(pending - g.XOffset, 3);
            if (pending != 0)
            {
                parts.Add(([.. run], pending));
                run.Clear();
            }

            int code = resource.Encode(g);
            if (resource.IsTwoByte) run.Add((byte)(code >> 8));
            run.Add((byte)code);
            pending = Math.Round(resource.Width(g.Glyph) + g.XOffset - g.Advance, 3);
        }

        Flush();
        if (_state.TextRise != 0)
        {
            _w.Number(0).Op("Ts");
            _state.TextRise = 0;
        }

        _w.Op("ET");

        void Flush()
        {
            if (run.Count > 0) parts.Add(([.. run], 0));
            run.Clear();
            pending = 0;
            if (parts.Count == 0) return;
            if (parts.Count == 1 && parts[0].Adjust == 0)
            {
                WriteString(parts[0].Codes);
                _w.Op("Tj");
            }
            else
            {
                _w.BeginArray();
                foreach ((byte[] codes, double adjust) in parts)
                {
                    if (codes.Length > 0) WriteString(codes);
                    if (adjust != 0) _w.Number(adjust);
                }

                _w.EndArray().Op("TJ");
            }

            parts.Clear();
        }

        void WriteString(byte[] codes)
        {
            if (resource.IsTwoByte) _w.HexString(codes);
            else _w.LiteralString(codes);
        }
    }

    private void SetFill(PdfColor color)
    {
        if (_state.Fill != color)
        {
            WriteColor(color, stroke: false);
            _state.Fill = color;
        }

        SetAlpha("ca", color.Alpha, ref _state.FillAlpha);
    }

    private void SetPen(PdfPen pen)
    {
        if (_state.Stroke != pen.Color)
        {
            WriteColor(pen.Color, stroke: true);
            _state.Stroke = pen.Color;
        }

        SetAlpha("CA", pen.Color.Alpha, ref _state.StrokeAlpha);
        if (_state.LineWidth != pen.Width)
        {
            _w.Number(pen.Width).Op("w");
            _state.LineWidth = pen.Width;
        }

        if (_state.Cap != pen.LineCap)
        {
            _w.Number((int)pen.LineCap).Op("J");
            _state.Cap = pen.LineCap;
        }

        if (_state.Join != pen.LineJoin)
        {
            _w.Number((int)pen.LineJoin).Op("j");
            _state.Join = pen.LineJoin;
        }

        if (_state.MiterLimit != pen.MiterLimit)
        {
            _w.Number(pen.MiterLimit).Op("M");
            _state.MiterLimit = pen.MiterLimit;
        }

        if (!_state.Dash.SequenceEqual(pen.DashPattern) || _state.DashOffset != pen.DashOffset)
        {
            var dash = new ContentWriter();
            foreach (double d in pen.DashPattern) dash.Number(d);
            _w.Raw("[" + System.Text.Encoding.ASCII.GetString(dash.Written.ToArray()) + "]").Number(pen.DashOffset).Op("d");
            _state.Dash = pen.DashPattern;
            _state.DashOffset = pen.DashOffset;
        }
    }

    private void WriteColor(PdfColor color, bool stroke)
    {
        if (color.Space == PdfColorSpace.Cmyk) _doc.UsesDeviceCmyk = true;
        double[] c = color.Components();
        foreach (double v in c) _w.Number(v);
        _w.Op(color.Space switch
        {
            PdfColorSpace.Gray => stroke ? "G" : "g",
            PdfColorSpace.Rgb => stroke ? "RG" : "rg",
            _ => stroke ? "K" : "k",
        });
    }

    private void SetAlpha(string key, double alpha, ref double current)
    {
        if (current == alpha) return;
        if (!_alphaStates.TryGetValue((key, alpha), out string? name))
        {
            var gs = new PdfDictionary { ["Type"] = new PdfName("ExtGState"), [key] = PdfNumber.FromDouble(alpha) };
            name = AddResource("ExtGState", "Gs", gs);
            _alphaStates[(key, alpha)] = name;
        }

        _w.Name(name).Op("gs");
        current = alpha;
    }

    private string AddShading(PdfGradientBrush brush)
    {
        PdfGradientStop[] stops = brush.Stops.ToArray();
        PdfColorSpace space = stops[0].Color.Space;
        if (stops.Any(s => s.Color.Space != space))
        {
            space = PdfColorSpace.Rgb;
            for (int i = 0; i < stops.Length; i++) stops[i] = stops[i] with { Color = stops[i].Color.ToRgb() };
        }

        if (space == PdfColorSpace.Cmyk) _doc.UsesDeviceCmyk = true;
        var shading = new PdfDictionary
        {
            ["ColorSpace"] = new PdfName(space switch { PdfColorSpace.Gray => "DeviceGray", PdfColorSpace.Rgb => "DeviceRGB", _ => "DeviceCMYK" }),
        };
        if (brush is PdfLinearGradientBrush linear)
        {
            shading.Set("ShadingType", PdfNumber.FromInteger(2));
            shading.Set("Coords", PdfArray.Of(linear.Start.X, linear.Start.Y, linear.End.X, linear.End.Y));
        }
        else
        {
            var radial = (PdfRadialGradientBrush)brush;
            shading.Set("ShadingType", PdfNumber.FromInteger(3));
            shading.Set("Coords", PdfArray.Of(radial.StartCenter.X, radial.StartCenter.Y, radial.StartRadius, radial.EndCenter.X, radial.EndCenter.Y, radial.EndRadius));
        }

        shading.Set("Function", GradientFunction(stops));
        if (brush.Extend) shading.Set("Extend", PdfArray.Of(PdfBoolean.True, PdfBoolean.True));
        return AddResource("Shading", "Sh", shading);
    }

    /// <summary>
    /// A type 2 function for two stops, otherwise a type 3 stitching function of type 2 pieces.
    /// Stops are padded to cover 0 to 1, and hard stops get a tiny interval since bounds must increase.
    /// </summary>
    internal static PdfDictionary GradientFunction(PdfGradientStop[] stops)
    {
        var list = new List<PdfGradientStop>(stops);
        if (list[0].Offset > 0) list.Insert(0, list[0] with { Offset = 0 });
        if (list[^1].Offset < 1) list.Add(list[^1] with { Offset = 1 });
        if (list.Count == 2) return Interpolation(list[0].Color, list[1].Color);

        var functions = new PdfArray();
        var bounds = new PdfArray();
        var encode = new PdfArray();
        double previous = 0;
        for (int i = 0; i < list.Count - 1; i++)
        {
            functions.Add(Interpolation(list[i].Color, list[i + 1].Color));
            encode.Add(PdfNumber.FromInteger(0));
            encode.Add(PdfNumber.FromInteger(1));
            if (i > 0)
            {
                double bound = Math.Max(list[i].Offset, previous + 1e-5);
                bounds.Add(PdfNumber.FromDouble(Math.Min(bound, 1)));
                previous = bound;
            }
        }

        return new PdfDictionary
        {
            ["FunctionType"] = PdfNumber.FromInteger(3),
            ["Domain"] = PdfArray.Of(0, 1),
            ["Functions"] = functions,
            ["Bounds"] = bounds,
            ["Encode"] = encode,
        };

        static PdfDictionary Interpolation(PdfColor from, PdfColor to) => new()
        {
            ["FunctionType"] = PdfNumber.FromInteger(2),
            ["Domain"] = PdfArray.Of(0, 1),
            ["C0"] = PdfArray.Of(from.Components()),
            ["C1"] = PdfArray.Of(to.Components()),
            ["N"] = PdfNumber.FromInteger(1),
        };
    }

    /// <summary>Adds an entry to the page's resources under a fresh name and returns the name.</summary>
    internal string AddResource(string category, string prefix, PdfObject value)
    {
        PdfDocument doc = _doc;
        if (_resources is null)
        {
            // Resources may be shared with other pages; write into a private copy.
            PdfDictionary current = doc.Resolve(_resourceOwner["Resources"]) as PdfDictionary ?? new PdfDictionary();
            _resources = PdfDocument.Clone(current);
            _resourceOwner.Set("Resources", _resources);
        }

        // Each category dictionary is copied too: the shallow copy above still shares them.
        PdfDictionary sub;
        if (_ownCategories.Add(category))
        {
            sub = doc.Resolve(_resources[category]) is PdfDictionary shared ? PdfDocument.Clone(shared) : new PdfDictionary();
            _resources.Set(category, sub);
        }
        else
        {
            sub = (PdfDictionary)_resources[category]!;
        }

        // An object the page already refers to, such as a font drawn by an earlier canvas, keeps its name.
        if (value is PdfReference reference)
        {
            foreach (KeyValuePair<string, PdfObject> entry in sub)
            {
                if (entry.Value is PdfReference r && r.Number == reference.Number) return entry.Key;
            }
        }

        int n = sub.Count + 1;
        while (sub.ContainsKey(prefix + n)) n++;
        string name = prefix + n;
        sub.Set(name, value);
        return name;
    }

    private void ThrowIfDisposed() => Compat.ThrowIfDisposed(_disposed, this);

    /// <summary>Mirror of the PDF graphics state, so unchanged parameters are not written again.</summary>
    private sealed class State
    {
        public PdfMatrix Matrix = PdfMatrix.Identity;
        public PdfColor Fill = PdfColor.Black;
        public PdfColor Stroke = PdfColor.Black;
        public double FillAlpha = 1;
        public double StrokeAlpha = 1;
        public double LineWidth = 1;
        public PdfLineCap Cap;
        public PdfLineJoin Join;
        public double MiterLimit = 10;
        public IReadOnlyList<double> Dash = [];
        public double DashOffset;
        public string? FontName;
        public double FontSize;
        public int TextMode;
        public double TextRise;
        public PdfBlendMode BlendMode;

        public State Clone() => (State)MemberwiseClone();
    }
}

/// <summary>A saved graphics state; disposing it restores the state, along with any saved after it.</summary>
public readonly struct PdfGraphicsState : IDisposable
{
    private readonly PdfGraphics? _graphics;
    private readonly int _depth;

    internal PdfGraphicsState(PdfGraphics graphics, int depth)
    {
        _graphics = graphics;
        _depth = depth;
    }

    /// <summary>Restores the saved state.</summary>
    public void Dispose() => _graphics?.RestoreTo(_depth);
}
