using PdfNetStack.Html.Dom;
using PdfNetStack.Objects;

namespace PdfNetStack.Html.Layout;

/// <summary>
/// Builds the structure tree of a tagged PDF (ISO 32000-1, 14.7 and 14.8) from the elements the
/// painter draws: each semantic element becomes a structure element with its standard type, and
/// each piece of content drawn for it a marked content sequence it owns. Elements are created as
/// their content is drawn, so a paragraph's text and the links inside it keep their reading order.
/// </summary>
internal sealed class Tagging
{
    private readonly PdfDocument _doc;
    private readonly PdfDictionary _root;
    private readonly PdfReference _rootReference;
    private readonly PdfDictionary _document;
    private readonly PdfReference _documentReference;
    private readonly Dictionary<HtmlElement, (PdfDictionary Element, PdfReference Reference)> _elements = [];
    private readonly Dictionary<int, (int Key, PdfArray Content)> _pages = [];
    private readonly SortedDictionary<int, PdfObject> _parentTree = [];
    private int _nextKey;

    public Tagging(PdfDocument doc, string? language)
    {
        _doc = doc;
        _root = new PdfDictionary { ["Type"] = new PdfName("StructTreeRoot") };
        _rootReference = doc.AddObject(_root);
        _document = new PdfDictionary
        {
            ["Type"] = new PdfName("StructElem"),
            ["S"] = new PdfName("Document"),
            ["P"] = _rootReference,
            ["K"] = new PdfArray(),
        };
        if (!string.IsNullOrEmpty(language)) _document.Set("Lang", PdfString.FromText(language!));
        _documentReference = doc.AddObject(_document);
        _root.Set("K", PdfArray.Of(_documentReference));
    }

    /// <summary>The standard structure type of an element (ISO 32000-1, 14.8.4), or null for one that only groups.</summary>
    public static string? Type(HtmlElement e) => e.Name switch
    {
        "h1" => "H1",
        "h2" => "H2",
        "h3" => "H3",
        "h4" => "H4",
        "h5" => "H5",
        "h6" => "H6",
        "p" or "pre" or "address" => "P",
        "ul" or "ol" or "dl" => "L",
        "li" => "LI",
        "dt" => "Lbl",
        "dd" => "LBody",
        "table" => "Table",
        "thead" => "THead",
        "tbody" => "TBody",
        "tfoot" => "TFoot",
        "tr" => "TR",
        "th" => "TH",
        "td" => "TD",
        "caption" or "figcaption" => "Caption",
        "img" or "svg" or "figure" => "Figure",
        "blockquote" => "BlockQuote",
        "q" => "Quote",
        "code" => "Code",
        "a" when e.GetAttribute("href") is not null => "Link",
        "article" => "Art",
        "section" or "main" or "nav" or "aside" or "header" or "footer" => "Sect",
        "toc" => "TOC",
        _ => null,
    };

    /// <summary>The structure type content of <paramref name="owner"/> is tagged with: its nearest tagged element's.</summary>
    public string TagOf(HtmlElement? owner) => Element(owner) is var (element, _) ? ((PdfName)element["S"]!).Value : "Document";

    /// <summary>
    /// A new marked content sequence on a page for the element that owns some content: its MCID,
    /// recorded in the element's kids and in the page's part of the parent tree.
    /// </summary>
    public int Content(PdfPage page, HtmlElement? owner)
    {
        (PdfDictionary element, PdfReference reference) = Element(owner) ?? (_document, _documentReference);
        if (!_pages.TryGetValue(page.Number, out (int Key, PdfArray Content) entry))
        {
            entry = (_nextKey++, new PdfArray());
            _pages[page.Number] = entry;
            page.Dictionary.Set("StructParents", PdfNumber.FromInteger(entry.Key));
            _parentTree[entry.Key] = entry.Content;
        }

        int mcid = entry.Content.Count;
        entry.Content.Add(reference);
        var pageReference = new PdfReference(page.Number, 0);

        // An element is placed on the page its first content is on; content elsewhere names its page.
        if (element["Pg"] is null) element.Set("Pg", pageReference);
        PdfObject kid = element["Pg"] is PdfReference pg && pg.Number == page.Number
            ? PdfNumber.FromInteger(mcid)
            : new PdfDictionary { ["Type"] = new PdfName("MCR"), ["Pg"] = pageReference, ["MCID"] = PdfNumber.FromInteger(mcid) };
        Kids(element).Add(kid);
        return mcid;
    }

