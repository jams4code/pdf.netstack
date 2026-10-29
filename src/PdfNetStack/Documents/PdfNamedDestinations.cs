using PdfNetStack.Documents;
using PdfNetStack.Objects;

namespace PdfNetStack;

/// <summary>
/// The named destinations of a document (ISO 32000-1, 12.3.2.3): the /Dests name tree under the
/// catalog's /Names, and the older /Dests dictionary of the catalog. Bookmarks and links that
/// point at a name follow it to a page, so renaming one renames every reference with it.
/// </summary>
/// <example>
/// <code>
/// PdfDocument doc = PdfDocument.Open("manual.pdf");
/// foreach (string name in doc.NamedDestinations.Names) Console.WriteLine($"{name}: page {doc.NamedDestinations.GetPage(name)?.Index}");
/// doc.NamedDestinations.Rename("_OPENTOPIC_TOC_PROCESSING_d55e123", "GUID-7F3A");
/// doc.Save("manual-renamed.pdf");
/// </code>
/// </example>
public sealed class PdfNamedDestinations
{
    private const int MaxEntries = 500_000;
    private const int MaxDepth = 32;
    private const int MaxObjects = 5_000_000;

    private readonly PdfDocument _doc;

    internal PdfNamedDestinations(PdfDocument doc) => _doc = doc;

    /// <summary>Every name, the name tree's in tree order, then the catalog dictionary's.</summary>
    public IReadOnlyList<string> Names => [.. Entries().Select(e => e.Name).Distinct(StringComparer.Ordinal)];

    /// <summary>True when the document defines the name.</summary>
    /// <param name="name">The destination name.</param>
    public bool Contains(string name) => Find(name) is not null;

    /// <summary>The page a name leads to, or null when the name is unknown or leads nowhere.</summary>
    /// <param name="name">The destination name.</param>
    public PdfPage? GetPage(string name)
    {
        PdfObject? target = _doc.Resolve(Find(name));
        if (target is PdfDictionary withD) target = _doc.Resolve(withD["D"]);
        return target is PdfArray { Count: > 0 } array && array[0] is PdfReference page ? _doc.Pages.FirstOrDefault(p => p.Number == page.Number) : null;
    }

    /// <summary>Renames one destination and every bookmark, link and action that refers to it.</summary>
    /// <param name="oldName">The current name.</param>
    /// <param name="newName">The new name.</param>
    /// <returns>The number of references changed, the definition included.</returns>
    /// <exception cref="ArgumentException">A name is empty.</exception>
    /// <exception cref="InvalidOperationException">The new name already names another destination.</exception>
    public int Rename(string oldName, string newName) => Rename(new Dictionary<string, string>(StringComparer.Ordinal) { [oldName ?? throw new ArgumentNullException(nameof(oldName))] = newName ?? throw new ArgumentNullException(nameof(newName)) });

    /// <summary>
    /// Renames destinations, old name to new name, and every bookmark (/Dest and /A), link
    /// annotation and GoTo action (the open action and additional actions too) that refers to them.
    /// References keep their form, string or name. The /Dests name tree is then rewritten as one
    /// sorted leaf, which every reader accepts and which cannot hold a key outside its /Limits.
    /// </summary>
    /// <param name="renames">Old name to new name.</param>
    /// <returns>The number of references changed, the definitions included.</returns>
    /// <exception cref="ArgumentException">A name is empty.</exception>
    /// <exception cref="InvalidOperationException">A new name already names another destination, or two old names get the same new one.</exception>
    public int Rename(IReadOnlyDictionary<string, string> renames)
    {
        Compat.ThrowIfNull(renames, nameof(renames));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> r in renames)
        {
            if (string.IsNullOrEmpty(r.Key) || string.IsNullOrEmpty(r.Value)) throw new ArgumentException("Destination names must not be empty.", nameof(renames));
            if (r.Key != r.Value) map[r.Key] = r.Value;
        }

