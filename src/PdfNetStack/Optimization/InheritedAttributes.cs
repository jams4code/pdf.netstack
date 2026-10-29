using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// Moves inheritable page attributes (ISO 32000-1, 7.7.3.4) from intermediate page tree nodes
/// onto every page that inherits them, so no page depends on its ancestors. A linearized file
/// needs this: its hint tables describe each page by the objects the page itself refers to.
/// Every page sees exactly the values it saw before; a value that was an indirect object stays
/// one shared object.
/// </summary>
internal static class InheritedAttributes
{
    /// <summary>Page attributes a page takes from its ancestors when it lacks them.</summary>
    public static readonly string[] Keys = ["Resources", "MediaBox", "CropBox", "Rotate"];

    private const int MaxDepth = 64;

    /// <summary>Pushes the attributes down. Null on success, otherwise why the page tree cannot be laid out per page; nothing is changed then.</summary>
    public static string? PushToPages(PdfFile file)
    {
        PdfDictionary? catalog = file.ResolveDictionary(file.Trailer["Root"]);
        if (catalog?["Pages"] is not PdfReference rootRef || file.GetObject(rootRef.Number) is not PdfDictionary root)
        {
            return "the catalog has no page tree";
        }

        var pages = new List<(PdfDictionary Page, Dictionary<string, PdfObject> Inherited)>();
        var nodes = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>();
        string? problem = Walk(file, root, new Dictionary<string, PdfObject>(StringComparer.Ordinal), 0, pages, nodes, seen);
        if (problem is not null) return problem;
        if (pages.Count == 0) return "the document has no pages";

        foreach ((PdfDictionary page, Dictionary<string, PdfObject> inherited) in pages)
        {
            foreach ((string key, PdfObject value) in inherited)
            {
                if (page[key] is null) page.Set(key, value);
            }
        }

        foreach (PdfDictionary node in nodes)
        {
            foreach (string key in Keys) node.Remove(key);
        }

        return null;
    }

    private static string? Walk(
        PdfFile file,
        PdfDictionary node,
        Dictionary<string, PdfObject> inherited,
        int depth,
        List<(PdfDictionary, Dictionary<string, PdfObject>)> pages,
        List<PdfDictionary> nodes,
        HashSet<PdfDictionary> seen)
    {
        if (depth > MaxDepth) return "the page tree is too deep";
        if (!seen.Add(node)) return "an object appears twice in the page tree";
        if (node.GetName("Type") != "Pages") return "a page tree node is not marked /Type /Pages";
        if (file.Resolve(node["Kids"]) is not PdfArray kids) return "a page tree node has no /Kids";
        nodes.Add(node);

        var next = new Dictionary<string, PdfObject>(inherited, StringComparer.Ordinal);
        foreach (string key in Keys)
        {
            if (node[key] is { } value && value is not PdfNull) next[key] = value;
        }

        foreach (PdfObject kid in kids)
        {
            if (kid is not PdfReference r || file.GetObject(r.Number) is not PdfDictionary child) return "a page tree entry is not a dictionary object";
            if (child.GetName("Type") == "Page")
            {
                if (!seen.Add(child)) return "a page appears twice in the page tree";
                pages.Add((child, next));
                continue;
            }

            string? problem = Walk(file, child, next, depth + 1, pages, nodes, seen);
            if (problem is not null) return problem;
        }

        return null;
    }
}
