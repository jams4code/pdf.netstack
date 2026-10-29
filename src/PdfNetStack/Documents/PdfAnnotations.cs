using PdfNetStack.Drawing;
using PdfNetStack.Fonts;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>Text markup annotations: highlight, underline, strikeout and squiggly underline.</summary>
public enum PdfTextMarkup
{
    /// <summary>A translucent marker over the text.</summary>
    Highlight,

    /// <summary>A line under the text.</summary>
    Underline,

    /// <summary>A line through the text.</summary>
    StrikeOut,

    /// <summary>A wavy line under the text.</summary>
    Squiggly,
}

/// <summary>
/// An annotation on a page (ISO 32000-1, 12.5): a link, a note, a text markup, free text or any
/// other kind a file contains. <see cref="Dictionary"/> gives access to everything else.
/// </summary>
public sealed class PdfAnnotation
{
    private readonly PdfPage _page;

    internal PdfAnnotation(PdfPage page, PdfDictionary dictionary, PdfObject entry)
    {
        _page = page;
        Dictionary = dictionary;
        Entry = entry;
    }

    /// <summary>The annotation dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The kind, such as "Link", "Text", "Highlight" or "Widget".</summary>
    public string Subtype => Dictionary.GetName("Subtype") ?? "";

    /// <summary>The annotation rectangle in PDF user space (origin bottom left, y up).</summary>
    public PdfRectangle Rectangle => PdfRectangle.From(Dictionary["Rect"], _page.Owner.Resolve) ?? default;

    /// <summary>The text of a note or comment, or the description of other annotations.</summary>
    public string? Contents
    {
        get => _page.Owner.Resolve(Dictionary["Contents"]) is PdfString s ? s.ToText() : null;
        set
        {
            if (value is null) Dictionary.Remove("Contents");
            else Dictionary.Set("Contents", PdfString.FromText(value));
        }
    }

    /// <summary>The web address a link opens, or null.</summary>
    public string? Uri =>
        _page.Owner.Resolve(Dictionary["A"]) is PdfDictionary action && action.GetName("S") == "URI"
            && _page.Owner.Resolve(action["URI"]) is PdfString uri ? uri.ToText() : null;

    /// <summary>The page a link goes to in the same document, or null.</summary>
    public PdfPage? Destination
    {
        get
        {
            PdfDocument doc = _page.Owner;
            PdfObject? dest = doc.Resolve(Dictionary["Dest"]);
            if (dest is null && doc.Resolve(Dictionary["A"]) is PdfDictionary action && action.GetName("S") == "GoTo") dest = doc.Resolve(action["D"]);
            return PdfAnnotationCollection.ResolveDestination(doc, dest);
        }
    }

    internal PdfObject Entry { get; }
}

/// <summary>
/// The annotations of a page. Positions use the same coordinates as <see cref="PdfGraphics"/>:
/// points from the top left corner of the media box, y down. New annotations come with an
/// appearance stream, so every viewer shows them the same way.
/// </summary>
public sealed class PdfAnnotationCollection : IReadOnlyList<PdfAnnotation>
{
    private const int MaxNameTreeDepth = 32;
    private const int MaxNameTreeNodes = 10_000;
    private readonly PdfPage _page;

    internal PdfAnnotationCollection(PdfPage page) => _page = page;

    private PdfDocument Doc => _page.Owner;

    /// <summary>Number of annotations.</summary>
    public int Count => Array()?.Count ?? 0;

    /// <summary>The annotation at <paramref name="index"/>.</summary>
    public PdfAnnotation this[int index]
    {
        get
        {
            PdfArray array = Array() ?? throw new ArgumentOutOfRangeException(nameof(index));
            PdfObject entry = array[index];
            return new PdfAnnotation(_page, Doc.Resolve(entry) as PdfDictionary ?? new PdfDictionary(), entry);
        }
    }

    /// <summary>A link opening a web address.</summary>
    public PdfAnnotation AddLink(double x, double y, double width, double height, Uri uri)
    {
        Compat.ThrowIfNull(uri, nameof(uri));
        if (!uri.IsAbsoluteUri) throw new ArgumentException("A link needs an absolute address.", nameof(uri));
        PdfDictionary link = Link(x, y, width, height);
        link.Set("A", new PdfDictionary
        {
            ["S"] = new PdfName("URI"),
            ["URI"] = new PdfString(System.Text.Encoding.ASCII.GetBytes(uri.AbsoluteUri).AsSpan()),
        });
        return Add(link);
    }

    /// <summary>A link to a page of this document, showing it from <paramref name="top"/> (points from its top), or whole when null.</summary>
    public PdfAnnotation AddLink(double x, double y, double width, double height, PdfPage target, double? top = null)
    {
        Compat.ThrowIfNull(target, nameof(target));
        if (!ReferenceEquals(target.Owner, Doc)) throw new ArgumentException("The target page belongs to another document.", nameof(target));
        PdfDictionary link = Link(x, y, width, height);
        var pageRef = new PdfReference(target.Number, 0);
        link.Set("Dest", top is double t
            ? PdfArray.Of(pageRef, new PdfName("XYZ"), PdfNull.Instance, PdfNumber.FromDouble(target.MediaBox.Top - t), PdfNull.Instance)
            : PdfArray.Of(pageRef, new PdfName("Fit")));
        return Add(link);
    }