        if (map.Count == 0) return 0;
        List<Entry> entries = Entries();
        var after = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Entry e in entries)
        {
            string name = map.TryGetValue(e.Name, out string? renamed) ? renamed : e.Name;
            string tree = e.Legacy ? "catalog" : "tree";
            if (after.TryGetValue(tree + "\n" + name, out string? from) && from != e.Name)
            {
                throw new InvalidOperationException($"Renaming would give two destinations the name '{name}': '{from}' and '{e.Name}'.");
            }

            after[tree + "\n" + name] = e.Name;
        }

        int changed = 0;
        changed += RewriteTree(entries.Where(e => !e.Legacy).ToList(), map);
        changed += RewriteLegacy(map);
        changed += RewriteReferences(map);
        return changed;
    }

    private sealed record Entry(string Name, PdfObject Key, PdfObject Value, bool Legacy);

    private PdfObject? Find(string name)
    {
        Compat.ThrowIfNull(name, nameof(name));
        return Entries().FirstOrDefault(e => e.Name == name)?.Value;
    }

    /// <summary>Every definition: name tree leaves in order (bounded and cycle safe), then the catalog's /Dests.</summary>
    private List<Entry> Entries()
    {
        var entries = new List<Entry>();
        if (Tree() is PdfDictionary root)
        {
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            void Walk(PdfDictionary node, int depth)
            {
                if (depth > MaxDepth || entries.Count >= MaxEntries || !visited.Add(node)) return;
                if (_doc.Resolve(node["Names"]) is PdfArray names)
                {
                    for (int i = 0; i + 1 < names.Count && entries.Count < MaxEntries; i += 2)
                    {
                        if (_doc.Resolve(names[i]) is PdfString key) entries.Add(new Entry(key.ToText(), names[i], names[i + 1], false));
                    }
                }

                if (_doc.Resolve(node["Kids"]) is PdfArray kids)
                {
                    foreach (PdfObject kid in kids)
                    {
                        if (_doc.ResolveDictionary(kid) is PdfDictionary child) Walk(child, depth + 1);
                    }
                }
            }

            Walk(root, 0);
        }

        if (_doc.ResolveDictionary(_doc.Catalog["Dests"]) is PdfDictionary legacy)
        {
            foreach (KeyValuePair<string, PdfObject> e in legacy) entries.Add(new Entry(e.Key, new PdfName(e.Key), e.Value, true));
        }

        return entries;
    }

    private PdfDictionary? Tree() => _doc.ResolveDictionary(_doc.ResolveDictionary(_doc.Catalog["Names"])?["Dests"]);

    /// <summary>The name tree as one leaf, keys renamed, sorted by their bytes as the specification orders them (7.9.6).</summary>
    private int RewriteTree(List<Entry> entries, Dictionary<string, string> map)
    {
        if (Tree() is not PdfDictionary root || entries.Count == 0) return 0;
        int changed = 0;
        var leaf = new List<(byte[] Bytes, PdfObject Key, PdfObject Value)>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Entry e in entries)
        {
            PdfObject key = e.Key;
            string name = e.Name;
            if (map.TryGetValue(name, out string? renamed))
            {
                key = PdfString.FromText(renamed);
                name = renamed;
                changed++;
            }

            // A name defined twice keeps its first definition, as readers resolve it.
            if (!seen.Add(name)) continue;
            leaf.Add((((PdfString)_doc.Resolve(key)!).Value, key, e.Value));
        }

        leaf.Sort((a, b) => Compare(a.Bytes, b.Bytes));
        var names = new PdfArray();
        foreach ((_, PdfObject key, PdfObject value) in leaf)
        {
            names.Add(key);
            names.Add(value);
        }

        root.Remove("Kids");
        root.Remove("Limits");
        root.Set("Names", names);
        return changed;
    }

    private static int Compare(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i]) return a[i] - b[i];
        }

        return a.Length - b.Length;
    }

    private int RewriteLegacy(Dictionary<string, string> map)
    {
        if (_doc.ResolveDictionary(_doc.Catalog["Dests"]) is not PdfDictionary legacy) return 0;
        int changed = 0;
        foreach (string key in legacy.Keys.ToList())
        {
            if (!map.TryGetValue(key, out string? renamed) || legacy[key] is not PdfObject value) continue;
            legacy.Remove(key);
            legacy.Set(renamed, value);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Destination references anywhere in the document: /Dest of bookmarks and links, and /D of
    /// GoTo actions. Only those positions change, so an equal string elsewhere is left alone.
    /// </summary>
    private int RewriteReferences(Dictionary<string, string> map)
    {
        int changed = 0, budget = MaxObjects;
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfObject>();
        stack.Push(_doc.Catalog);
        while (stack.Count > 0 && budget-- > 0)
        {
            PdfObject? item = _doc.Resolve(stack.Pop());
            if (item is PdfStream stream) item = stream.Dictionary;
            if (item is null || !visited.Add(item)) continue;
            switch (item)
            {
                case PdfDictionary d:
                    if (d["Dest"] is PdfObject dest && Renamed(dest, map) is PdfObject newDest)
                    {
                        d.Set("Dest", newDest);
                        changed++;
                    }

                    if (d.GetName("S") == "GoTo" && d["D"] is PdfObject target && Renamed(target, map) is PdfObject newTarget)
                    {
                        d.Set("D", newTarget);
                        changed++;
                    }

                    foreach (KeyValuePair<string, PdfObject> e in d)
                    {
                        // The name tree holds definitions, not references, and was rewritten already.
                        if (e.Key is "Parent" || (e.Key == "Dests" && ReferenceEquals(d, _doc.ResolveDictionary(_doc.Catalog["Names"])))) continue;
                        if (e.Value is PdfDictionary or PdfArray or PdfReference) stack.Push(e.Value);
                    }

                    break;
                case PdfArray a:
                    foreach (PdfObject v in a)
                    {
                        if (v is PdfDictionary or PdfArray or PdfReference) stack.Push(v);
                    }

                    break;
            }
        }

        return changed;
    }

    /// <summary>The reference renamed in its own form, string or name; null when it names nothing renamed.</summary>
    private PdfObject? Renamed(PdfObject reference, Dictionary<string, string> map) => _doc.Resolve(reference) switch
    {
        PdfString s when map.TryGetValue(s.ToText(), out string? renamed) => PdfString.FromText(renamed),
        PdfName n when map.TryGetValue(n.Value, out string? renamed) => new PdfName(renamed),
        _ => null,
    };
}
