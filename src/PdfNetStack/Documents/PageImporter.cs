using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Documents;

/// <summary>
/// Copies pages from one document into another, or duplicates pages within a document.
/// Shared resources (fonts, images, color spaces) are copied once per source document and reused
/// by every later import. Annotations are copied fresh for each page, because an annotation
/// belongs to exactly one page. References to pages are remapped to their copies when those pages
/// are imported in the same call, and become null otherwise.
/// </summary>
internal sealed class PageImporter
{
    // Keys that never travel with a page: its place in the old tree, its slot in the old
    // structure tree, and article beads that point into the old document's threads.
    private static readonly HashSet<string> DroppedPageKeys = new(StringComparer.Ordinal) { "Parent", "StructParents", "B" };
    private static readonly HashSet<string> AnnotationKeys = new(StringComparer.Ordinal) { "Annots", "Popup", "IRT" };

    private readonly PdfDocument _source;
    private readonly PdfDocument _target;
    private readonly Dictionary<int, int> _shared = new();

    public PageImporter(PdfDocument source, PdfDocument target)
    {
        _source = source;
        _target = target;
    }

    private bool SameDocument => ReferenceEquals(_source, _target);

    public IReadOnlyList<PdfPage> Import(List<PdfPage> pages)
    {
        if (!SameDocument && !_source.Security.HasOwnerAccess && !_source.Security.Permissions.HasFlag(PdfPermissions.CopyContent))
        {
            throw new InvalidOperationException("The source document does not permit copying its content. Open it with the owner password.");
        }

        // Reserve numbers for every page first so links between them can be remapped.
        var pageMap = new Dictionary<int, int>();
        var numbers = new int[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            numbers[i] = _target.NextNumber++;
            pageMap.TryAdd(pages[i].Number, numbers[i]);
        }

        var batch = new Dictionary<int, int>();
        var work = new Queue<(PdfObject Source, int Target)>();
        var result = new List<PdfPage>(pages.Count);

        for (int i = 0; i < pages.Count; i++)
        {
            var copy = new PdfDictionary(pages[i].Dictionary.Count);
            for (int k = 0; k < pages[i].Dictionary.Count; k++)
            {
                (string key, PdfObject value) = pages[i].Dictionary.EntryAt(k);
                if (DroppedPageKeys.Contains(key)) continue;
                copy.Set(key, CopyValue(value, key, 0, pageMap, batch, work));
            }

            _target.Objects[numbers[i]] = copy;
            result.Add(new PdfPage(_target, numbers[i], copy));
        }

        while (work.Count > 0)
        {
            (PdfObject source, int target) = work.Dequeue();
            _target.Objects[target] = source switch
            {
                PdfStream s => new PdfStream(CopyDictionary(s.Dictionary, 0, pageMap, batch, work), s.Data),
                PdfDictionary d => CopyDictionary(d, 0, pageMap, batch, work),
                _ => CopyValue(source, null, 0, pageMap, batch, work),
            };
        }

        return result;
    }

    private PdfDictionary CopyDictionary(PdfDictionary d, int depth, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work)
    {
        var copy = new PdfDictionary(d.Count);
        bool annotation = IsAnnotation(d);
        for (int i = 0; i < d.Count; i++)
        {
            (string key, PdfObject value) = d.EntryAt(i);

            // An annotation's slot in the source structure tree means nothing in the copy.
            if (annotation && key == "StructParent") continue;
            copy.Set(key, CopyValue(value, key, depth + 1, pageMap, batch, work));
        }

        return copy;
    }

    private PdfObject CopyValue(PdfObject value, string? key, int depth, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work)
    {
        if (depth > PdfParser.MaxNesting) throw new PdfFormatException("Object nesting exceeds the supported depth.");
        switch (value)
        {
            case PdfReference r:
                return MapReference(r, key, pageMap, batch, work);
            case PdfArray a:
            {
                var items = new List<PdfObject>(a.Count);
                foreach (PdfObject item in a.Items) items.Add(CopyValue(item, key, depth + 1, pageMap, batch, work));
                return new PdfArray(items);
            }

            case PdfDictionary d:
                return CopyDictionary(d, depth, pageMap, batch, work);
            default:
                // Names, numbers, strings, booleans and null are immutable and can be shared.
                return value;
        }
    }

    private PdfObject MapReference(PdfReference r, string? key, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work)
    {
        if (!_source.Objects.TryGetValue(r.Number, out PdfObject? target)) return PdfNull.Instance;

        if (pageMap.TryGetValue(r.Number, out int copiedPage)) return new PdfReference(copiedPage, 0);
        if (_source.IsPageTreeNode(r.Number)) return PdfNull.Instance;
        if (_source.IsPage(r.Number)) return SameDocument ? r : PdfNull.Instance;

        PdfDictionary? dict = target switch
        {
            PdfDictionary d => d,
            PdfStream s => s.Dictionary,
            _ => null,
        };

        // Annotations belong to one page: copy them for every import, never share.
        if ((key is not null && AnnotationKeys.Contains(key)) || (dict is not null && IsAnnotation(dict)))
        {
            if (!batch.TryGetValue(r.Number, out int fresh))
            {
                fresh = _target.NextNumber++;
                batch[r.Number] = fresh;
                work.Enqueue((target, fresh));
            }

            return new PdfReference(fresh, 0);
        }

        if (SameDocument) return r;

        if (!_shared.TryGetValue(r.Number, out int number))
        {
            number = _target.NextNumber++;
            _shared[r.Number] = number;
            work.Enqueue((target, number));
        }

        return new PdfReference(number, 0);
    }

    private static bool IsAnnotation(PdfDictionary d) =>
        d.GetName("Type") == "Annot" || (d["Subtype"] is PdfName && d["Rect"] is not null && d["Type"] is null or PdfName { Value: "Annot" });
}
