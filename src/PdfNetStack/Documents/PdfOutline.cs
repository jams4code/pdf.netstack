using System.Collections.ObjectModel;
using PdfNetStack.Documents;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>A bookmark. Children nest under it.</summary>
public sealed class PdfOutline
{
    /// <summary>Creates a bookmark that opens <paramref name="destination"/>.</summary>
    public PdfOutline(string title, PdfPage? destination = null)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Destination = destination;
        DestinationChanged = true;
    }

    internal PdfOutline(string title, PdfPage? destination, PdfDictionary original, bool isOpen)
    {
        Title = title;
        _destination = destination;
        Original = original;
        IsOpen = isOpen;
    }

    private PdfPage? _destination;

    /// <summary>Text shown in the bookmark panel.</summary>
    public string Title { get; set; }

    /// <summary>
    /// Page the bookmark opens, or null when it has no page destination (for example a web link,
    /// or a page that is no longer in the document).
    /// </summary>
    public PdfPage? Destination
    {
        get => _destination;
        set
        {
            _destination = value;
            DestinationChanged = true;
        }
    }

    /// <summary>Whether the children are expanded when the document opens.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Nested bookmarks.</summary>
    public Collection<PdfOutline> Children { get; } = new();

    /// <summary>Adds a child bookmark and returns it.</summary>
    public PdfOutline Add(string title, PdfPage? destination = null)
    {
        var child = new PdfOutline(title, destination);
        Children.Add(child);
        return child;
    }

    // The source item, kept so styling, colors and non-page actions survive a rewrite.
    internal PdfDictionary? Original { get; }

    internal bool DestinationChanged { get; private set; }
}

/// <summary>The top level bookmarks of a document.</summary>
public sealed class PdfOutlineCollection : Collection<PdfOutline>
{
    private const int MaxItems = 100_000;

    /// <summary>Adds a top level bookmark and returns it.</summary>
    public PdfOutline Add(string title, PdfPage? destination = null)
    {
        var item = new PdfOutline(title, destination);
        Add(item);
        return item;
    }

    internal static PdfOutlineCollection Load(PdfDocument doc)
    {
        var result = new PdfOutlineCollection();
        if (doc.ResolveDictionary(doc.Catalog["Outlines"]) is not { } root) return result;
        var pageByNumber = doc.Pages.ToDictionary(p => p.Number);
        var names = new NamedDestinations(doc);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        int budget = MaxItems;

        void LoadLevel(PdfDictionary parent, Collection<PdfOutline> into, int depth)
        {
            PdfDictionary? item = doc.ResolveDictionary(parent["First"]);
            while (item is not null && depth < 64 && budget-- > 0 && seen.Add(item))
            {
                string title = PdfText.GetString(item, "Title", doc.Resolve) ?? "";
                bool open = doc.Resolve(item["Count"]) is PdfNumber { IsInteger: true, IntegerValue: > 0 };
                var outline = new PdfOutline(title, FindPage(doc, item, pageByNumber, names), item, open);
                into.Add(outline);
                LoadLevel(item, outline.Children, depth + 1);
                item = doc.ResolveDictionary(item["Next"]);
            }
        }

        LoadLevel(root, result, 0);
        return result;
    }

    private static PdfPage? FindPage(PdfDocument doc, PdfDictionary item, Dictionary<int, PdfPage> pages, NamedDestinations names)
    {
        PdfObject? dest = item["Dest"];
        if (dest is null && doc.ResolveDictionary(item["A"]) is { } action && action.GetName("S") == "GoTo") dest = action["D"];
        dest = doc.Resolve(dest);
        if (dest is PdfName or PdfString) dest = names.Lookup(dest);
        if (dest is PdfDictionary withD) dest = doc.Resolve(withD["D"]);
        return dest is PdfArray { Count: > 0 } array && array[0] is PdfReference page && pages.TryGetValue(page.Number, out PdfPage? found)
            ? found
            : null;
    }

    /// <summary>
    /// Named destinations, indexed once per load. The name tree walk is capped, so a hostile tree
    /// costs a bounded amount of work no matter how many bookmarks refer into it.
    /// </summary>
    private sealed class NamedDestinations(PdfDocument doc)
    {
        private const int MaxEntries = 200_000;
        private Dictionary<string, PdfObject>? _byString;

