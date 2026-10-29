using PdfNetStack.Content;
using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Optimization;

/// <summary>
/// What the content of a document uses: the resource names each resource dictionary is looked
/// up with, which streams are content streams and nothing else, and which glyphs each composite
/// font with an identity encoding shows. The optimizer runs it before it changes anything, and
/// the verifier runs it again on its own parse of the source, so every structural change is
/// checked against an analysis of the original.
/// </summary>
/// <remarks>
/// Content is executed from the page tree and from annotation appearances, following form
/// XObjects, tiling patterns, Type 3 glyph procedures and soft mask groups. Resource names are
/// collected by scanning the decoded bytes for every name token, whatever operator it belongs
/// to and even inside strings or inline image data, so a name counts as used whenever any reader
/// could look it up. A name that content with its own resources uses but does not define also
/// counts against the resources of the content that invoked it, since some readers fall back to
/// them. Glyphs are collected by following the font through Tf, gs, q and Q. Work is
/// bounded; when a budget runs out the analysis reports itself incomplete and nothing may be
/// pruned or merged.
/// </remarks>
internal sealed class ContentAnalysis
{
    /// <summary>Resource categories whose entries are looked up by name from content (ISO 32000-1, 7.8.3).</summary>
    public static readonly string[] Categories = ["Font", "XObject", "ExtGState", "Pattern", "Shading", "ColorSpace", "Properties"];

    private const long MaxBytes = 1L << 31;
    private const long MaxOperations = 50_000_000;
    private const int MaxDepth = 32;
    private const int MaxOperands = 4096;
    private const int MaxStateDepth = 256;

    private readonly PdfFile _file;
    private readonly ReferenceIndex _references;
    private readonly long _decodeLimit;
    private readonly CancellationToken _ct;

    private readonly Dictionary<PdfDictionary, HashSet<string>> _names = new();
    private readonly HashSet<PdfDictionary> _keepAll = [];
    private readonly HashSet<PdfDictionary> _holders = [];
    private readonly Dictionary<PdfDictionary, int> _indirect = new();
    private readonly HashSet<(PdfObject Holder, PdfDictionary? Resources, PdfDictionary? Font)> _executed = [];
    private readonly HashSet<(PdfObject Holder, PdfDictionary? Resources)> _reached = [];
    private readonly Queue<(PdfObject Holder, IReadOnlyList<PdfStream> Parts, PdfDictionary? Resources)> _deferred = new();
    private readonly Dictionary<PdfDictionary, HashSet<int>> _codes = new();
    private readonly HashSet<PdfDictionary> _unreliableFonts = [];
    private readonly Dictionary<PdfDictionary, HashSet<PdfDictionary>> _fallbacks = new();
    private long _bytes;
    private long _operations;
    private bool _exhausted;

    private ContentAnalysis(PdfFile file, ReferenceIndex references, long decodeLimit, CancellationToken ct)
    {
        _file = file;
        _references = references;
        _decodeLimit = decodeLimit;
        _ct = ct;
    }

    /// <summary>False when a work budget ran out: no resource may then be pruned and no font merged.</summary>
    public bool Complete => !_exhausted;

    /// <summary>
    /// False when some text could not be attributed to a font, or content could not be read
    /// operator by operator. Glyph sets are then incomplete and no font may be merged.
    /// </summary>
    public bool GlyphUsageComplete { get; private set; } = true;

    public ReferenceIndex References => _references;

    /// <summary>
    /// Resource category dictionaries whose entries may be pruned, with the names content may
    /// look up in them. A category dictionary that is missing here must be kept whole.
    /// </summary>
    public Dictionary<PdfDictionary, HashSet<string>> PrunableCategories { get; } = new();

    /// <summary>Object numbers of streams that every referrer uses as a content stream.</summary>
    public HashSet<int> ContentStreams { get; } = [];

    /// <summary>Codes shown with each composite font whose encoding is Identity-H or Identity-V.</summary>
    public IReadOnlyDictionary<PdfDictionary, HashSet<int>> IdentityCodes => _codes;

    /// <summary>Composite fonts some of whose text could not be decoded into codes.</summary>
    public IReadOnlyCollection<PdfDictionary> UnreliableFonts => _unreliableFonts;

    /// <summary>True when the font object <paramref name="fontNumber"/> is selectable only through category dictionaries this analysis saw completely.</summary>
    public bool FontFullyObserved(int fontNumber) =>
        Complete && GlyphUsageComplete &&
        _references.OnlyFrom(fontNumber, r => r.Container is PdfDictionary c && PrunableCategories.ContainsKey(c) && IsCategory(c, "Font"));

