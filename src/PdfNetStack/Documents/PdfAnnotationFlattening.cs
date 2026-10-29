using PdfNetStack.Drawing;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>Which annotations flattening turns into page content, besides markup such as notes, stamps, shapes and highlights.</summary>
public sealed class PdfFlattenOptions
{
    /// <summary>Also flatten links: their look stays, they stop working. Default false.</summary>
    public bool IncludeLinks { get; init; }

    /// <summary>
    /// Also flatten form widgets: the field shows its current value and leaves the form. Default
    /// false. <see cref="PdfAcroForm.Flatten"/> flattens a whole form.
    /// </summary>
    public bool IncludeWidgets { get; init; }
}

/// <content>
/// Flattening (ISO 32000-1, 12.5.5): an annotation's normal appearance is drawn into the page
/// content where the annotation shows it, and the annotation is removed.
/// </content>
public sealed partial class PdfAnnotationCollection
{
    private const int FlagHidden = 2;
    private const int FlagNoRotate = 16;
    private const int FlagNoView = 32;

    /// <summary>
    /// Draws the annotation's appearance into the page content and removes the annotation, with
    /// its pop up. A widget also leaves the form. Returns false, changing nothing, when the
    /// annotation is not on this page, is hidden or not shown on screen (flags Hidden and
    /// NoView), or has no appearance to draw. A link without an appearance is removed.
    /// </summary>
    /// <example>
    /// <code>
    /// PdfAnnotation stamp = page.Annotations.AddStamp(PdfStampName.Final, 400, 40, 150, 45);
    /// page.Annotations.Flatten(stamp);
    /// </code>
    /// </example>
    public bool Flatten(PdfAnnotation annotation)
    {
        Compat.ThrowIfNull(annotation, nameof(annotation));
        return Flatten((entry, _) => Same(entry, annotation.Entry)) > 0;
    }

    /// <summary>
    /// Draws every markup annotation's appearance (notes, text markup, free text, stamps, ink,
    /// shapes, file attachments) into the page content and removes it. Hidden and NoView
    /// annotations, pop ups and annotations without an appearance stay. Links and form widgets
    /// stay unless <paramref name="options"/> asks for them. Returns how many were flattened.
    /// </summary>
    public int Flatten(PdfFlattenOptions? options = null)
    {
        options ??= new PdfFlattenOptions();
        return Flatten((_, d) => d.GetName("Subtype") switch
        {
            "Link" => options.IncludeLinks,
            "Widget" => options.IncludeWidgets,
            _ => true,
        });
    }