        public PdfObject? Lookup(PdfObject name)
        {
            if (name is PdfName n) return doc.ResolveDictionary(doc.Catalog["Dests"]) is { } dests ? doc.Resolve(dests[n.Value]) : null;
            if (name is not PdfString s) return null;
            _byString ??= Build();
            return _byString.TryGetValue(Compat.Latin1.GetString(s.Value), out PdfObject? found) ? found : null;
        }

        private Dictionary<string, PdfObject> Build()
        {
            var map = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(PdfDictionary Node, int Depth)>();
            if (doc.ResolveDictionary(doc.ResolveDictionary(doc.Catalog["Names"])?["Dests"]) is { } root) stack.Push((root, 0));
            int budget = MaxEntries;
            while (stack.Count > 0 && budget > 0)
            {
                (PdfDictionary node, int depth) = stack.Pop();
                if (depth > 32 || !visited.Add(node)) continue;
                budget--;
                if (doc.Resolve(node["Names"]) is PdfArray names)
                {
                    for (int i = 0; i + 1 < names.Count && budget > 0; i += 2, budget--)
                    {
                        if (doc.Resolve(names[i]) is PdfString key) map.TryAdd(Compat.Latin1.GetString(key.Value), doc.Resolve(names[i + 1])!);
                    }
                }

                if (doc.Resolve(node["Kids"]) is PdfArray kids)
                {
                    foreach (PdfObject kid in kids.Items)
                    {
                        if (doc.ResolveDictionary(kid) is { } child) stack.Push((child, depth + 1));
                    }
                }
            }

            return map;
        }
    }

    /// <summary>Writes the tree into the document, replacing the catalog's /Outlines.</summary>
    internal void WriteTo(PdfDocument doc)
    {
        if (Count == 0)
        {
            doc.Catalog.Remove("Outlines");
            return;
        }

        var root = new PdfDictionary();
        root.Set("Type", new PdfName("Outlines"));
        int rootNumber = doc.Add(root);
        int visible = WriteLevel(doc, this, root, rootNumber, 0);
        root.Set("Count", PdfNumber.FromInteger(visible));
        doc.Catalog.Set("Outlines", new PdfReference(rootNumber, 0));
    }

    /// <returns>Number of visible descendants, as the parent's /Count requires.</returns>
    private static int WriteLevel(PdfDocument doc, IList<PdfOutline> items, PdfDictionary parent, int parentNumber, int depth)
    {
        if (items.Count == 0 || depth > 64) return 0;
        var numbers = new int[items.Count];
        var dicts = new PdfDictionary[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            dicts[i] = items[i].Original is { } original ? PdfDocument.Clone(original) : new PdfDictionary();
            numbers[i] = doc.Add(dicts[i]);
        }

        int visible = 0;
        for (int i = 0; i < items.Count; i++)
        {
            PdfOutline item = items[i];
            PdfDictionary d = dicts[i];
            foreach (string key in new[] { "Parent", "First", "Last", "Next", "Prev", "Count" }) d.Remove(key);
            d.Set("Title", PdfText.Encode(item.Title));
            d.Set("Parent", new PdfReference(parentNumber, 0));
            if (i > 0) d.Set("Prev", new PdfReference(numbers[i - 1], 0));
            if (i + 1 < items.Count) d.Set("Next", new PdfReference(numbers[i + 1], 0));

            if (item.DestinationChanged || item.Original is null)
            {
                d.Remove("A");
                d.Remove("SD");
                if (item.Destination is { } page && page.Owner == doc && page.Index >= 0)
                {
                    d.Set("Dest", new PdfArray([new PdfReference(page.Number, 0), new PdfName("Fit")]));
                }
                else
                {
                    d.Remove("Dest");
                }
            }

            int descendants = WriteLevel(doc, item.Children, d, numbers[i], depth + 1);
            if (item.Children.Count > 0)
            {
                d.Set("Count", PdfNumber.FromInteger(item.IsOpen ? descendants : -descendants));
            }

            visible += 1 + (item.IsOpen ? descendants : 0);
        }

        parent.Set("First", new PdfReference(numbers[0], 0));
        parent.Set("Last", new PdfReference(numbers[^1], 0));
        return visible;
    }
}
