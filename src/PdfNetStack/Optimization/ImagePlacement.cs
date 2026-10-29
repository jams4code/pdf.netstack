using PdfNetStack.Content;
using PdfNetStack.Drawing;
using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// The largest size, in inches, at which an image is drawn: how long its own x and y axes become
/// on the page, over every placement found. Effective resolution is pixels over this size.
/// </summary>
/// <param name="Width">Longest placed length of the image's width.</param>
/// <param name="Height">Longest placed length of the image's height.</param>
internal readonly record struct PlacedSize(double Width, double Height);

/// <summary>
/// Finds where images are drawn by walking content with its transformation matrix (ISO 32000-1,
/// 8.3 and 8.4): page content, form XObjects, tiling patterns, soft mask groups and annotation
/// appearances, each with the matrix that maps it onto the page. An image is measured only when
/// every reference to it sits in an XObject resource dictionary that the walk interpreted in
/// full; anything else might draw it at a size the walk never saw, so it gets no entry and is
/// never downsampled.
/// </summary>
/// <remarks>
/// Type 3 glyphs are not followed: images in a Type 3 font's resources, or in resources a Type 3
/// font without its own shares, count as unmeasured. Content that cannot be decoded, forms nested
/// too deep and walks over budget make their images unmeasured the same way.
/// </remarks>
internal sealed class ImagePlacement
{
    private const int MaxFormDepth = 16;
    private const int MaxStateDepth = 256;
    private const int MaxPageTreeDepth = 64;
    private const long MaxOperations = 50_000_000;
    private const long MaxBytes = 1L << 30;

    private readonly PdfFile _file;
    private readonly long _decodeLimit;
    private readonly CancellationToken _ct;
    private readonly Dictionary<int, PlacedSize> _sizes = [];
    private readonly HashSet<PdfDictionary> _interpreted = [];
    private readonly HashSet<PdfDictionary> _tainted = [];
    private readonly HashSet<(int Number, long A, long B, long C, long D)> _walked = [];
    private readonly HashSet<int> _active = [];
    private long _operations;
    private long _bytes;
    private bool _overBudget;

    private ImagePlacement(PdfFile file, long decodeLimit, CancellationToken ct)
    {
        _file = file;
        _decodeLimit = decodeLimit;
        _ct = ct;
    }

    /// <summary>Placed sizes of the <paramref name="candidates"/> that are measured with certainty.</summary>
    public static Dictionary<int, PlacedSize> Measure(PdfFile file, ObjectGraph graph, HashSet<int> candidates, long decodeLimit, CancellationToken ct)
    {
        if (candidates.Count == 0) return [];
        var walk = new ImagePlacement(file, decodeLimit, ct);
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        walk.PageTree(file.Resolve(catalog?["Pages"]) as PdfDictionary, null, 0, []);
        if (walk._overBudget) return [];

        var measured = new Dictionary<int, PlacedSize>();
        HashSet<int> unaccounted = walk.UnaccountedReferences(graph, candidates);
        foreach (KeyValuePair<int, PlacedSize> entry in walk._sizes)
        {
            if (candidates.Contains(entry.Key) && !unaccounted.Contains(entry.Key)) measured[entry.Key] = entry.Value;
        }

        return measured;
    }

    /// <summary>Images referenced from anywhere but an XObject dictionary the walk interpreted cleanly.</summary>
    private HashSet<int> UnaccountedReferences(ObjectGraph graph, HashSet<int> candidates)
    {
        var unaccounted = new HashSet<int>();
        void Visit(PdfObject? value, PdfDictionary? container, int depth)
        {
            switch (value)
            {
                case PdfReference r when candidates.Contains(r.Number):
                    if (container is null || !_interpreted.Contains(container) || _tainted.Contains(container)) unaccounted.Add(r.Number);
                    break;
                case PdfArray a when depth < PdfParser.MaxNesting:
                    foreach (PdfObject item in a.Items) Visit(item, null, depth + 1);
                    break;
                case PdfDictionary d when depth < PdfParser.MaxNesting:
                    for (int i = 0; i < d.Count; i++) Visit(d.ValueAt(i), d, depth + 1);
                    break;
                case PdfStream s:
                    Visit(s.Dictionary, null, depth + 1);
                    break;
            }
        }

        foreach (int number in graph.Order) Visit(_file.GetObject(number), null, 0);
        return unaccounted;
    }