    /// <summary>Ties a link annotation to its Link element (ISO 32000-1, 14.7.4.3), with the link text as its contents.</summary>
    public void Annotation(PdfAnnotation annotation, PdfPage page, HtmlElement link)
    {
        if (Element(link) is not var (element, reference) || annotation.Entry is not PdfReference annotationReference) return;
        int key = _nextKey++;
        annotation.Dictionary.Set("StructParent", PdfNumber.FromInteger(key));
        _parentTree[key] = reference;
        string text = string.Join(" ", link.TextContent.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 0 && annotation.Dictionary["Contents"] is null) annotation.Dictionary.Set("Contents", PdfString.FromText(text));
        Kids(element).Add(new PdfDictionary
        {
            ["Type"] = new PdfName("OBJR"),
            ["Obj"] = annotationReference,
            ["Pg"] = new PdfReference(page.Number, 0),
        });
    }

    /// <summary>Writes the parent tree and marks the document as tagged.</summary>
    public void Finish()
    {
        var nums = new PdfArray();
        foreach (KeyValuePair<int, PdfObject> entry in _parentTree)
        {
            nums.Add(PdfNumber.FromInteger(entry.Key));
            nums.Add(entry.Value is PdfArray array ? _doc.AddObject(array) : entry.Value);
        }

        _root.Set("ParentTree", _doc.AddObject(new PdfDictionary { ["Nums"] = nums }));
        _root.Set("ParentTreeNextKey", PdfNumber.FromInteger(_nextKey));
        _doc.Catalog.Set("StructTreeRoot", _rootReference);
        _doc.Catalog.Set("MarkInfo", new PdfDictionary { ["Marked"] = PdfBoolean.True });

        // Viewers title the window with the document title rather than the file name (PDF/UA, 7.1).
        PdfDictionary preferences = _doc.Resolve(_doc.Catalog["ViewerPreferences"]) as PdfDictionary ?? new PdfDictionary();
        preferences.Set("DisplayDocTitle", PdfBoolean.True);
        _doc.Catalog.Set("ViewerPreferences", preferences);
    }

    /// <summary>The structure element of the nearest tagged element at or above <paramref name="e"/>, created with its ancestors on first use.</summary>
    private (PdfDictionary Element, PdfReference Reference)? Element(HtmlElement? e)
    {
        for (HtmlElement? at = e; at is not null; at = at.ParentElement)
        {
            if (_elements.TryGetValue(at, out var found)) return found;
            if (Type(at) is not string type) continue;

            (PdfDictionary parent, PdfReference parentReference) = Element(at.ParentElement) ?? (_document, _documentReference);
            var element = new PdfDictionary
            {
                ["Type"] = new PdfName("StructElem"),
                ["S"] = new PdfName(type),
                ["P"] = parentReference,
                ["K"] = new PdfArray(),
            };
            if (type == "Figure") element.Set("Alt", PdfString.FromText(at.GetAttribute("alt") ?? at.GetAttribute("aria-label") ?? at.GetAttribute("title") ?? ""));
            if (at.GetAttribute("lang") is { Length: > 0 } lang) element.Set("Lang", PdfString.FromText(lang));
            if (type is "TH" or "TD")
            {
                int span = int.TryParse(at.GetAttribute("colspan"), out int c) && c > 1 ? c : 1, rows = int.TryParse(at.GetAttribute("rowspan"), out int r) && r > 1 ? r : 1;
                var attributes = new PdfDictionary { ["O"] = new PdfName("Table") };
                if (span > 1) attributes.Set("ColSpan", PdfNumber.FromInteger(span));
                if (rows > 1) attributes.Set("RowSpan", PdfNumber.FromInteger(rows));
                if (type == "TH") attributes.Set("Scope", new PdfName(at.GetAttribute("scope")?.ToLowerInvariant() == "row" ? "Row" : "Column"));
                if (attributes.Count > 1) element.Set("A", attributes);
            }

            PdfReference reference = _doc.AddObject(element);
            Kids(parent).Add(reference);
            _elements[at] = (element, reference);
            return (element, reference);
        }

        return null;
    }

    private static PdfArray Kids(PdfDictionary element) => (PdfArray)element["K"]!;
}