    /// <summary>A sticky note: an icon at (x, y) that opens <paramref name="text"/>.</summary>
    public PdfAnnotation AddNote(double x, double y, string text, string? author = null, PdfColor? color = null)
    {
        Compat.ThrowIfNull(text, nameof(text));
        PdfColor c = color ?? PdfColor.FromHex("#FACC15");
        PdfDictionary note = Base("Text", x, y, 20, 20, c);
        note.Set("Name", new PdfName("Comment"));
        note.Set("Contents", PdfString.FromText(text));
        if (author is not null) note.Set("T", PdfString.FromText(author));
        Appearance(note, 20, 20, g =>
        {
            g.DrawRoundedRectangle(new PdfPen(PdfColor.Gray(0.25), 0.8), c, 1, 1, 18, 15, 2);
            g.DrawPolygon(new PdfPen(PdfColor.Gray(0.25), 0.8), c, [new PdfPoint(5, 16), new PdfPoint(5, 19.5), new PdfPoint(9, 16)]);
            for (int line = 0; line < 3; line++) g.DrawLine(new PdfPen(PdfColor.Gray(0.25), 0.8), 4, 5 + 3.5 * line, 16, 5 + 3.5 * line);
        });
        return Add(note);
    }

    /// <summary>Marks up a text area: highlight (yellow by default), underline, strikeout or squiggly.</summary>
    public PdfAnnotation AddTextMarkup(PdfTextMarkup kind, double x, double y, double width, double height, PdfColor? color = null, string? comment = null)
    {
        PdfColor c = color ?? (kind == PdfTextMarkup.Highlight ? PdfColor.FromHex("#FDE047") : PdfColor.FromHex("#DC2626"));
        string subtype = kind == PdfTextMarkup.StrikeOut ? "StrikeOut" : kind.ToString();
        PdfDictionary markup = Base(subtype, x, y, width, height, c);
        PdfRectangle r = ToPdf(x, y, width, height);

        // Upper left, upper right, lower left, lower right: the order viewers expect.
        markup.Set("QuadPoints", PdfArray.Of(r.Left, r.Top, r.Right, r.Top, r.Left, r.Bottom, r.Right, r.Bottom));
        if (comment is not null) markup.Set("Contents", PdfString.FromText(comment));
        Appearance(markup, width, height, g =>
        {
            double t = Math.Max(0.5, height / 14);
            switch (kind)
            {
                case PdfTextMarkup.Highlight:
                    g.BlendMode = PdfBlendMode.Multiply;
                    g.DrawRectangle(null, c, 0, 0, width, height);
                    break;
                case PdfTextMarkup.Underline:
                    g.DrawRectangle(null, c, 0, height - 2 * t, width, t);
                    break;
                case PdfTextMarkup.StrikeOut:
                    g.DrawRectangle(null, c, 0, height * 0.55 - t / 2, width, t);
                    break;
                default:
                    var wave = new PdfPath().MoveTo(0, height - t);
                    for (double px = 0; px < width; px += 2 * t) wave.LineTo(Math.Min(width, px + t), height - 2.5 * t).LineTo(Math.Min(width, px + 2 * t), height - t);
                    g.DrawPath(new PdfPen(c, t * 0.8), null, wave);
                    break;
            }
        });
        return Add(markup);
    }

    /// <summary>Text written on the page as an annotation, in a box with an optional border and fill.</summary>
    public PdfAnnotation AddFreeText(double x, double y, double width, double height, string text, PdfTextStyle style, PdfColor? border = null, PdfColor? fill = null)
    {
        Compat.ThrowIfNull(text, nameof(text));
        Compat.ThrowIfNull(style, nameof(style));
        PdfDictionary free = Base("FreeText", x, y, width, height, null);
        free.Set("Contents", PdfString.FromText(text));
        free.Set("DA", PdfString.FromText($"/Helv {style.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)} Tf 0 g"));
        Appearance(free, width, height, g =>
        {
            if (border is not null || fill is not null) g.DrawRectangle(border is PdfColor b ? new PdfPen(b, 1) : null, fill, 0.5, 0.5, width - 1, height - 1);
            g.IntersectClip(2, 2, width - 4, height - 4);
            g.DrawText(text, style, 4, 3, Math.Max(1, width - 8));
        });
        return Add(free);
    }