    /// <summary>
    /// Paints an annotation's normal appearance on the page as a viewer would (ISO 32000-1,
    /// 12.5.5): the form's bounding box, transformed by its matrix, is fitted to the annotation
    /// rectangle. NoRotate annotations stay upright on a rotated page, turned about their top left
    /// corner, and optional content keeps its visibility. Returns false when there is nothing to paint.
    /// </summary>
    internal static bool DrawAppearance(PdfGraphics g, PdfPage page, PdfDictionary annotation)
    {
        PdfDocument doc = page.Owner;
        if (NormalAppearance(doc, annotation) is not PdfReference appearance) return false;
        PdfStream stream = (PdfStream)doc.Resolve(appearance)!;
        if (stream.Dictionary.GetName("Subtype") is string subtype && subtype != "Form") return false;
        PdfRectangle r = PdfRectangle.From(annotation["Rect"], doc.Resolve) ?? default;
        var rect = new PdfRectangle(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));
        if (!(rect.Width > 0) || !(rect.Height > 0) || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height)) return false;

        stream.Dictionary.Set("Type", new PdfName("XObject"));
        stream.Dictionary.Set("Subtype", new PdfName("Form"));
        PdfMatrix placement = PlaceAppearance(doc, stream.Dictionary, rect);
        long flags = doc.Resolve(annotation["F"]) is PdfNumber { IsInteger: true } f ? f.IntegerValue : 0;
        if ((flags & FlagNoRotate) != 0 && page.Rotation != 0)
        {
            // Turned against the page rotation about the top left corner, so it reads upright.
            PdfMatrix turn = PdfMatrix.Multiply(PdfMatrix.Multiply(PdfMatrix.CreateTranslation(-rect.Left, -rect.Top), PdfMatrix.CreateRotation(page.Rotation)), PdfMatrix.CreateTranslation(rect.Left, rect.Top));
            placement = PdfMatrix.Multiply(placement, turn);
        }

        PdfObject? group = annotation["OC"];
        bool optional = doc.Resolve(group) is PdfDictionary;
        if (optional) g.BeginOptionalContent(group!);
        g.DrawPdfSpaceXObject(appearance, placement);
        if (optional) g.EndMarkedContent();
        return true;
    }

    /// <summary>
    /// The matrix that maps an appearance onto an annotation rectangle (ISO 32000-1, 12.5.5): the
    /// bounding box, transformed by the form's own matrix, is scaled and moved onto the rectangle.
    /// </summary>
    internal static PdfMatrix PlaceAppearance(PdfDocument doc, PdfDictionary form, PdfRectangle rect)
    {
        PdfRectangle bbox = PdfRectangle.From(form["BBox"], doc.Resolve) ?? new PdfRectangle(0, 0, rect.Width, rect.Height);
        PdfMatrix matrix = doc.Resolve(form["Matrix"]) is PdfArray { Count: 6 } m && m.All(v => doc.Resolve(v) is PdfNumber)
            ? new PdfMatrix(Num(m[0]), Num(m[1]), Num(m[2]), Num(m[3]), Num(m[4]), Num(m[5]))
            : PdfMatrix.Identity;
        PdfPoint[] corners =
        [
            matrix.Transform(new PdfPoint(bbox.Left, bbox.Bottom)), matrix.Transform(new PdfPoint(bbox.Right, bbox.Bottom)),
            matrix.Transform(new PdfPoint(bbox.Left, bbox.Top)), matrix.Transform(new PdfPoint(bbox.Right, bbox.Top)),
        ];
        double minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y);
        double w = corners.Max(c => c.X) - minX, h = corners.Max(c => c.Y) - minY;
        double sx = w > 0 && !double.IsInfinity(w) ? rect.Width / w : 1, sy = h > 0 && !double.IsInfinity(h) ? rect.Height / h : 1;
        return new PdfMatrix(sx, 0, 0, sy, rect.Left - minX * sx, rect.Bottom - minY * sy);

        double Num(PdfObject o) => ((PdfNumber)doc.Resolve(o)!).ToDouble();
    }

    /// <summary>Flattens the annotations <paramref name="wanted"/> selects, in drawing order, and rebuilds /Annots without them.</summary>
    private int Flatten(Func<PdfObject, PdfDictionary, bool> wanted)
    {
        PdfArray? annots = Array();
        if (annots is null || annots.Count == 0) return 0;
        var removed = new HashSet<PdfObject>();
        var removedNumbers = new HashSet<int>();
        var widgets = new HashSet<int>();
        PdfGraphics? g = null;
        int count = 0;
        try
        {
            foreach (PdfObject entry in annots)
            {
                if (Doc.Resolve(entry) is not PdfDictionary annotation || !wanted(entry, annotation)) continue;
                string subtype = annotation.GetName("Subtype") ?? "";
                long flags = Doc.Resolve(annotation["F"]) is PdfNumber { IsInteger: true } f ? f.IntegerValue : 0;
                if (subtype == "Popup" || (flags & (FlagHidden | FlagNoView)) != 0) continue;
                if (NormalAppearance(Doc, annotation) is null)
                {
                    if (subtype != "Link") continue;
                }
                else
                {
                    g ??= _page.CreateGraphics();
                    if (!DrawAppearance(g, _page, annotation)) continue;
                }

                count++;
                removed.Add(entry);
                if (entry is PdfReference r)
                {
                    removedNumbers.Add(r.Number);
                    if (subtype == "Widget") widgets.Add(r.Number);
                }

                if (annotation["Popup"] is PdfReference popup) removedNumbers.Add(popup.Number);
            }
        }
        finally
        {
            g?.Dispose();
        }

        if (count == 0) return 0;

        // Pop ups go with the annotation they belong to, whichever side records the link.
        var kept = new PdfArray();
        foreach (PdfObject entry in annots)
        {
            if (removed.Contains(entry) || (entry is PdfReference r && removedNumbers.Contains(r.Number))) continue;
            if (Doc.Resolve(entry) is PdfDictionary d && d.GetName("Subtype") == "Popup" && d["Parent"] is PdfReference parent && removedNumbers.Contains(parent.Number)) continue;
            kept.Add(entry);
        }

        if (kept.Count == 0) _page.Dictionary.Remove("Annots");
        else _page.Dictionary.Set("Annots", kept);
        if (widgets.Count > 0) Doc.Form.RemoveWidgets(widgets);
        return count;
    }
}
