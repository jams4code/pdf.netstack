using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>Where a reference sits: a dictionary entry (<see cref="Key"/>) or an array item (<see cref="Index"/>).</summary>
internal readonly record struct Referrer(PdfObject Container, string? Key, int Index);

/// <summary>
/// Every reference in the reachable graph, by target object number, plus the dictionaries that
/// play a structural role passes need to recognise: resource dictionaries, appearance
/// dictionaries, Type 3 glyph procedures, soft masks and widget icons. Passes that change an
/// object on purpose use it to prove the object is reached only in the role they understand.
/// </summary>
internal sealed class ReferenceIndex
{
    private static readonly IReadOnlyList<Referrer> None = [];

    private readonly PdfFile _file;
    private readonly Dictionary<int, List<Referrer>> _referrers = new();
    private readonly HashSet<string> _ignoredKeys;

    private ReferenceIndex(PdfFile file, IEnumerable<string> ignoredKeys)
    {
        _file = file;
        _ignoredKeys = new HashSet<string>(ignoredKeys, StringComparer.Ordinal);
    }

    /// <summary>Values of /Resources and /DR entries anywhere in the graph.</summary>
    public HashSet<PdfDictionary> ResourceDictionaries { get; } = [];

    /// <summary>Values of /AP entries.</summary>
    public HashSet<PdfDictionary> AppearanceDictionaries { get; } = [];

    /// <summary>/CharProcs dictionaries of Type 3 fonts.</summary>
    public HashSet<PdfDictionary> GlyphProcedureDictionaries { get; } = [];

    /// <summary>Soft mask dictionaries, the values of /SMask entries that are dictionaries.</summary>
    public HashSet<PdfDictionary> SoftMaskDictionaries { get; } = [];

    /// <summary>Widget appearance characteristics, the values of /MK entries.</summary>
    public HashSet<PdfDictionary> WidgetCharacteristics { get; } = [];

    /// <summary>Arrays that are a page's /Contents.</summary>
    public HashSet<PdfArray> ContentArrays { get; } = [];

    /// <param name="order">The reachable indirect objects.</param>
    /// <param name="ignoredKeys">Entries whose references do not count, for keys that opt-in stripping removes.</param>
    public static ReferenceIndex Build(PdfFile file, IEnumerable<int> order, IEnumerable<string> ignoredKeys, CancellationToken ct)
    {
        var index = new ReferenceIndex(file, ignoredKeys);
        foreach (string key in ObjectGraph.RootKeys.Append("ID"))
        {
            if (file.Trailer[key] is { } value) index.Visit(file.Trailer, key, -1, value, 0);
        }

        int n = 0;
        foreach (int number in order)
        {
            if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
            switch (file.GetObject(number))
            {
                case PdfStream s:
                    index.WalkDictionary(s.Dictionary, 0);
                    break;
                case PdfDictionary d:
                    index.WalkDictionary(d, 0);
                    break;
                case PdfArray a:
                    index.WalkArray(a, 0);
                    break;
            }
        }

        return index;
    }

    /// <summary>Every place that references object <paramref name="number"/>.</summary>
    public IReadOnlyList<Referrer> Of(int number) => _referrers.TryGetValue(number, out List<Referrer>? list) ? list : None;

    /// <summary>True when the object has at least one referrer and every referrer satisfies <paramref name="accept"/>.</summary>
    public bool OnlyFrom(int number, Func<Referrer, bool> accept)
    {
        IReadOnlyList<Referrer> list = Of(number);
        if (list.Count == 0) return false;
        foreach (Referrer r in list)
        {
            if (!accept(r)) return false;
        }

        return true;
    }

    private void WalkDictionary(PdfDictionary d, int depth)
    {
        if (depth > PdfParser.MaxNesting) throw new PdfFormatException("Object nesting exceeds the supported depth.");
        for (int i = 0; i < d.Count; i++)
        {
            (string key, PdfObject value) = d.EntryAt(i);
            if (_ignoredKeys.Contains(key)) continue;
            Note(d, key, value);
            Visit(d, key, -1, value, depth + 1);
        }
    }

    private void WalkArray(PdfArray a, int depth)
    {
        if (depth > PdfParser.MaxNesting) throw new PdfFormatException("Object nesting exceeds the supported depth.");
        for (int i = 0; i < a.Count; i++) Visit(a, null, i, a[i], depth + 1);
    }

    private void Visit(PdfObject container, string? key, int index, PdfObject value, int depth)
    {
        switch (value)
        {
            case PdfReference r:
                if (!_referrers.TryGetValue(r.Number, out List<Referrer>? list))
                {
                    list = new List<Referrer>(1);
                    _referrers[r.Number] = list;
                }

                list.Add(new Referrer(container, key, index));
                break;
            case PdfDictionary d:
                WalkDictionary(d, depth);
                break;
            case PdfArray a:
                WalkArray(a, depth);
                break;
        }
    }

    /// <summary>Records the structural role of an entry's value.</summary>
    private void Note(PdfDictionary container, string key, PdfObject value)
    {
        switch (key)
        {
            case "Resources" or "DR":
                if (_file.Resolve(value) is PdfDictionary resources) ResourceDictionaries.Add(resources);
                break;
            case "AP":
                if (_file.Resolve(value) is PdfDictionary ap) AppearanceDictionaries.Add(ap);
                break;
            case "CharProcs" when container.GetName("Subtype") == "Type3":
                if (_file.Resolve(value) is PdfDictionary procs) GlyphProcedureDictionaries.Add(procs);
                break;
            case "SMask":
                if (_file.Resolve(value) is PdfDictionary mask) SoftMaskDictionaries.Add(mask);
                break;
            case "MK":
                if (_file.Resolve(value) is PdfDictionary mk) WidgetCharacteristics.Add(mk);
                break;
            case "Contents" when container.GetName("Type") == "Page":
                if (_file.Resolve(value) is PdfArray parts) ContentArrays.Add(parts);
                break;
        }
    }
}
