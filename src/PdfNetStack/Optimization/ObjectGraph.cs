using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// The set of indirect objects reachable from the trailer, in breadth first order from the
/// catalog. Anything not in this set is garbage: unreachable by any conforming reader.
/// </summary>
internal sealed class ObjectGraph
{
    // Keys whose targets have identity semantics: the target object itself matters, not just
    // its value. Two equal annotations, structure elements or outline items are still two
    // distinct things, so objects reached through these keys are never merged.
    private static readonly HashSet<string> IdentityKeys = new(StringComparer.Ordinal)
    {
        "Parent", "Kids", "P", "Pg", "Obj", "Stm", "First", "Last", "Next", "Prev",
        "Annots", "Fields", "Popup", "IRT", "StructTreeRoot", "ParentTree", "Root", "Info",
        "AcroForm", "Outlines", "Threads", "B", "T", "Dest", "D", "SE", "K", "RoleMap",
        "Pages", "Names", "Dests", "OCProperties", "OCGs", "ON", "OFF", "Order", "RBGroups",
        "Locked", "AS", "Metadata", "V", "Lock", "SV", "Perms", "DSS", "OpenAction",
    };

    private ObjectGraph(List<int> order, HashSet<int> identitySensitive, bool signatureMarkers, int strippedEntries, bool generationMismatch)
    {
        Order = order;
        IdentitySensitive = identitySensitive;
        HasSignatureMarkers = signatureMarkers;
        StrippedEntries = strippedEntries;
        HasGenerationMismatch = generationMismatch;
    }

    /// <summary>
    /// A reference names a generation the cross-reference data does not record. Readers disagree
    /// on what that means, so the file is treated as damaged.
    /// </summary>
    public bool HasGenerationMismatch { get; }

    public List<int> Order { get; }

    public HashSet<int> IdentitySensitive { get; }

    public bool HasSignatureMarkers { get; }

    public int StrippedEntries { get; }

    public static IEnumerable<string> RootKeys => ["Root", "Info"];

    public static ObjectGraph Collect(PdfFile file, bool stripThumbnails, bool stripPieceInfo, CancellationToken ct)
    {
        var order = new List<int>();
        var seen = new HashSet<int>();
        var identity = new HashSet<int>();
        var queue = new Queue<int>();
        bool signature = false;
        bool generationMismatch = false;
        int stripped = 0;

        // The identity key through which an object was first reached. An indirect array reached
        // as /Annots or /Kids passes that identity on to its elements.
        var reachedVia = new Dictionary<int, string>();

        foreach (string key in RootKeys)
        {
            if (file.Trailer[key] is { } root) Walk(root, key, 0);
        }

        if (file.ResolveDictionary(file.Trailer["Root"]) is { } catalog && (catalog["Perms"] is not null || catalog["DSS"] is not null))
        {
            signature = true;
        }

        int visited = 0;
        while (queue.Count > 0)
        {
            if ((++visited & 1023) == 0) ct.ThrowIfCancellationRequested();
            int number = queue.Dequeue();
            PdfObject obj = file.GetObject(number);
            order.Add(number);
            switch (obj)
            {
                case PdfStream s:
                    Strip(s.Dictionary);
                    Inspect(s.Dictionary);
                    WalkDictionary(s.Dictionary, null, isStreamDictionary: true, depth: 0);
                    break;
                case PdfDictionary d:
                    Strip(d);
                    Inspect(d);
                    WalkDictionary(d, null, isStreamDictionary: false, depth: 0);
                    break;
                default:
                    Walk(obj, reachedVia.GetValueOrDefault(number), 0);
                    break;
            }
        }

        return new ObjectGraph(order, identity, signature, stripped, generationMismatch);

        void Enqueue(int number)
        {
            if (file.Exists(number) && seen.Add(number)) queue.Enqueue(number);
        }

        void Walk(PdfObject obj, string? key, int depth)
        {
            if (depth > PdfParser.MaxNesting) throw new PdfFormatException("Object nesting exceeds the supported depth.");
            switch (obj)
            {
                case PdfReference r:
                    if (key is not null && IdentityKeys.Contains(key))
                    {
                        identity.Add(r.Number);
                        reachedVia.TryAdd(r.Number, key);
                    }

                    if (file.Exists(r.Number) && file.GenerationOf(r.Number) != r.Generation) generationMismatch = true;
                    Enqueue(r.Number);
                    break;
                case PdfArray a:
                    foreach (PdfObject item in a.Items) Walk(item, key, depth + 1);
                    break;
                case PdfDictionary d:
                    Strip(d);
                    Inspect(d);
                    WalkDictionary(d, key, isStreamDictionary: false, depth + 1);
                    break;
            }
        }

        void WalkDictionary(PdfDictionary d, string? parentKey, bool isStreamDictionary, int depth)
        {
            for (int i = 0; i < d.Count; i++)
            {
                (string key, PdfObject value) = d.EntryAt(i);

                // The writer always emits /Length directly, so an indirect length object is dead weight.
                if (isStreamDictionary && key == "Length") continue;
                Walk(value, key, depth);
            }
        }

        void Inspect(PdfDictionary d)
        {
            if (signature) return;
            if (d.ContainsKey("ByteRange")) signature = true;
            string? type = d.GetName("Type");
            if (type is "Sig" or "DocTimeStamp") signature = true;
            if (d.GetName("FT") == "Sig" && d["V"] is not null and not PdfNull) signature = true;
        }

        void Strip(PdfDictionary d)
        {
            if (stripPieceInfo && d.Remove("PieceInfo")) stripped++;
            if (stripThumbnails && d.GetName("Type") == "Page" && d.Remove("Thumb")) stripped++;
        }
    }
}
