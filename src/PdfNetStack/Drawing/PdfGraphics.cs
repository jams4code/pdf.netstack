using PdfNetStack.Objects;

namespace PdfNetStack.Drawing;

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
    private readonly PdfPage _page;
    private readonly PdfGraphicsLayer _layer;
    private readonly ContentWriter _w = new();
    private readonly Stack<State> _saved = new();
    private readonly Dictionary<(string Key, double Value), string> _alphaStates = new();
    private readonly HashSet<string> _ownCategories = new(StringComparer.Ordinal);
    private PdfDictionary? _resources;
    private State _state = new();
    private bool _disposed;

    internal PdfGraphics(PdfPage page, PdfGraphicsLayer layer)
    {
        _page = page;
        _layer = layer;
        PdfRectangle box = page.MediaBox;
        Width = box.Width;
        Height = box.Height;

        // Flip to a top left origin with y down. Everything below is in canvas coordinates.
        _w.Op("q").Matrix(new PdfMatrix(1, 0, 0, -1, box.Left, box.Top)).Op("cm");
        page.Owner.RegisterGraphics(this);
    }

    /// <summary>The page being drawn on.</summary>
    public PdfPage Page => _page;

    /// <summary>Media box width in points.</summary>
    public double Width { get; }

    /// <summary>Media box height in points.</summary>
    public double Height { get; }

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

    /// <summary>Writes the drawing into the page. Further drawing is not possible.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _page.Owner.UnregisterGraphics(this);
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
        PdfDocument doc = _page.Owner;
        PdfDictionary page = _page.Dictionary;
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
        PdfDocument doc = _page.Owner;
        if (_resources is null)
        {
            // Resources may be shared with other pages; write into a private copy.
            PdfDictionary current = doc.Resolve(_page.Dictionary["Resources"]) as PdfDictionary ?? new PdfDictionary();
            _resources = PdfDocument.Clone(current);
            _page.Dictionary.Set("Resources", _resources);
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