    private void PageTree(PdfDictionary? node, PdfObject? inheritedResources, int depth, HashSet<PdfDictionary> seen)
    {
        if (node is null || depth > MaxPageTreeDepth || !seen.Add(node) || _overBudget) return;
        PdfObject? resources = node["Resources"] ?? inheritedResources;
        if (_file.Resolve(node["Kids"]) is PdfArray kids)
        {
            foreach (PdfObject kid in kids.Items) PageTree(_file.Resolve(kid) as PdfDictionary, resources, depth + 1, seen);
            return;
        }

        Page(node, _file.Resolve(resources) as PdfDictionary);
    }

    private void Page(PdfDictionary page, PdfDictionary? resources)
    {
        double unit = _file.Resolve(page["UserUnit"]) is PdfNumber u && u.ToDouble() is > 0 and < 1e6 ? u.ToDouble() : 1;
        PdfMatrix pageSpace = PdfMatrix.CreateScale(unit, unit);

        PdfObject? contents = _file.Resolve(page["Contents"]);
        IEnumerable<PdfObject> parts = contents switch
        {
            PdfArray array => array.Items,
            PdfStream stream => [stream],
            _ => [],
        };

        // Content split across streams is one stream (ISO 32000-1, 7.8.2).
        using var joined = new MemoryStream();
        bool damaged = false;
        foreach (PdfObject part in parts)
        {
            if (_file.Resolve(part) is not PdfStream stream) continue;
            byte[]? data = Decode(stream);
            if (data is null)
            {
                damaged = true;
                continue;
            }

            joined.Write(data, 0, data.Length);
            joined.WriteByte((byte)'\n');
        }

        if (damaged) Taint(resources);
        Execute(joined.ToArray(), resources, pageSpace, 0);

        if (_file.Resolve(page["Annots"]) is not PdfArray annotations) return;
        foreach (PdfObject item in annotations.Items)
        {
            if (_file.Resolve(item) is not PdfDictionary annotation) continue;
            if (_file.Resolve(annotation["AP"]) is not PdfDictionary appearances) continue;
            double[]? rect = Numbers(_file.Resolve(annotation["Rect"]) as PdfArray, 4);
            foreach (string key in new[] { "N", "R", "D" })
            {
                PdfObject? entry = appearances[key];
                if (_file.Resolve(entry) is PdfDictionary states)
                {
                    foreach (string state in states.Keys) Appearance(states[state], rect, resources, pageSpace);
                }
                else
                {
                    Appearance(entry, rect, resources, pageSpace);
                }
            }
        }
    }

    /// <summary>
    /// An appearance stream is fitted into the annotation rectangle (ISO 32000-1, 12.5.5): its
    /// bounding box, transformed by its matrix, is mapped onto Rect.
    /// </summary>
    private void Appearance(PdfObject? entry, double[]? rect, PdfDictionary? pageResources, PdfMatrix pageSpace)
    {
        if (entry is not PdfReference reference || _file.Resolve(reference) is not PdfStream form) return;
        double[]? box = Numbers(_file.Resolve(form.Dictionary["BBox"]) as PdfArray, 4);
        if (rect is null || box is null) return;
        PdfMatrix matrix = FormMatrix(form);
        PdfPoint[] corners =
        [
            matrix.Transform(new PdfPoint(box[0], box[1])), matrix.Transform(new PdfPoint(box[2], box[1])),
            matrix.Transform(new PdfPoint(box[0], box[3])), matrix.Transform(new PdfPoint(box[2], box[3])),
        ];
        double x0 = corners.Min(p => p.X), x1 = corners.Max(p => p.X), y0 = corners.Min(p => p.Y), y1 = corners.Max(p => p.Y);
        double rx0 = Math.Min(rect[0], rect[2]), rx1 = Math.Max(rect[0], rect[2]), ry0 = Math.Min(rect[1], rect[3]), ry1 = Math.Max(rect[1], rect[3]);
        if (x1 - x0 < 1e-9 || y1 - y0 < 1e-9) return;
        double sx = (rx1 - rx0) / (x1 - x0), sy = (ry1 - ry0) / (y1 - y0);
        var fit = new PdfMatrix(sx, 0, 0, sy, rx0 - (sx * x0), ry0 - (sy * y0));
        Form(form, PdfMatrix.Multiply(fit, pageSpace), pageResources, 1, reference.Number);
    }