    /// <summary>Removes an annotation from the page.</summary>
    public bool Remove(PdfAnnotation annotation)
    {
        Compat.ThrowIfNull(annotation, nameof(annotation));
        PdfArray? array = Array();
        if (array is null) return false;
        for (int i = 0; i < array.Count; i++)
        {
            if (Same(array[i], annotation.Entry))
            {
                PdfArray copy = Writable();
                copy.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<PdfAnnotation> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The page a destination names: an explicit array, or a name looked up in /Dests or the Dests name tree.</summary>
    internal static PdfPage? ResolveDestination(PdfDocument doc, PdfObject? dest)
    {
        dest = doc.Resolve(dest);
        if (dest is PdfName or PdfString)
        {
            string name = dest is PdfName n ? n.Value : ((PdfString)dest).ToText();
            dest = doc.Resolve(LookupName(doc, name));
            if (dest is PdfDictionary d) dest = doc.Resolve(d["D"]);
        }

        if (dest is not PdfArray { Count: > 0 } array || array[0] is not PdfReference page) return null;
        return doc.Pages.FirstOrDefault(p => p.Number == page.Number);
    }

    private static PdfObject? LookupName(PdfDocument doc, string name)
    {
        if (doc.Resolve(doc.Catalog["Dests"]) is PdfDictionary dests && dests[name] is PdfObject direct) return direct;
        PdfDictionary? names = doc.Resolve(doc.Catalog["Names"]) as PdfDictionary;
        if (names is null) return null;
        var visited = new HashSet<PdfDictionary>();
        return SearchNameTree(doc, doc.Resolve(names["Dests"]) as PdfDictionary, name, 0, visited);
    }

    /// <summary>
    /// Depth first through the name tree. Each node is visited once and the walk stops after
    /// <see cref="MaxNameTreeNodes"/> nodes: a crafted tree listing a kid many times at every
    /// level would otherwise take exponential time.
    /// </summary>
    private static PdfObject? SearchNameTree(PdfDocument doc, PdfDictionary? node, string name, int depth, HashSet<PdfDictionary> visited)
    {
        if (node is null || depth > MaxNameTreeDepth || visited.Count >= MaxNameTreeNodes || !visited.Add(node)) return null;
        if (doc.Resolve(node["Names"]) is PdfArray leaf)
        {
            for (int i = 0; i + 1 < leaf.Count; i += 2)
            {
                if (doc.Resolve(leaf[i]) is PdfString key && key.ToText() == name) return leaf[i + 1];
            }
        }

        if (doc.Resolve(node["Kids"]) is PdfArray kids)
        {
            foreach (PdfObject kid in kids)
            {
                if (SearchNameTree(doc, doc.Resolve(kid) as PdfDictionary, name, depth + 1, visited) is PdfObject found) return found;
            }
        }

        return null;
    }

    private PdfRectangle ToPdf(double x, double y, double width, double height)
    {
        if (!(width > 0) || !(height > 0) || double.IsInfinity(width) || double.IsInfinity(height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "An annotation needs a positive size.");
        }

        PdfRectangle box = _page.MediaBox;
        return new PdfRectangle(box.Left + x, box.Top - y - height, box.Left + x + width, box.Top - y);
    }

    private PdfDictionary Base(string subtype, double x, double y, double width, double height, PdfColor? color)
    {
        PdfRectangle r = ToPdf(x, y, width, height);
        var d = new PdfDictionary
        {
            ["Type"] = new PdfName("Annot"),
            ["Subtype"] = new PdfName(subtype),
            ["Rect"] = r.ToArray(),
            ["P"] = new PdfReference(_page.Number, 0),
            ["F"] = PdfNumber.FromInteger(4), // print
            ["M"] = PdfString.FromText("D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z"),
        };
        if (color is PdfColor c) d.Set("C", PdfArray.Of(c.ToRgb().Components()));
        return d;
    }

    private PdfDictionary Link(double x, double y, double width, double height)
    {
        PdfDictionary link = Base("Link", x, y, width, height, null);
        link.Remove("F");
        link.Set("Border", PdfArray.Of(0, 0, 0));
        return link;
    }

    private void Appearance(PdfDictionary annotation, double width, double height, Action<PdfGraphics> draw)
    {
        PdfForm form = PdfForm.Create(Doc, width, height);
        using (PdfGraphics g = form.CreateGraphics()) draw(g);
        annotation.Set("AP", new PdfDictionary { ["N"] = form.Reference });
    }

    private PdfAnnotation Add(PdfDictionary annotation)
    {
        PdfReference reference = Doc.AddObject(annotation);
        Writable().Add(reference);
        return new PdfAnnotation(_page, annotation, reference);
    }

    private PdfArray? Array() => Doc.Resolve(_page.Dictionary["Annots"]) as PdfArray;

    /// <summary>The page's own /Annots array: a shared or indirect array is copied first.</summary>
    private PdfArray Writable()
    {
        var copy = new PdfArray();
        if (Array() is PdfArray existing)
        {
            foreach (PdfObject o in existing) copy.Add(o);
        }

        _page.Dictionary.Set("Annots", copy);
        return copy;
    }

    private static bool Same(PdfObject a, PdfObject b) =>
        ReferenceEquals(a, b) || (a is PdfReference ra && b is PdfReference rb && ra.Number == rb.Number);
}