    private readonly Dictionary<PdfDictionary, string> _categoryOf = new();

    private bool IsCategory(PdfDictionary c, string category) => _categoryOf.TryGetValue(c, out string? k) && k == category;

    /// <summary>The prunable category dictionaries of one category, such as "Font".</summary>
    public IEnumerable<PdfDictionary> CategoryDictionaries(string category) => PrunableCategories.Keys.Where(c => IsCategory(c, category));

    public static ContentAnalysis Run(PdfFile file, IEnumerable<int> order, IEnumerable<string> ignoredKeys, long decodeLimit, CancellationToken ct)
    {
        List<int> reachable = order as List<int> ?? order.ToList();
        ReferenceIndex references = ReferenceIndex.Build(file, reachable, ignoredKeys, ct);
        var analysis = new ContentAnalysis(file, references, decodeLimit, ct);
        analysis.Execute();
        analysis.ClassifyContentStreams(reachable);
        return analysis;
    }

    private void Execute()
    {
        PdfDictionary? catalog = _file.ResolveDictionary(_file.Trailer["Root"]);
        try
        {
            if (catalog?["Pages"] is { } pages) WalkPageTree(pages, null, 0, []);
            while (_deferred.Count > 0 && !_exhausted)
            {
                (PdfObject holder, IReadOnlyList<PdfStream> parts, PdfDictionary? resources) = _deferred.Dequeue();
                Run(holder, parts, resources, null, 0);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PdfFormatException or StreamDecodeException)
        {
            _exhausted = true;
        }

        if (_exhausted)
        {
            GlyphUsageComplete = false;
            return;
        }

        Summarize();
    }

    private void WalkPageTree(PdfObject node, PdfDictionary? inherited, int depth, HashSet<PdfDictionary> path)
    {
        if (_file.Resolve(node) is not PdfDictionary dict || depth > 64 || !path.Add(dict)) return;
        try
        {
            PdfObject? own = dict["Resources"];
            PdfDictionary? resources = inherited;
            if (own is not null && _file.Resolve(own) is PdfDictionary r)
            {
                resources = r;
                Remember(r, own);
                _holders.Add(dict);
            }

            bool isPage = dict.GetName("Type") == "Page" || (dict.GetName("Type") != "Pages" && dict["Kids"] is null);
            if (!isPage)
            {
                if (_file.Resolve(dict["Kids"]) is PdfArray kids)
                {
                    foreach (PdfObject kid in kids) WalkPageTree(kid, resources, depth + 1, path);
                }

                return;
            }

            var parts = new List<PdfStream>();
            bool unreadable = false;
            switch (_file.Resolve(dict["Contents"]))
            {
                case PdfStream single:
                    parts.Add(single);
                    break;
                case PdfArray array:
                    foreach (PdfObject part in array)
                    {
                        PdfObject? resolved = _file.Resolve(part);
                        if (resolved is PdfStream s) parts.Add(s);
                        else if (resolved is not PdfNull) unreadable = true;
                    }

                    break;
            }

            if (unreadable && resources is not null) _keepAll.Add(resources);
            Run(dict, parts, resources, null, 0);

            if (_file.Resolve(dict["Annots"]) is PdfArray annotations)
            {
                foreach (PdfObject a in annotations)
                {
                    if (_file.Resolve(a) is not PdfDictionary annotation) continue;
                    if (_file.Resolve(annotation["AP"]) is not PdfDictionary ap) continue;
                    foreach (string state in new[] { "N", "R", "D" })
                    {
                        switch (_file.Resolve(ap[state]))
                        {
                            case PdfStream stream:
                                Defer(stream, resources);
                                break;
                            case PdfDictionary states:
                                foreach (KeyValuePair<string, PdfObject> entry in states)
                                {
                                    if (_file.Resolve(entry.Value) is PdfStream stateStream) Defer(stateStream, resources);
                                }

                                break;
                        }
                    }
                }
            }
        }
        finally
        {
            path.Remove(dict);
        }
    }

    /// <summary>Queues a form-like stream for execution with no font selected.</summary>
    private void Defer(PdfStream form, PdfDictionary? context)
    {
        PdfDictionary? resources = OwnResources(form.Dictionary, context) ?? context;
        if (_reached.Add((form, resources))) _deferred.Enqueue((form, [form], resources));
    }

    /// <summary>
    /// A holder's own /Resources, recording the holder as one whose resources this analysis owns.
    /// Some readers look a name the holder's resources lack up in the resources of the content
    /// that used it, so <paramref name="context"/> is remembered as a fallback.
    /// </summary>
    private PdfDictionary? OwnResources(PdfDictionary holder, PdfDictionary? context)
    {
        PdfObject? value = holder["Resources"];
        if (value is null || _file.Resolve(value) is not PdfDictionary resources) return null;
        Remember(resources, value);
        _holders.Add(holder);
        if (context is not null && !ReferenceEquals(context, resources))
        {
            if (!_fallbacks.TryGetValue(resources, out HashSet<PdfDictionary>? contexts))
            {
                contexts = [];
                _fallbacks[resources] = contexts;
            }

            contexts.Add(context);
        }

        return resources;
    }

    private void Remember(PdfDictionary resources, PdfObject value)
    {
        if (value is PdfReference r) _indirect[resources] = r.Number;
    }

    /// <summary>Executes content: one stream, or a page's parts read as one.</summary>
    private void Run(PdfObject holder, IReadOnlyList<PdfStream> parts, PdfDictionary? resources, PdfDictionary? font, int depth)
    {
        _ct.ThrowIfCancellationRequested();
        if (!_executed.Add((holder, resources, font))) return;
        _reached.Add((holder, resources));
        if (depth > MaxDepth)
        {
            GlyphUsageComplete = false;
            if (resources is not null) _keepAll.Add(resources);
            return;
        }

        List<byte[]>? decoded = Decode(parts);
        if (decoded is null)
        {
            // Content nobody can read still names resources a reader might find; keep them all.
            if (resources is not null) _keepAll.Add(resources);
            GlyphUsageComplete = false;
            return;
        }

        byte[] content = Join(decoded, (byte)'\n');
        var found = new HashSet<string>(StringComparer.Ordinal);
        ScanNames(content, found);

        // Readers that join the parts without a separator can see a name run across two parts.
        if (decoded.Count > 1) ScanNames(Join(decoded, null), found);
        if (resources is not null)
        {
            if (_names.TryGetValue(resources, out HashSet<string>? names)) names.UnionWith(found);
            else _names[resources] = found;
        }

        Interpret(content, resources, font, depth);
        if (resources is not null) FollowNames(found, resources);
    }

    private List<byte[]>? Decode(IReadOnlyList<PdfStream> parts)
    {
        var decoded = new List<byte[]>(parts.Count);
        foreach (PdfStream part in parts)
        {
            byte[]? data = DecodeOne(part);
            if (data is null) return null;
            decoded.Add(data);
        }

        return decoded;
    }

    private static byte[] Join(List<byte[]> parts, byte? separator)
    {
        if (parts.Count == 1) return parts[0];
        using var joined = new MemoryStream();
        foreach (byte[] data in parts)
        {
            joined.Write(data, 0, data.Length);
            if (separator is { } b) joined.WriteByte(b);
        }

        return joined.ToArray();
    }

    private byte[]? DecodeOne(PdfStream stream)
    {
        try
        {
            List<FilterSpec> filters = StreamCodec.GetFilters(stream.Dictionary, _file.Resolve);
            using DecodedStream decoded = StreamCodec.Decode(stream.Data, filters, DecodeDepth.Full, _decodeLimit, _file.Resolve);
            if (decoded.Remaining.Count > 0 || decoded.PendingPredictor is not null) return null;
            _bytes += decoded.Data.Length;
            if (_bytes > MaxBytes) _exhausted = true;
            return _exhausted ? null : decoded.Data.ToArray();
        }
        catch (StreamDecodeException)
        {
            return null;
        }
    }

    /// <summary>Every '/' followed by regular characters is a name some tokenization may produce.</summary>
    internal static void ScanNames(ReadOnlySpan<byte> s, HashSet<string> names)
    {
        int i = 0;
        while (true)
        {
            int slash = s[i..].IndexOf((byte)'/');
            if (slash < 0) return;
            int start = i + slash + 1;
            int end = start;
            while (end < s.Length && PdfSyntax.IsRegular(s[end])) end++;
            names.Add(PdfParser.DecodeName(s[start..end]));
            i = end;
            if (i >= s.Length) return;
        }
    }

    /// <summary>
    /// Holders named anywhere in the content, whatever the operator: every one of them is
    /// executed, so the resources they own or share are seen in full.
    /// </summary>
    private void FollowNames(HashSet<string> names, PdfDictionary resources)
    {
        PdfDictionary? xobjects = _file.Resolve(resources["XObject"]) as PdfDictionary;
        PdfDictionary? patterns = _file.Resolve(resources["Pattern"]) as PdfDictionary;
        PdfDictionary? fonts = _file.Resolve(resources["Font"]) as PdfDictionary;
        PdfDictionary? states = _file.Resolve(resources["ExtGState"]) as PdfDictionary;
        foreach (string name in names)
        {
            if (xobjects?[name] is { } x && _file.Resolve(x) is PdfStream form && IsForm(form.Dictionary)) Defer(form, resources);
            if (patterns?[name] is { } p && _file.Resolve(p) is PdfStream tiling) Defer(tiling, resources);
            if (fonts?[name] is { } f && _file.Resolve(f) is PdfDictionary font) DeferGlyphProcedures(font, resources);
            if (states?[name] is { } g && _file.Resolve(g) is PdfDictionary state) DeferGraphicsState(state, resources);
        }
    }

    private void DeferGlyphProcedures(PdfDictionary font, PdfDictionary context)
    {
        if (font.GetName("Subtype") != "Type3" || _file.Resolve(font["CharProcs"]) is not PdfDictionary procs) return;
        PdfDictionary resources = OwnResources(font, context) ?? context;
        foreach (KeyValuePair<string, PdfObject> entry in procs)
        {
            if (_file.Resolve(entry.Value) is PdfStream proc && _reached.Add((proc, resources))) _deferred.Enqueue((proc, [proc], resources));
        }
    }

    private void DeferGraphicsState(PdfDictionary state, PdfDictionary context)
    {
        if (_file.Resolve(state["SMask"]) is PdfDictionary mask && _file.Resolve(mask["G"]) is PdfStream group) Defer(group, context);
        if (_file.Resolve(state["Font"]) is PdfArray { Count: > 0 } selection && _file.Resolve(selection[0]) is PdfDictionary font)
        {
            DeferGlyphProcedures(font, context);
        }
    }

    private static bool IsForm(PdfDictionary d) => d.GetName("Subtype") is "Form" or null;

    /// <summary>
    /// Follows the font through the content and records the codes shown with it. Form XObjects
    /// run from Do inherit the current font; anything the walk cannot read for certain makes the
    /// glyph usage incomplete.
    /// </summary>
    private void Interpret(byte[] content, PdfDictionary? resources, PdfDictionary? font, int depth)
    {
        var parser = new PdfParser(content);
        var operands = new List<PdfObject>();
        var saved = new Stack<PdfDictionary?>();
        PdfDictionary? current = font;
        try
        {
            while (true)
            {
                int start = parser.Position;
                Token t = parser.NextToken();
                if (t.Kind == TokenKind.EndOfInput) return;
                if (t.Kind != TokenKind.Keyword || IsLiteral(parser.Span[t.Start..t.End]))
                {
                    parser.Position = start;
                    if (operands.Count >= MaxOperands) operands.RemoveAt(0);
                    operands.Add(parser.ParseObject());
                    continue;
                }

                if (++_operations > MaxOperations)
                {
                    _exhausted = true;
                    return;
                }

                string op = Compat.Latin1.GetString(parser.Span[t.Start..t.End]);
                switch (op)
                {
                    case "q":
                        if (saved.Count >= MaxStateDepth)
                        {
                            GlyphUsageComplete = false;
                            return;
                        }

                        saved.Push(current);
                        break;
                    case "Q":
                        if (saved.Count > 0) current = saved.Pop();
                        break;
                    case "Tf":
                        if (operands.Count < 2 || operands[^2] is not PdfName fontName)
                        {
                            GlyphUsageComplete = false;
                            return;
                        }

                        current = Lookup(resources, "Font", fontName.Value) as PdfDictionary ?? Missing;
                        break;
                    case "gs" when operands.Count > 0 && operands[^1] is PdfName stateName:
                        if (Lookup(resources, "ExtGState", stateName.Value) is PdfDictionary state &&
                            _file.Resolve(state["Font"]) is PdfArray { Count: > 0 } selection)
                        {
                            current = _file.Resolve(selection[0]) as PdfDictionary ?? Missing;
                        }

                        break;
                    case "Tj" or "'" or "\"" or "TJ":
                        foreach (PdfObject operand in operands) Show(current, operand);
                        break;
                    case "Do" when operands.Count > 0 && operands[^1] is PdfName formName:
                        if (Lookup(resources, "XObject", formName.Value) is PdfStream form && IsForm(form.Dictionary) && form.Dictionary.GetName("Subtype") == "Form")
                        {
                            Run(form, [form], OwnResources(form.Dictionary, resources) ?? resources, current, depth + 1);
                        }

                        break;
                    case "BI":
                        if (!SkipInlineImage(parser)) return;
                        break;
                    case ")" or "{" or "}":
                        // Stray delimiters: readers recover in different ways.
                        GlyphUsageComplete = false;
                        return;
                }

                operands.Clear();
            }
        }
        catch (PdfFormatException)
        {
            GlyphUsageComplete = false;
        }
    }

    private static readonly PdfDictionary Missing = new();

    private static bool IsLiteral(ReadOnlySpan<byte> k) => k.SequenceEqual("true"u8) || k.SequenceEqual("false"u8) || k.SequenceEqual("null"u8);

    private PdfObject? Lookup(PdfDictionary? resources, string category, string name) =>
        resources is not null && _file.Resolve(resources[category]) is PdfDictionary c ? _file.Resolve(c[name]) : null;

    private void Show(PdfDictionary? font, PdfObject operand)
    {
        switch (operand)
        {
            case PdfString s when s.Value.Length > 0:
                if (font is null)
                {
                    GlyphUsageComplete = false;
                    return;
                }

                if (font.GetName("Subtype") != "Type0" || font.GetName("Encoding") is not ("Identity-H" or "Identity-V")) return;
                if (!_codes.TryGetValue(font, out HashSet<int>? codes))
                {
                    codes = [];
                    _codes[font] = codes;
                }

                if (s.Value.Length % 2 != 0) _unreliableFonts.Add(font);
                for (int i = 0; i + 1 < s.Value.Length; i += 2) codes.Add((s.Value[i] << 8) | s.Value[i + 1]);
                break;
            case PdfArray items:
                foreach (PdfObject item in items) Show(font, item);
                break;
        }
    }

    /// <summary>Steps over BI ... ID data EI. False when the end of the data is ambiguous.</summary>
    private bool SkipInlineImage(PdfParser parser)
    {
        var image = new PdfDictionary();
        while (true)
        {
            Token t = parser.NextToken();
            if (t.Kind == TokenKind.EndOfInput) return Ambiguous();
            if (t.Kind == TokenKind.Keyword && parser.Span[t.Start..t.End].SequenceEqual("ID"u8)) break;
            if (t.Kind != TokenKind.Name) return Ambiguous();
            image.Set(PdfParser.DecodeName(parser.Span[(t.Start + 1)..t.End]), parser.ParseObject());
        }

        (_, int keywordEnd, bool unambiguous) = InlineImageBounds.Find(parser.Span, parser.Position + 1, image);
        if (!unambiguous) return Ambiguous();
        parser.Position = keywordEnd;
        return true;

        bool Ambiguous()
        {
            GlyphUsageComplete = false;
            return false;
        }
    }

    /// <summary>Turns the names seen per resource dictionary into prunable category dictionaries.</summary>
    private void Summarize()
    {
        PropagateFallbacks();
        var prunable = new HashSet<PdfDictionary>();
        foreach (PdfDictionary resources in _names.Keys)
        {
            if (_keepAll.Contains(resources)) continue;
            if (_indirect.TryGetValue(resources, out int number) &&
                !_references.OnlyFrom(number, r => r.Key == "Resources" && r.Container is PdfDictionary h && _holders.Contains(h)))
            {
                continue;
            }

            prunable.Add(resources);
        }

        var rejected = new HashSet<PdfDictionary>();
        foreach (PdfDictionary resources in prunable)
        {
            HashSet<string> names = _names[resources];
            foreach (string category in Categories)
            {
                PdfObject? value = resources[category];
                if (value is null || _file.Resolve(value) is not PdfDictionary c) continue;
                if (_categoryOf.TryGetValue(c, out string? seen) && seen != category) rejected.Add(c);
                _categoryOf[c] = category;
                if (value is PdfReference r &&
                    !_references.OnlyFrom(r.Number, x => x.Key == category && x.Container is PdfDictionary owner && prunable.Contains(owner)))
                {
                    rejected.Add(c);
                    continue;
                }

                if (!PrunableCategories.TryGetValue(c, out HashSet<string>? used))
                {
                    used = new HashSet<string>(StringComparer.Ordinal);
                    PrunableCategories[c] = used;
                }

                used.UnionWith(names);
            }
        }

        foreach (PdfDictionary c in rejected) PrunableCategories.Remove(c);
    }

    /// <summary>
    /// Names a form, pattern, glyph procedure or appearance uses but its own resources do not
    /// define count against the resources of the content that used it, to a fixpoint.
    /// </summary>
    private void PropagateFallbacks()
    {
        bool changed = true;
        for (int round = 0; changed && round < MaxDepth; round++)
        {
            changed = false;
            foreach ((PdfDictionary own, HashSet<PdfDictionary> contexts) in _fallbacks)
            {
                if (!_names.TryGetValue(own, out HashSet<string>? names)) continue;
                List<string> missing = names.Where(name => !Defines(own, name)).ToList();
                if (missing.Count == 0) continue;
                foreach (PdfDictionary context in contexts)
                {
                    // A context that ran no content of its own is never pruned; leave it alone.
                    if (!_names.TryGetValue(context, out HashSet<string>? outer)) continue;
                    foreach (string name in missing) changed |= outer.Add(name);
                }
            }
        }

        // Chains longer than the content nesting limit are not worth resolving; keep them whole.
        if (changed)
        {
            foreach (HashSet<PdfDictionary> contexts in _fallbacks.Values) _keepAll.UnionWith(contexts);
        }
    }

    private bool Defines(PdfDictionary resources, string name)
    {
        foreach (string category in Categories)
        {
            if (_file.Resolve(resources[category]) is PdfDictionary c && c[name] is not null) return true;
        }

        return false;
    }

    /// <summary>
    /// A stream is a content stream when every reference to it is in a content role: page
    /// contents, a form XObject or tiling pattern in resources, an appearance, a glyph
    /// procedure, a soft mask group or a widget icon.
    /// </summary>
    private void ClassifyContentStreams(List<int> reachable)
    {
        var xobjectCategories = new HashSet<PdfDictionary>();
        var patternCategories = new HashSet<PdfDictionary>();
        foreach (PdfDictionary resources in _references.ResourceDictionaries)
        {
            if (_file.Resolve(resources["XObject"]) is PdfDictionary x) xobjectCategories.Add(x);
            if (_file.Resolve(resources["Pattern"]) is PdfDictionary p) patternCategories.Add(p);
        }

        var appearanceStates = new HashSet<PdfDictionary>();
        foreach (PdfDictionary ap in _references.AppearanceDictionaries)
        {
            foreach (string state in new[] { "N", "R", "D" })
            {
                if (_file.Resolve(ap[state]) is PdfDictionary states) appearanceStates.Add(states);
            }
        }

        foreach (int number in reachable)
        {
            if (_file.GetObject(number) is not PdfStream stream) continue;
            PdfDictionary d = stream.Dictionary;
            string? type = d.GetName("Type");
            if (type is not (null or "XObject" or "Pattern")) continue;
            bool tiling = _file.Resolve(d["PatternType"]) is PdfNumber { IsInteger: true, IntegerValue: 1 };
            bool formLike = d.GetName("Subtype") is null or "Form";
            if (!formLike) continue;

            bool content = _references.OnlyFrom(number, r => r switch
            {
                { Container: PdfDictionary c, Key: not null } when xobjectCategories.Contains(c) => !tiling && d.GetName("Subtype") == "Form",
                { Container: PdfDictionary c, Key: not null } when patternCategories.Contains(c) => tiling,
                { Container: PdfDictionary c, Key: "Contents" } => c.GetName("Type") == "Page" && !tiling,
                { Container: PdfArray a } => _references.ContentArrays.Contains(a) && !tiling,
                { Container: PdfDictionary c, Key: "N" or "R" or "D" } when _references.AppearanceDictionaries.Contains(c) => !tiling,
                { Container: PdfDictionary c, Key: not null } when appearanceStates.Contains(c) => !tiling,
                { Container: PdfDictionary c, Key: not null } when _references.GlyphProcedureDictionaries.Contains(c) => !tiling,
                { Container: PdfDictionary c, Key: "G" } when _references.SoftMaskDictionaries.Contains(c) => !tiling,
                { Container: PdfDictionary c, Key: "I" or "RI" or "IX" } when _references.WidgetCharacteristics.Contains(c) => !tiling,
                _ => false,
            });
            if (content) ContentStreams.Add(number);
        }
    }
}