    private void Execute(byte[] content, PdfDictionary? resources, PdfMatrix ctm, int depth)
    {
        _bytes += content.Length;
        if (_bytes > MaxBytes) _overBudget = true;
        if (_overBudget) return;

        Interpret(resources, ctm, depth);
        var reader = new ContentReader(content);
        var op = new ContentOperation();
        var saved = new Stack<PdfMatrix>();
        while (reader.Next(op))
        {
            if (++_operations > MaxOperations)
            {
                _overBudget = true;
                return;
            }

            if ((_operations & 4095) == 0) _ct.ThrowIfCancellationRequested();
            List<PdfObject> o = op.Operands;
            switch (op.Operator)
            {
                case "q":
                    if (saved.Count < MaxStateDepth) saved.Push(ctm);
                    break;
                case "Q":
                    if (saved.Count > 0) ctm = saved.Pop();
                    break;
                case "cm" when Numbers(o, 6) is { } m:
                    ctm = PdfMatrix.Multiply(new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]), ctm);
                    break;
                case "Do" when o.Count > 0 && o[^1] is PdfName name:
                    Draw(name.Value, resources, ctm, depth);
                    break;
                case "gs" when o.Count > 0 && o[^1] is PdfName name:
                    SoftMask(name.Value, resources, ctm, depth);
                    break;
            }
        }
    }

    /// <summary>
    /// Records a resource dictionary as interpreted: its XObjects are drawn only through content
    /// walked here. Tiling patterns in it are walked too, in the space of the content stream that
    /// uses them, whether or not they are painted, which can only overstate a size.
    /// </summary>
    private void Interpret(PdfDictionary? resources, PdfMatrix ctm, int depth)
    {
        if (resources is null) return;
        if (_file.Resolve(resources["XObject"]) is PdfDictionary xobjects) _interpreted.Add(xobjects);

        if (_file.Resolve(resources["Font"]) is PdfDictionary fonts)
        {
            foreach (string key in fonts.Keys)
            {
                // Glyphs are not walked: a Type 3 font's own resources, or these when it has none,
                // may draw images at sizes the walk never sees, even when shared with a page.
                if (_file.Resolve(fonts[key]) is PdfDictionary font && font.GetName("Subtype") == "Type3")
                {
                    Taint(_file.Resolve(font["Resources"]) as PdfDictionary ?? resources);
                }
            }
        }

        if (_file.Resolve(resources["Pattern"]) is PdfDictionary patterns)
        {
            foreach (string key in patterns.Keys)
            {
                if (_file.Resolve(patterns[key]) is not PdfStream pattern || _file.Resolve(pattern.Dictionary["PatternType"]) is not PdfNumber { IntegerValue: 1 }) continue;
                if (depth < MaxFormDepth && patterns[key] is PdfReference reference) Form(pattern, ctm, resources, depth + 1, reference.Number);
                else Taint(_file.Resolve(pattern.Dictionary["Resources"]) as PdfDictionary);
            }
        }
    }

    private void Draw(string name, PdfDictionary? resources, PdfMatrix ctm, int depth)
    {
        if (_file.Resolve(resources?["XObject"]) is not PdfDictionary xobjects || xobjects[name] is not PdfReference reference) return;
        if (_file.Resolve(reference) is not PdfStream xobject) return;
        switch (xobject.Dictionary.GetName("Subtype"))
        {
            case "Image":
                double width = Math.Sqrt((ctm.A * ctm.A) + (ctm.B * ctm.B)) / 72;
                double height = Math.Sqrt((ctm.C * ctm.C) + (ctm.D * ctm.D)) / 72;
                if (double.IsNaN(width) || double.IsNaN(height)) break;
                _sizes[reference.Number] = _sizes.TryGetValue(reference.Number, out PlacedSize size)
                    ? new PlacedSize(Math.Max(size.Width, width), Math.Max(size.Height, height))
                    : new PlacedSize(width, height);
                break;
            case "Form":
                if (depth >= MaxFormDepth) Taint(_file.Resolve(xobject.Dictionary["Resources"]) as PdfDictionary ?? resources);
                else Form(xobject, ctm, resources, depth + 1, reference.Number);
                break;
        }
    }

    /// <summary>An ExtGState soft mask paints its group form with the matrix current when it is set (ISO 32000-1, 11.6.5.2).</summary>
    private void SoftMask(string name, PdfDictionary? resources, PdfMatrix ctm, int depth)
    {
        if (_file.Resolve(resources?["ExtGState"]) is not PdfDictionary states || _file.Resolve(states[name]) is not PdfDictionary state) return;
        if (_file.Resolve(state["SMask"]) is not PdfDictionary mask || mask["G"] is not PdfReference group) return;
        if (_file.Resolve(group) is PdfStream form && depth < MaxFormDepth) Form(form, ctm, resources, depth + 1, group.Number);
    }

    /// <summary>
    /// Walks a form, pattern or group with its matrix applied. One drawn at a scale already walked
    /// adds nothing, so each is walked once per distinct linear part of its matrix.
    /// </summary>
    private void Form(PdfStream form, PdfMatrix ctm, PdfDictionary? parentResources, int depth, int number)
    {
        ctm = PdfMatrix.Multiply(FormMatrix(form), ctm);
        PdfDictionary? resources = _file.Resolve(form.Dictionary["Resources"]) as PdfDictionary ?? parentResources;
        if (!_walked.Add((number, Key(ctm.A), Key(ctm.B), Key(ctm.C), Key(ctm.D))) || !_active.Add(number)) return;
        try
        {
            byte[]? data = Decode(form);
            if (data is null) Taint(resources);
            else Execute(data, resources, ctm, depth);
        }
        finally
        {
            _active.Remove(number);
        }
    }

    private static long Key(double v) => (long)Math.Round(v * 1e6);

    private PdfMatrix FormMatrix(PdfStream form) =>
        Numbers(_file.Resolve(form.Dictionary["Matrix"]) as PdfArray, 6) is { } m ? new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]) : PdfMatrix.Identity;

    /// <summary>Content of these resources was not fully walked, so images they hold are not measured.</summary>
    private void Taint(PdfDictionary? resources)
    {
        if (_file.Resolve(resources?["XObject"]) is PdfDictionary xobjects) _tainted.Add(xobjects);
    }

    private byte[]? Decode(PdfStream stream)
    {
        try
        {
            List<FilterSpec> filters = StreamCodec.GetFilters(stream.Dictionary, _file.Resolve);
            using DecodedStream decoded = StreamCodec.Decode(stream.Data, filters, DecodeDepth.Full, _decodeLimit, _file.Resolve);
            if (decoded.Remaining.Count > 0 || decoded.PendingPredictor is not null) return null;
            return decoded.Data.ToArray();
        }
        catch (StreamDecodeException)
        {
            return null;
        }
    }

    private static double[]? Numbers(IReadOnlyList<PdfObject>? o, int count)
    {
        if (o is null || o.Count < count) return null;
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (o[o.Count - count + i] is not PdfNumber n) return null;
            double v = n.ToDouble();
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
            result[i] = v;
        }

        return result;
    }
}
