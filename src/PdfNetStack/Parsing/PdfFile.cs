using PdfNetStack.Filters;
using PdfNetStack.Objects;
using PdfNetStack.Security;

namespace PdfNetStack.Parsing;

internal enum XrefEntryType : byte
{
    Free = 0,
    InFile = 1,
    InObjectStream = 2,
}

/// <summary>For <see cref="XrefEntryType.InFile"/>, Field1 is the byte offset. For object streams it is the stream number and Field2 the index.</summary>
internal readonly record struct XrefEntry(XrefEntryType Type, long Field1, int Field2);

/// <summary>
/// Read side of a PDF: cross-reference loading across every revision, lazy object
/// resolution, object stream expansion and reconstruction of damaged files.
/// The source buffer is never modified; stream data is sliced from it without copying.
/// </summary>
internal sealed class PdfFile
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly long _decodeLimit;
    private readonly Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject> _cache = new();
    private readonly Dictionary<int, ObjectStreamIndex> _objectStreams = new();
    private readonly HashSet<int> _resolving = new();
    private bool _loadingXref;

    // Offsets are defined from the start of the file, but producers that prepend bytes before
    // %PDF often measure from the header instead. Both conventions are tried, header first.
    private int _offsetBase;

    // Start offsets of every known in-file object, sorted. A parse never reads past the next
    // object's start, which keeps hostile files (unterminated strings, missing endstream) linear.
    private long[] _objectStarts = [];
    private long _objectStreamCacheBytes;
    private long _objectStreamDecodedTotal;

    // Indirect /Length, /Filter or /N values can chain through other streams. Each link costs
    // stack frames, so the chain is cut off well before the stack is at risk.
    private const int MaxResolveDepth = 24;

    private PdfFile(ReadOnlyMemory<byte> data, long decodeLimit)
    {
        _data = data;
        _decodeLimit = decodeLimit;
        Trailer = new PdfDictionary();
    }

    public PdfDictionary Trailer { get; private set; }

    public string HeaderVersion { get; private set; } = "1.4";

    /// <summary>Byte offset of the %PDF header. Some files carry junk before it.</summary>
    public int HeaderOffset { get; private set; }

    public int XrefSectionCount { get; private set; }

    public bool IsLinearized { get; private set; }

    public bool UsesXrefStreams { get; private set; }

    public bool UsesObjectStreams => _xref.Values.Any(e => e.Type == XrefEntryType.InObjectStream);

    public bool WasReconstructed { get; private set; }

    public ReadOnlyMemory<byte> Data => _data;

    public IReadOnlyDictionary<int, XrefEntry> Xref => _xref;

    /// <summary>
    /// Number of revisions. A linearized file has two xref sections but one revision.
    /// </summary>
    public int RevisionCount => Math.Max(1, IsLinearized ? XrefSectionCount - 1 : XrefSectionCount);

    public static PdfFile Open(ReadOnlyMemory<byte> data, long decodeLimit)
    {
        var file = new PdfFile(data, decodeLimit);
        file.ReadHeader();
        bool loaded = file.TryLoadXref(file.HeaderOffset) || (file.HeaderOffset > 0 && file.TryLoadXref(0));
        if (!loaded) file.Reconstruct();
        file.IndexObjectStarts();

        if (file.Trailer["Root"] is null) throw new PdfFormatException("Document has no catalog (/Root).");
        return file;
    }

    private bool TryLoadXref(int offsetBase)
    {
        _xref.Clear();
        _cache.Clear();
        _objectStreams.Clear();
        _objectStreamCacheBytes = 0;
        _objectStarts = [];
        Trailer = new PdfDictionary();
        XrefSectionCount = 0;
        UsesXrefStreams = false;
        _offsetBase = offsetBase;
        try
        {
            _loadingXref = true;
            LoadXrefChain();
            return ValidateXref();
        }
        catch (Exception ex) when (ex is PdfFormatException or StreamDecodeException or InvalidCastException or ArgumentOutOfRangeException)
        {
            return false;
        }
        finally
        {
            _loadingXref = false;
            _cache.Clear();
        }
    }

    private void IndexObjectStarts()
    {
        _objectStarts = _xref.Values.Where(e => e.Type == XrefEntryType.InFile).Select(e => e.Field1).Distinct().OrderBy(x => x).ToArray();
    }

    /// <summary>End of the region an object starting at <paramref name="offset"/> may occupy.</summary>
    private int RegionEnd(long offset)
    {
        int i = Array.BinarySearch(_objectStarts, offset + 1);
        if (i < 0) i = ~i;
        return i < _objectStarts.Length ? (int)_objectStarts[i] : _data.Length;
    }

    /// <summary>Generation recorded for an object number; object stream members are generation 0.</summary>
    public int GenerationOf(int number) =>
        _xref.TryGetValue(number, out XrefEntry e) && e.Type == XrefEntryType.InFile ? e.Field2 : 0;

    /// <summary>Security handler after <see cref="Unlock"/>; null for unencrypted or still locked files.</summary>
    public StandardSecurity? Security { get; private set; }

    private int _encryptNumber = -1;

    /// <summary>
    /// Authenticates against the /Encrypt dictionary. Every object read afterwards is decrypted.
    /// </summary>
    /// <exception cref="PdfPasswordException">The password matches neither user nor owner password.</exception>
    public void Unlock(string? password)
    {
        PdfObject? encrypt = Trailer["Encrypt"];
        if (encrypt is PdfReference r) _encryptNumber = r.Number;
        if (Resolve(encrypt) is not PdfDictionary dict) throw new PdfFormatException("Encryption dictionary is missing.");
        byte[] id0 = Resolve(Trailer["ID"]) is PdfArray { Count: > 0 } id && Resolve(id[0]) is PdfString s0 ? s0.Value : [];
        int budget = 4096;
        Security = StandardSecurity.Open((PdfDictionary)DeepResolve(dict, 0, ref budget), id0, password, Resolve);

        // Everything read so far was read encrypted. Keep only the encryption dictionary.
        lock (_cache)
        {
            _cache.Clear();
            _objectStreams.Clear();
            _objectStreamCacheBytes = 0;
            if (_encryptNumber >= 0) _cache[_encryptNumber] = dict;
        }
    }

    /// <summary>
    /// A copy with every indirect reference replaced by its value, for small self-contained
    /// dictionaries. A node budget keeps shared or deeply nested references from expanding.
    /// </summary>
    private PdfObject DeepResolve(PdfObject obj, int depth, ref int budget)
    {
        if (--budget < 0) throw new PdfFormatException("Encryption dictionary is too large.");
        PdfObject value = Resolve(obj) ?? PdfNull.Instance;
        if (depth > 16) return value;
        switch (value)
        {
            case PdfDictionary d:
            {
                var copy = new PdfDictionary(d.Count);
                for (int i = 0; i < d.Count; i++) copy.Set(d.EntryAt(i).Key, DeepResolve(d.ValueAt(i), depth + 1, ref budget));
                return copy;
            }

            case PdfArray a:
            {
                var items = new List<PdfObject>(a.Count);
                foreach (PdfObject item in a.Items) items.Add(DeepResolve(item, depth + 1, ref budget));
                return new PdfArray(items);
            }
            default:
                return value;
        }
    }

    public PdfObject? Resolve(PdfObject? obj)
    {
        int guard = 0;
        while (obj is PdfReference r)
        {
            if (++guard > 32) return PdfNull.Instance;
            obj = GetObject(r.Number);
        }

        return obj;
    }

    public PdfDictionary? ResolveDictionary(PdfObject? obj) => Resolve(obj) switch
    {
        PdfDictionary d => d,
        PdfStream s => s.Dictionary,
        _ => null,
    };

    public bool Exists(int number) =>
        _xref.TryGetValue(number, out XrefEntry e) && e.Type != XrefEntryType.Free;

    /// <summary>
    /// Returns the object with the given number. A missing or unreadable object is null, which
    /// is how the specification defines a reference to a non-existent object.
    /// </summary>
    public PdfObject GetObject(int number)
    {
        lock (_cache)
        {
            return GetObjectLocked(number);
        }
    }

    private PdfObject GetObjectLocked(int number)
    {
        if (_cache.TryGetValue(number, out PdfObject? cached)) return cached;
        if (!_xref.TryGetValue(number, out XrefEntry entry) || entry.Type == XrefEntryType.Free) return PdfNull.Instance;
        if (!_resolving.Add(number)) return PdfNull.Instance;
        if (_resolving.Count > MaxResolveDepth)
        {
            // Not cached: the same object read from a shallower context may well succeed.
            _resolving.Remove(number);
            UnreadableObjects++;
            return PdfNull.Instance;
        }

        try
        {
            PdfObject obj = entry.Type == XrefEntryType.InFile
                ? ReadIndirectObject(entry.Field1, number)
                : ReadFromObjectStream((int)entry.Field1, entry.Field2, number);
            _cache[number] = obj;
            return obj;
        }
        catch (Exception ex) when (ex is PdfFormatException or StreamDecodeException)
        {
            _cache[number] = PdfNull.Instance;
            UnreadableObjects++;
            return PdfNull.Instance;
        }
        finally
        {
            _resolving.Remove(number);
        }
    }

    /// <summary>Objects listed in the xref that could not be parsed and therefore read as null.</summary>
    public int UnreadableObjects { get; private set; }

    private void ReadHeader()
    {
        ReadOnlySpan<byte> s = _data.Span;
        int window = Math.Min(s.Length, 1024);
        int at = s[..window].IndexOf("%PDF-"u8);
        if (at < 0) throw new PdfFormatException("Missing %PDF header.");
        HeaderOffset = at;
        int p = at + 5;
        int end = p;
        while (end < s.Length && end - p < 4 && (PdfSyntax.IsDigit(s[end]) || s[end] == (byte)'.')) end++;
        if (end > p) HeaderVersion = System.Text.Encoding.ASCII.GetString(s[p..end]);
        IsLinearized = DetectLinearization(s, at);
    }

    private static bool DetectLinearization(ReadOnlySpan<byte> s, int headerOffset)
    {
        int window = Math.Min(s.Length - headerOffset, 2048);
        ReadOnlySpan<byte> head = s.Slice(headerOffset, window);
        int obj = head.IndexOf(" obj"u8);
        if (obj < 0) return false;
        int end = head[obj..].IndexOf("endobj"u8);
        ReadOnlySpan<byte> body = end < 0 ? head[obj..] : head.Slice(obj, end);
        return body.IndexOf("/Linearized"u8) >= 0;
    }

    private long FindStartXref()
    {
        ReadOnlySpan<byte> s = _data.Span;
        int tailStart = Math.Max(0, s.Length - 4096);
        int at = s[tailStart..].LastIndexOf("startxref"u8);
        if (at < 0)
        {
            at = s.LastIndexOf("startxref"u8);
            if (at < 0) throw new PdfFormatException("startxref not found.");
        }
        else
        {
            at += tailStart;
        }

        var parser = new PdfParser(_data, at + 9);
        if (!parser.TryReadInteger(out long offset)) throw new PdfFormatException("startxref offset missing.");
        return offset;
    }

    private void LoadXrefChain()
    {
        long offset = FindStartXref();
        var visited = new HashSet<long>();
        bool first = true;
        while (true)
        {
            if (!visited.Add(offset)) break;
            long actual = offset + _offsetBase;
            if (actual < 0 || actual >= _data.Length)
            {
                // Offsets measured from the header are the norm; some files measure from byte 0.
                actual = offset;
                if (actual < 0 || actual >= _data.Length) throw new PdfFormatException("xref offset outside the file.");
            }

            var section = new Dictionary<int, XrefEntry>();
            PdfDictionary trailer = ReadXrefSection(actual, section);
            XrefSectionCount++;

            if (trailer["XRefStm"] is PdfNumber { IsInteger: true } stm)
            {
                var hybrid = new Dictionary<int, XrefEntry>();
                ReadXrefSection(stm.IntegerValue + _offsetBase, hybrid);
                foreach ((int num, XrefEntry e) in hybrid)
                {
                    if (!section.TryGetValue(num, out XrefEntry existing) || existing.Type == XrefEntryType.Free)
                    {
                        section[num] = e;
                    }
                }
            }

            foreach ((int num, XrefEntry e) in section) _xref.TryAdd(num, e);

            if (first)
            {
                Trailer = trailer;
                first = false;
            }
            else
            {
                // Older trailers can carry keys the newest one omits, such as /Info.
                foreach (string key in new[] { "Root", "Info", "ID", "Encrypt" })
                {
                    if (Trailer[key] is null && trailer[key] is { } value) Trailer.Set(key, value);
                }
            }

            if (trailer["Prev"] is PdfNumber { IsInteger: true } prev) offset = prev.IntegerValue;
            else break;
        }
    }

    private PdfDictionary ReadXrefSection(long offset, Dictionary<int, XrefEntry> section)
    {
        if (offset < 0 || offset >= _data.Length) throw new PdfFormatException("xref offset outside the file.");
        var parser = new PdfParser(_data, (int)offset);
        if (parser.TryReadKeyword("xref"u8)) return ReadXrefTable(parser, section);

        parser.Position = (int)offset;
        PdfObject obj = ReadIndirectObject(offset, expectedNumber: null);
        if (obj is not PdfStream xs || xs.Dictionary.GetName("Type") != "XRef")
        {
            throw new PdfFormatException("startxref does not point at a cross-reference section.");
        }

        UsesXrefStreams = true;
        ReadXrefStream(xs, section);
        return xs.Dictionary;
    }

    private PdfDictionary ReadXrefTable(PdfParser parser, Dictionary<int, XrefEntry> section)
    {
        while (true)
        {
            if (parser.TryReadKeyword("trailer"u8))
            {
                if (parser.ParseObject() is not PdfDictionary trailer) throw new PdfFormatException("Invalid trailer.");
                return trailer;
            }

            if (!parser.TryReadInteger(out long start) || !parser.TryReadInteger(out long count) || start < 0 || count < 0)
            {
                throw new PdfFormatException("Invalid xref subsection header.");
            }

            for (long i = 0; i < count; i++)
            {
                if (!parser.TryReadInteger(out long field1) || !parser.TryReadInteger(out long gen))
                {
                    throw new PdfFormatException("Invalid xref entry.");
                }

                Token t = parser.NextToken();
                if (t.Kind != TokenKind.Keyword || t.End - t.Start != 1) throw new PdfFormatException("Invalid xref entry type.");
                byte kind = parser.Span[t.Start];
                long num = start + i;
                if (num > int.MaxValue) throw new PdfFormatException("Object number out of range.");
                if (kind == (byte)'n' && field1 > 0)
                {
                    section.TryAdd((int)num, new XrefEntry(XrefEntryType.InFile, field1 + _offsetBase, (int)gen));
                }
                else
                {
                    section.TryAdd((int)num, new XrefEntry(XrefEntryType.Free, 0, (int)gen));
                }
            }
        }
    }

    private void ReadXrefStream(PdfStream xs, Dictionary<int, XrefEntry> section)
    {
        PdfDictionary d = xs.Dictionary;
        if (Resolve(d["W"]) is not PdfArray w || w.Count < 3) throw new PdfFormatException("xref stream without /W.");
        int w0 = IntOf(w[0]), w1 = IntOf(w[1]), w2 = IntOf(w[2]);
        if (w0 is < 0 or > 4 || w1 is < 0 or > 8 || w2 is < 0 or > 4) throw new PdfFormatException("Unsupported /W widths.");
        int rowSize = w0 + w1 + w2;
        if (rowSize == 0) throw new PdfFormatException("Empty /W.");

        List<FilterSpec> filters = StreamCodec.GetFilters(d, Resolve);
        using DecodedStream decoded = StreamCodec.Decode(xs.Data, filters, DecodeDepth.Full, _decodeLimit, Resolve);
        if (decoded.Remaining.Count > 0 || decoded.PendingPredictor is not null) throw new PdfFormatException("Undecodable xref stream.");
        ReadOnlySpan<byte> data = decoded.Data.Span;

        var index = new List<(long Start, long Count)>();
        if (Resolve(d["Index"]) is PdfArray idx && idx.Count >= 2)
        {
            for (int i = 0; i + 1 < idx.Count; i += 2) index.Add((IntOf(idx[i]), IntOf(idx[i + 1])));
        }
        else
        {
            index.Add((0, IntOf(d["Size"])));
        }

        int pos = 0;
        foreach ((long start, long count) in index)
        {
            for (long i = 0; i < count; i++)
            {
                if (pos + rowSize > data.Length) return;
                long type = w0 == 0 ? 1 : ReadField(data, pos, w0);
                long f1 = ReadField(data, pos + w0, w1);
                long f2 = ReadField(data, pos + w0 + w1, w2);
                pos += rowSize;
                long num = start + i;
                if (num is < 0 or > int.MaxValue) continue;
                XrefEntry entry = type switch
                {
                    1 when f1 > 0 => new XrefEntry(XrefEntryType.InFile, f1 + _offsetBase, (int)f2),
                    2 => new XrefEntry(XrefEntryType.InObjectStream, f1, (int)f2),
                    _ => new XrefEntry(XrefEntryType.Free, 0, 0),
                };
                section.TryAdd((int)num, entry);
            }
        }
    }

    private static long ReadField(ReadOnlySpan<byte> data, int pos, int width)
    {
        long v = 0;
        for (int i = 0; i < width; i++) v = (v << 8) | data[pos + i];
        return v;
    }

    private int IntOf(PdfObject? o) =>
        Resolve(o) is PdfNumber { IsInteger: true } n && n.IntegerValue is >= 0 and <= int.MaxValue
            ? (int)n.IntegerValue
            : throw new PdfFormatException("Expected a non-negative integer.");

    /// <summary>Checks that every in-file entry really points at "n g obj".</summary>
    private bool ValidateXref()
    {
        if (Trailer["Root"] is null) return false;
        foreach ((int num, XrefEntry e) in _xref)
        {
            if (e.Type != XrefEntryType.InFile) continue;
            if (e.Field1 < 0 || e.Field1 >= _data.Length) return false;
            var parser = new PdfParser(_data, (int)e.Field1);
            if (!parser.TryReadInteger(out long n) || n != num) return false;
            if (!parser.TryReadInteger(out _) || !parser.TryReadKeyword("obj"u8)) return false;
        }

        foreach ((_, XrefEntry e) in _xref)
        {
            if (e.Type == XrefEntryType.InObjectStream &&
                (!_xref.TryGetValue((int)e.Field1, out XrefEntry host) || host.Type != XrefEntryType.InFile))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rebuilds the cross-reference table by scanning for "n g obj" headers. The last
    /// definition of an object number wins, matching how incremental updates append.
    /// </summary>
    private void Reconstruct()
    {
        WasReconstructed = true;
        PdfDictionary previousTrailer = Trailer;
        _xref.Clear();
        _cache.Clear();
        _objectStreams.Clear();
        _objectStreamCacheBytes = 0;
        _objectStarts = [];

        ReadOnlySpan<byte> s = _data.Span;
        var direct = new Dictionary<int, XrefEntry>();
        int searchFrom = 0;
        while (true)
        {
            int rel = s[searchFrom..].IndexOf("obj"u8);
            if (rel < 0) break;
            int at = searchFrom + rel;
            searchFrom = at + 3;
            if (at + 3 < s.Length && PdfSyntax.IsRegular(s[at + 3])) continue;
            if (TryReadObjectHeaderBackwards(s, at, out int num, out int gen, out int start))
            {
                direct[num] = new XrefEntry(XrefEntryType.InFile, start, gen);
            }
        }

        foreach ((int num, XrefEntry e) in direct) _xref[num] = e;
        IndexObjectStarts();

        // Expand object streams found during the scan.
        foreach ((int num, XrefEntry e) in direct)
        {
            PdfObject obj;
            try { obj = ReadIndirectObject(e.Field1, num); }
            catch (PdfFormatException) { continue; }
            if (obj is PdfStream st && st.Dictionary.GetName("Type") == "ObjStm")
            {
                try
                {
                    ObjectStreamIndex index = LoadObjectStream(num, st);
                    for (int i = 0; i < index.Numbers.Length; i++)
                    {
                        _xref.TryAdd(index.Numbers[i], new XrefEntry(XrefEntryType.InObjectStream, num, i));
                    }
                }
                catch (Exception ex) when (ex is PdfFormatException or StreamDecodeException)
                {
                    // An unreadable object stream contributes nothing.
                }
            }
        }

        // The scan above parsed objects only to find object streams; count repairs on real reads.
        _cache.Clear();
        LengthsRepaired = 0;
        UnreadableObjects = 0;
        PdfDictionary trailer = FindTrailerByScan(s) ?? new PdfDictionary();
        foreach (string key in new[] { "Root", "Info", "ID", "Encrypt" })
        {
            if (trailer[key] is null && previousTrailer[key] is { } v) trailer.Set(key, v);
        }

        if (trailer["Root"] is PdfReference root && Resolve(root) is not PdfDictionary) trailer.Remove("Root");
        if (trailer["Root"] is null)
        {
            foreach (int num in _xref.Keys.OrderByDescending(n => n))
            {
                if (GetObject(num) is PdfDictionary d && d.GetName("Type") == "Catalog")
                {
                    trailer.Set("Root", new PdfReference(num, 0));
                    break;
                }
            }
        }

        Trailer = trailer;
        XrefSectionCount = Math.Max(1, XrefSectionCount);
    }

    private PdfDictionary? FindTrailerByScan(ReadOnlySpan<byte> s)
    {
        int at = s.Length;
        for (int attempt = 0; attempt < 16 && at > 0; attempt++)
        {
            int found = s[..at].LastIndexOf("trailer"u8);
            if (found < 0) break;
            at = found;
            try
            {
                var parser = new PdfParser(_data[..Math.Min(_data.Length, found + 7 + 65536)], found + 7);
                if (parser.ParseObject() is PdfDictionary d && d["Root"] is not null) return d;
            }
            catch (PdfFormatException)
            {
                // Keep looking further back.
            }
        }

        foreach ((int num, XrefEntry e) in _xref.Where(x => x.Value.Type == XrefEntryType.InFile).OrderByDescending(x => x.Value.Field1))
        {
            PdfObject obj = GetObject(num);
            if (obj is PdfStream st && st.Dictionary.GetName("Type") == "XRef" && st.Dictionary["Root"] is not null)
            {
                return st.Dictionary;
            }
        }

        return null;
    }

    private static bool TryReadObjectHeaderBackwards(ReadOnlySpan<byte> s, int objAt, out int num, out int gen, out int start)
    {
        num = gen = start = 0;
        int p = objAt - 1;
        int ws = p;
        while (p >= 0 && PdfSyntax.IsWhitespace(s[p])) p--;
        if (p == ws) return false;
        int genEnd = p + 1;
        while (p >= 0 && PdfSyntax.IsDigit(s[p])) p--;
        int genStart = p + 1;
        if (genStart == genEnd || genEnd - genStart > 5) return false;
        ws = p;
        while (p >= 0 && PdfSyntax.IsWhitespace(s[p])) p--;
        if (p == ws) return false;
        int numEnd = p + 1;
        while (p >= 0 && PdfSyntax.IsDigit(s[p])) p--;
        int numStart = p + 1;
        if (numStart == numEnd || numEnd - numStart > 10) return false;
        if (p >= 0 && PdfSyntax.IsRegular(s[p])) return false;
        if (!PdfParser.TryParseInteger(s[numStart..numEnd], out long n) || n > int.MaxValue) return false;
        if (!PdfParser.TryParseInteger(s[genStart..genEnd], out long g) || g > 65535) return false;
        num = (int)n;
        gen = (int)g;
        start = numStart;
        return true;
    }

    private PdfObject ReadIndirectObject(long offset, int? expectedNumber)
    {
        if (offset < 0 || offset >= _data.Length) throw new PdfFormatException("Object offset outside the file.");
        ReadOnlyMemory<byte> region = _data[..RegionEnd(offset)];
        var parser = new PdfParser(region, (int)offset);
        if (!parser.TryReadInteger(out long num) || !parser.TryReadInteger(out long gen) || !parser.TryReadKeyword("obj"u8))
        {
            throw new PdfFormatException($"No object header at offset {offset}.");
        }

        if (expectedNumber is { } expected && num != expected)
        {
            throw new PdfFormatException($"Expected object {expected} at offset {offset}, found {num}.");
        }

        PdfObject obj = parser.ParseObject();
        if (obj is PdfDictionary dict)
        {
            int afterDict = parser.Position;
            if (parser.TryReadKeyword("stream"u8))
            {
                obj = new PdfStream(dict, ReadStreamData(parser, dict, region));
            }
            else
            {
                parser.Position = afterDict;
            }
        }

        return Security is null || num == _encryptNumber ? obj : Decrypt(obj, (int)num, (int)gen);
    }

    /// <summary>Decrypts strings and stream data of one in-file object, in place where possible.</summary>
    private PdfObject Decrypt(PdfObject obj, int number, int generation)
    {
        StandardSecurity security = Security!;
        if (obj is PdfStream stream)
        {
            DecryptStrings(stream.Dictionary, number, generation, 0);
            string? type = stream.Dictionary.GetName("Type");
            if (type == "XRef") return stream;
            if (type == "Metadata" && !security.EncryptMetadata) return stream;
            if (!RemoveIdentityCryptFilter(stream.Dictionary)) return stream;
            stream.Data = security.Decrypt(number, generation, stream.Data.Span, isString: false);
            return stream;
        }

        return DecryptValue(obj, number, generation, 0);
    }

    private PdfObject DecryptValue(PdfObject value, int number, int generation, int depth)
    {
        switch (value)
        {
            case PdfString s:
                return new PdfString(Security!.Decrypt(number, generation, s.Value, isString: true), s.WasHex);
            case PdfArray a when depth < PdfParser.MaxNesting:
                for (int i = 0; i < a.Count; i++) a[i] = DecryptValue(a[i], number, generation, depth + 1);
                return a;
            case PdfDictionary d when depth < PdfParser.MaxNesting:
                DecryptStrings(d, number, generation, depth + 1);
                return d;
            default:
                return value;
        }
    }

    private void DecryptStrings(PdfDictionary d, int number, int generation, int depth)
    {
        for (int i = 0; i < d.Count; i++) d.SetValueAt(i, DecryptValue(d.ValueAt(i), number, generation, depth));
    }

    /// <summary>
    /// A leading /Crypt filter selects the crypt filter for this stream. Identity means the data is
    /// stored in the clear. Returns false when the data must not be decrypted. The filter entry is
    /// removed either way, since the data is decrypted now.
    /// </summary>
    private bool RemoveIdentityCryptFilter(PdfDictionary dict)
    {
        PdfObject? filter = Resolve(dict["Filter"]);
        PdfObject? parms = Resolve(dict["DecodeParms"]);
        bool isCrypt = filter is PdfName { Value: "Crypt" } || (filter is PdfArray { Count: > 0 } fa && Resolve(fa[0]) is PdfName { Value: "Crypt" });
        if (!isCrypt) return true;

        PdfDictionary? cryptParms = parms switch
        {
            PdfDictionary pd => pd,
            PdfArray { Count: > 0 } pa => Resolve(pa[0]) as PdfDictionary,
            _ => null,
        };
        bool identity = (cryptParms?.GetName("Name") ?? "Identity") == "Identity";

        if (filter is PdfArray array && array.Count > 1)
        {
            array.Items.RemoveAt(0);
            if (parms is PdfArray parmArray && parmArray.Count > 0) parmArray.Items.RemoveAt(0);
        }
        else
        {
            dict.Remove("Filter");
            dict.Remove("DecodeParms");
        }

        return !identity;
    }

    private ReadOnlyMemory<byte> ReadStreamData(PdfParser parser, PdfDictionary dict, ReadOnlyMemory<byte> region)
    {
        ReadOnlySpan<byte> s = region.Span;
        int p = parser.Position;
        if (p < s.Length && s[p] == (byte)'\r') p++;
        if (p < s.Length && s[p] == (byte)'\n') p++;
        int dataStart = p;

        long declared = -1;
        PdfObject? lengthObj = dict["Length"];
        if (lengthObj is PdfNumber { IsInteger: true } ln) declared = ln.IntegerValue;
        else if (lengthObj is PdfReference lr && Resolve(lr) is PdfNumber { IsInteger: true } rn) declared = rn.IntegerValue;

        if (declared >= 0 && dataStart + declared <= s.Length && EndstreamFollows(s, (int)(dataStart + declared)))
        {
            return _data.Slice(dataStart, (int)declared);
        }

        // The declared length is missing or wrong. Recover it from the endstream keyword.
        int rel = s[dataStart..].IndexOf("endstream"u8);
        if (rel < 0) throw new PdfFormatException("Stream without endstream.");
        int end = dataStart + rel;
        if (end > dataStart && s[end - 1] == (byte)'\n') end--;
        if (end > dataStart && s[end - 1] == (byte)'\r') end--;
        if (!_loadingXref) LengthsRepaired++;
        return region[dataStart..end];
    }

    /// <summary>Streams whose /Length was wrong and had to be recovered from endstream.</summary>
    public int LengthsRepaired { get; private set; }

    private static bool EndstreamFollows(ReadOnlySpan<byte> s, int p)
    {
        while (p < s.Length && PdfSyntax.IsWhitespace(s[p])) p++;
        return s[p..].StartsWith("endstream"u8);
    }

    private PdfObject ReadFromObjectStream(int streamNumber, int index, int expectedNumber)
    {
        if (!_objectStreams.TryGetValue(streamNumber, out ObjectStreamIndex? os))
        {
            if (Resolve(new PdfReference(streamNumber, 0)) is not PdfStream st) throw new PdfFormatException("Object stream missing.");
            os = LoadObjectStream(streamNumber, st);
        }

        int i = index;
        if (i < 0 || i >= os.Numbers.Length || os.Numbers[i] != expectedNumber)
        {
            i = os.IndexOf(expectedNumber);
            if (i < 0) throw new PdfFormatException($"Object {expectedNumber} not found in object stream {streamNumber}.");
        }

        int start = os.First + os.Offsets[i];
        if (start < 0 || start >= os.Data.Length) throw new PdfFormatException("Object stream offset out of range.");
        var parser = new PdfParser(os.Data, start);
        return parser.ParseObject();
    }

    private ObjectStreamIndex LoadObjectStream(int number, PdfStream st)
    {
        PdfDictionary d = st.Dictionary;
        byte[] content;
        using (DecodedStream decoded = StreamCodec.Decode(st.Data, StreamCodec.GetFilters(d, Resolve), DecodeDepth.Full, _decodeLimit, Resolve))
        {
            if (decoded.Remaining.Count > 0 || decoded.PendingPredictor is not null) throw new PdfFormatException("Undecodable object stream.");
            content = decoded.Data.ToArray();
        }

        int n = IntOf(d["N"]);
        int first = IntOf(d["First"]);
        if (n > 1_000_000 || first > content.Length) throw new PdfFormatException("Invalid object stream header.");
        var parser = new PdfParser(content);
        var numbers = new int[n];
        var offsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (!parser.TryReadInteger(out long num) || !parser.TryReadInteger(out long off) || num < 0 || num > int.MaxValue || off < 0 || off > int.MaxValue)
            {
                throw new PdfFormatException("Invalid object stream header.");
            }

            numbers[i] = (int)num;
            offsets[i] = (int)off;
        }

        var index = new ObjectStreamIndex(content, first, numbers, offsets);

        // Every object stream decode counts against a per-document budget, so a file that forces
        // repeated decodes (many large streams, cache churn) fails instead of burning CPU.
        _objectStreamDecodedTotal += content.Length;
        if (_objectStreamDecodedTotal > 4 * _decodeLimit)
        {
            throw new PdfFormatException("Object streams exceed the decode budget for one document.");
        }

        // Decoded object streams are cached, but never more than one decode limit's worth;
        // past that the cache starts over and streams are decoded again on demand.
        if (_objectStreamCacheBytes + content.Length > _decodeLimit)
        {
            _objectStreams.Clear();
            _objectStreamCacheBytes = 0;
        }

        _objectStreams[number] = index;
        _objectStreamCacheBytes += content.Length;
        return index;
    }

    private sealed class ObjectStreamIndex(byte[] data, int first, int[] numbers, int[] offsets)
    {
        private Dictionary<int, int>? _lookup;

        public byte[] Data { get; } = data;

        public int First { get; } = first;

        public int[] Numbers { get; } = numbers;

        public int[] Offsets { get; } = offsets;

        public int IndexOf(int number)
        {
            if (_lookup is null)
            {
                _lookup = new Dictionary<int, int>(Numbers.Length);
                for (int i = 0; i < Numbers.Length; i++) _lookup.TryAdd(Numbers[i], i);
            }

            return _lookup.TryGetValue(number, out int index) ? index : -1;
        }
    }
}
