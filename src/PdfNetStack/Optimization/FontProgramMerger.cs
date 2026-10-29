using System.IO.Compression;
using PdfNetStack.Filters;
using PdfNetStack.Fonts;
using PdfNetStack.Fonts.OpenType;
using PdfNetStack.Objects;
using PdfNetStack.Parsing;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Optimization;

/// <summary>
/// Merges subsets of one TrueType font into a single program. A document assembled from several
/// sources often embeds the same font many times, each copy holding only the glyphs its part
/// used. When every copy keeps the original glyph ids (an identity CIDToGIDMap under an
/// Identity-H or Identity-V encoding), the copies can be replaced by one program holding the
/// union of their glyphs, provided every glyph each font shows keeps exactly its data and
/// metrics, and every table that affects rendering is the same in all of them.
/// </summary>
internal static class FontProgramMerger
{
    /// <summary>
    /// The FontFile2 streams this pass may change, with the glyph ids the fonts using each one
    /// can show: the codes from the content analysis, .notdef, and composite components.
    /// </summary>
    public static Dictionary<int, HashSet<int>> MergeablePrograms(PdfFile file, ContentAnalysis analysis, long decodeLimit)
    {
        var result = new Dictionary<int, HashSet<int>>();
        if (!analysis.Complete || !analysis.GlyphUsageComplete) return result;
        ReferenceIndex refs = analysis.References;

        // Program number -> (descriptor, codes) for every candidate font chain.
        var users = new Dictionary<int, List<(PdfDictionary Descriptor, HashSet<int> Codes)>>();
        var seenFonts = new HashSet<int>();
        foreach (PdfDictionary category in analysis.CategoryDictionaries("Font"))
        {
            foreach (KeyValuePair<string, PdfObject> entry in category)
            {
                if (entry.Value is not PdfReference fontRef || !seenFonts.Add(fontRef.Number)) continue;
                if (Chain(file, analysis, fontRef.Number) is not { } chain) continue;
                if (!users.TryGetValue(chain.Program, out var list))
                {
                    list = [];
                    users[chain.Program] = list;
                }

                HashSet<int> codes = analysis.IdentityCodes.TryGetValue(chain.Font, out HashSet<int>? shown) ? shown : [];
                list.Add((chain.Descriptor, codes));
            }
        }

        foreach ((int program, var list) in users)
        {
            // Every reference to the program must come from a descriptor of a candidate font.
            if (!refs.OnlyFrom(program, r => r.Key == "FontFile2" && list.Exists(u => ReferenceEquals(u.Descriptor, r.Container)))) continue;
            if (file.GetObject(program) is not PdfStream stream) continue;
            TrueTypeGlyphs? glyphs = TrueTypeGlyphs.Read(file, stream, decodeLimit);
            if (glyphs is null) continue;

            var used = new HashSet<int> { 0 };
            foreach ((_, HashSet<int> codes) in list) used.UnionWith(codes);
            result[program] = glyphs.Closure(used);
        }

        return result;
    }

    private sealed record FontChain(PdfDictionary Font, PdfDictionary Descriptor, int Program);

    /// <summary>
    /// Type0 font, its only descendant CIDFontType2, its descriptor and FontFile2 program, each
    /// reached only through the one before it. Null when any link is shared or of another kind.
    /// </summary>
    private static FontChain? Chain(PdfFile file, ContentAnalysis analysis, int fontNumber)
    {
        ReferenceIndex refs = analysis.References;
        if (file.GetObject(fontNumber) is not PdfDictionary font || font.GetName("Subtype") != "Type0") return null;
        if (font.GetName("Encoding") is not ("Identity-H" or "Identity-V")) return null;
        if (!analysis.FontFullyObserved(fontNumber) || analysis.UnreliableFonts.Contains(font)) return null;

        PdfObject? descendants = font["DescendantFonts"];
        if (file.Resolve(descendants) is not PdfArray { Count: 1 } array) return null;
        if (descendants is PdfReference dr && !refs.OnlyFrom(dr.Number, r => ReferenceEquals(r.Container, font) && r.Key == "DescendantFonts")) return null;
        if (array[0] is not PdfReference cidRef || file.GetObject(cidRef.Number) is not PdfDictionary cid) return null;
        if (!refs.OnlyFrom(cidRef.Number, r => ReferenceEquals(r.Container, array))) return null;
        if (cid.GetName("Subtype") != "CIDFontType2") return null;
        if (cid["CIDToGIDMap"] is { } map && file.Resolve(map) is not PdfName { Value: "Identity" }) return null;

        if (cid["FontDescriptor"] is not PdfReference descriptorRef || file.GetObject(descriptorRef.Number) is not PdfDictionary descriptor) return null;
        if (!refs.OnlyFrom(descriptorRef.Number, r => ReferenceEquals(r.Container, cid) && r.Key == "FontDescriptor")) return null;

        // A CIDSet lists the glyphs the program holds; a merged program would contradict it.
        if (descriptor["CIDSet"] is not null || descriptor["FontFile"] is not null || descriptor["FontFile3"] is not null) return null;
        if (descriptor["FontFile2"] is not PdfReference programRef) return null;
        return new FontChain(font, descriptor, programRef.Number);
    }

    /// <summary>
    /// Replaces each group of compatible programs by their union. Every member stream receives
    /// the same Flate encoded bytes, so deduplication then keeps one copy.
    /// </summary>
    /// <returns>Programs rewritten, and their object numbers.</returns>
    public static (int Merged, HashSet<int> Changed) Run(PdfFile file, ContentAnalysis analysis, CompressionLevel level, long decodeLimit, CancellationToken ct)
    {
        var changed = new HashSet<int>();
        Dictionary<int, HashSet<int>> programs = MergeablePrograms(file, analysis, decodeLimit);
        if (programs.Count < 2) return (0, changed);

        var groups = new Dictionary<string, List<(int Number, TrueTypeGlyphs Glyphs, HashSet<int> Used)>>(StringComparer.Ordinal);
        foreach ((int number, HashSet<int> used) in programs.OrderBy(p => p.Key))
        {
            ct.ThrowIfCancellationRequested();
            if (file.GetObject(number) is not PdfStream stream) continue;
            if (TrueTypeGlyphs.Read(file, stream, decodeLimit) is not { } glyphs) continue;
            string key = glyphs.CompatibilityKey();
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }

            group.Add((number, glyphs, used));
        }

        int merged = 0;
        foreach (var group in groups.Values)
        {
            if (group.Count < 2) continue;
            var unions = new List<GlyphUnion>();
            foreach ((int number, TrueTypeGlyphs glyphs, HashSet<int> used) in group)
            {
                GlyphUnion? home = unions.Find(u => u.TryAdd(number, glyphs, used));
                if (home is null) unions.Add(new GlyphUnion(number, glyphs, used));
            }

            foreach (GlyphUnion union in unions)
            {
                if (union.Members.Count < 2) continue;
                ct.ThrowIfCancellationRequested();
                byte[] program = union.Build();
                byte[] encoded = StreamCodec.Deflate(program, level);
                foreach (int number in union.Members)
                {
                    var stream = (PdfStream)file.GetObject(number);
                    stream.Data = encoded;
                    stream.Dictionary.Set("Filter", new PdfName("FlateDecode"));
                    stream.Dictionary.Remove("DecodeParms");
                    stream.Dictionary.Remove("DL");
                    stream.Dictionary.Set("Length1", PdfNumber.FromInteger(program.Length));
                    changed.Add(number);
                }

                merged += union.Members.Count;
            }
        }

        return (merged, changed);
    }

    /// <summary>Programs merged so far and the glyph slots they define.</summary>
    private sealed class GlyphUnion
    {
        private readonly List<(TrueTypeGlyphs Glyphs, HashSet<int> Used)> _members = [];

        public GlyphUnion(int number, TrueTypeGlyphs glyphs, HashSet<int> used)
        {
            _members.Add((glyphs, used));
            Members.Add(number);
        }

        public List<int> Members { get; } = [];

        private int Count => _members.Max(m => m.Glyphs.GlyphCount);

        /// <summary>Adds the program when no glyph any member shows would change.</summary>
        public bool TryAdd(int number, TrueTypeGlyphs glyphs, HashSet<int> used)
        {
            var candidate = new List<(TrueTypeGlyphs Glyphs, HashSet<int> Used)>(_members) { (glyphs, used) };
            int count = candidate.Max(m => m.Glyphs.GlyphCount);
            foreach ((TrueTypeGlyphs g, HashSet<int> u) in candidate)
            {
                foreach (int gid in u)
                {
                    if (!TrueTypeGlyphs.SameSlot(g.Slot(gid), Slot(candidate, count, gid))) return false;
                }
            }

            _members.Add((glyphs, used));
            Members.Add(number);
            return true;
        }

        /// <summary>
        /// The slot the members that show <paramref name="gid"/> agree on, or a conflict marker
        /// when two of them disagree. A glyph no member shows takes the first outline any member
        /// has; what a member holds for a glyph it never shows does not matter.
        /// </summary>
        private static GlyphSlot Slot(List<(TrueTypeGlyphs Glyphs, HashSet<int> Used)> members, int count, int gid)
        {
            GlyphSlot? found = null;
            foreach ((TrueTypeGlyphs g, HashSet<int> used) in members)
            {
                if (!used.Contains(gid)) continue;
                GlyphSlot slot = g.Slot(gid);
                if (found is { } f && !TrueTypeGlyphs.SameSlot(f, slot)) return GlyphSlot.Conflict;
                found ??= slot;
            }

            // A glyph id past the end of a member's program cannot stay so in a longer union.
            if (found is { } shown) return shown.InRange || gid >= count ? shown : GlyphSlot.Conflict;
            foreach ((TrueTypeGlyphs g, _) in members)
            {
                GlyphSlot slot = g.Slot(gid);
                if (slot.Defined && !slot.Data.IsEmpty) return slot;
            }

            return gid < count ? GlyphSlot.Empty : GlyphSlot.OutOfRange;
        }

        public byte[] Build()
        {
            int count = Count;
            var slots = new GlyphSlot[count];
            for (int gid = 0; gid < count; gid++) slots[gid] = Slot(_members, count, gid);
            return _members[0].Glyphs.Rebuild(slots, _members.Select(m => m.Glyphs).ToList());
        }
    }
}

/// <summary>One glyph id of a TrueType program: its glyf bytes and horizontal metrics.</summary>
internal readonly record struct GlyphSlot(bool InRange, ReadOnlyMemory<byte> Data, int Advance, int SideBearing, bool IsConflict = false)
{
    public static readonly GlyphSlot Empty = new(true, ReadOnlyMemory<byte>.Empty, 0, 0);

    public static readonly GlyphSlot OutOfRange = new(false, ReadOnlyMemory<byte>.Empty, 0, 0);

    public static readonly GlyphSlot Conflict = new(false, ReadOnlyMemory<byte>.Empty, 0, 0, IsConflict: true);

    /// <summary>A slot that carries something: an outline or metrics. Subsets leave unused slots empty.</summary>
    public bool Defined => InRange && (!Data.IsEmpty || Advance != 0 || SideBearing != 0);
}

/// <summary>
/// A TrueType program read for merging: its tables, glyph slots and the header fields a merge
/// must keep. Also the check the verifier applies to a merged program.
/// </summary>
internal sealed class TrueTypeGlyphs
{
    /// <summary>Tables a merge rebuilds or that a CIDFontType2 with an identity map never reads.</summary>
    private static readonly HashSet<string> Rebuilt = new(StringComparer.Ordinal)
    {
        "glyf", "loca", "hmtx", "hhea", "maxp", "head", "post", "name", "cmap",
    };

    private readonly OpenTypeFile _font;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;

    private TrueTypeGlyphs(OpenTypeFile font, Dictionary<string, (int Offset, int Length)> tables)
    {
        _font = font;
        _tables = tables;
    }

    public int GlyphCount => _font.GlyphCount;

    /// <summary>Decodes and reads a FontFile2 stream. Null when it is not a well formed TrueType program.</summary>
    public static TrueTypeGlyphs? Read(PdfFile file, PdfStream stream, long decodeLimit)
    {
        try
        {
            List<FilterSpec> filters = StreamCodec.GetFilters(stream.Dictionary, file.Resolve);
            using DecodedStream decoded = StreamCodec.Decode(stream.Data, filters, DecodeDepth.Full, decodeLimit, file.Resolve);
            if (decoded.Remaining.Count > 0 || decoded.PendingPredictor is not null) return null;
            return Parse(decoded.Data.ToArray());
        }
        catch (StreamDecodeException)
        {
            return null;
        }
    }

    public static TrueTypeGlyphs? Parse(byte[] data)
    {
        try
        {
            if (data.Length < 12 || U32(data, 0) is not (0x00010000 or 0x74727565)) return null;
            OpenTypeFile font = OpenTypeFile.ParseSubset(data);
            if (font.IsCff) return null;
            var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            int count = U16(data, 4);
            for (int i = 0; i < count; i++)
            {
                int record = 12 + 16 * i;
                Check(data, record, 16);
                int offset = Offset32(data, record + 8), length = Offset32(data, record + 12);
                Check(data, offset, length);
                if (!tables.TryAdd(Tag(data, record), (offset, length))) return null;
            }

            if (!tables.TryGetValue("head", out (int, int Length) head) || head.Length < 54) return null;
            if (!tables.TryGetValue("hhea", out (int, int Length) hhea) || hhea.Length < 36) return null;
            if (!tables.TryGetValue("maxp", out (int, int Length) maxp) || maxp.Length < 6) return null;

            // Every glyph's range must be readable before anything relies on it.
            for (int g = 0; g < font.GlyphCount; g++) font.GlyphRange(g);
            return new TrueTypeGlyphs(font, tables);
        }
        catch (Exception ex) when (ex is FontFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or KeyNotFoundException)
        {
            return null;
        }
    }

    private ReadOnlySpan<byte> Table(string tag) =>
        _tables.TryGetValue(tag, out (int Offset, int Length) t) ? _font.Data.AsSpan(t.Offset, t.Length) : default;

    /// <summary>The glyph's bytes and metrics, or <see cref="GlyphSlot.OutOfRange"/> past the last glyph.</summary>
    public GlyphSlot Slot(int gid)
    {
        if (gid < 0 || gid >= GlyphCount) return GlyphSlot.OutOfRange;
        (int start, int end) = _font.GlyphRange(gid);
        ReadOnlyMemory<byte> data = _font.Data.AsMemory(_tables["glyf"].Offset + start, end - start);
        ReadOnlySpan<byte> hmtx = Table("hmtx");
        int full = _font.NumberOfHMetrics;
        int lsbAt = gid < full ? 4 * gid + 2 : 4 * full + 2 * (gid - full);
        int lsb = lsbAt + 2 <= hmtx.Length ? I16(hmtx, lsbAt) : 0;
        return new GlyphSlot(true, data, _font.Advance(gid), lsb);
    }

    /// <summary>
    /// Equal slots. Glyph data may be followed by zero padding, which rasterizers never read:
    /// the longer of the two must start with the shorter and continue with zeros only.
    /// </summary>
    public static bool SameSlot(GlyphSlot a, GlyphSlot b)
    {
        if (a.IsConflict || b.IsConflict) return false;
        if (a.InRange != b.InRange) return false;
        if (!a.InRange) return true;
        if (a.Advance != b.Advance || a.SideBearing != b.SideBearing) return false;
        ReadOnlySpan<byte> x = a.Data.Span, y = b.Data.Span;
        if (x.Length > y.Length)
        {
            ReadOnlySpan<byte> t = x;
            x = y;
            y = t;
        }

        if (!y[..x.Length].SequenceEqual(x)) return false;
        foreach (byte padding in y[x.Length..])
        {
            if (padding != 0) return false;
        }

        // An empty glyph and a zero padded one differ: only real data may carry padding.
        return x.Length > 0 || y.Length == 0;
    }

    /// <summary>The glyphs, .notdef and every component of the composite ones among them, transitively.</summary>
    public HashSet<int> Closure(HashSet<int> glyphs)
    {
        var result = new HashSet<int>(glyphs.Where(g => g >= GlyphCount || g < 0));
        result.UnionWith(TrueTypeSubsetter.Closure(_font, glyphs.Where(g => g >= 0 && g < GlyphCount)));
        return result;
    }

    /// <summary>
    /// Identifies programs a merge may combine: every table outside <see cref="Rebuilt"/>, byte
    /// for byte, and the header fields that steer scaling and hinting.
    /// </summary>
    public string CompatibilityKey()
    {
        var sink = new Writing.ByteSink(256);
        foreach (string tag in _tables.Keys.Where(t => !Rebuilt.Contains(t)).OrderBy(t => t, StringComparer.Ordinal))
        {
            sink.WriteAscii(tag);
            sink.Write(Compat.Sha256(Table(tag)));
        }

        sink.Write(FixedFields(this));
        return Compat.ToHex(Compat.Sha256(sink.WrittenSpan));
    }

    /// <summary>head, hhea and maxp fields a merge must keep: version, revision, flags, units per em, style, vertical metrics, hinting limits' version.</summary>
    private static byte[] FixedFields(TrueTypeGlyphs f)
    {
        ReadOnlySpan<byte> head = f.Table("head"), hhea = f.Table("hhea"), maxp = f.Table("maxp");
        return [.. head[..8], .. head[16..20], .. head[44..50], .. head[52..54], .. hhea[..10], .. hhea[18..24], .. hhea[32..34], .. maxp[..4]];
    }

    /// <summary>
    /// The merged program: these tables, with glyf, loca, hmtx and the counts in hhea and maxp
    /// rebuilt from <paramref name="slots"/>, the font box and hinting limits widened to cover
    /// every member, and post reduced to format 3.
    /// </summary>
    public byte[] Rebuild(GlyphSlot[] slots, List<TrueTypeGlyphs> members)
    {
        int count = slots.Length;
        var glyf = new FontWriter(1024);
        var loca = new FontWriter(4 * (count + 1));
        var hmtx = new FontWriter(4 * count);
        int advanceMax = 0;
        for (int gid = 0; gid < count; gid++)
        {
            loca.U32((uint)glyf.Length);
            GlyphSlot slot = slots[gid];
            if (slot.Defined)
            {
                glyf.Bytes(slot.Data.Span);
                glyf.Pad(4);
            }

            int advance = slot.Defined ? slot.Advance : 0;
            advanceMax = Math.Max(advanceMax, advance);
            hmtx.U16(advance);
            hmtx.U16((ushort)(slot.Defined ? slot.SideBearing : 0));
        }

        loca.U32((uint)glyf.Length);

        byte[] head = Table("head").ToArray();
        short xMin = members.Min(m => I16(m.Table("head"), 36)), yMin = members.Min(m => I16(m.Table("head"), 38));
        short xMax = members.Max(m => I16(m.Table("head"), 40)), yMax = members.Max(m => I16(m.Table("head"), 42));
        SetI16(head, 36, xMin);
        SetI16(head, 38, yMin);
        SetI16(head, 40, xMax);
        SetI16(head, 42, yMax);
        head[50] = 0;
        head[51] = 1; // indexToLocFormat: long offsets

        byte[] hhea = Table("hhea").ToArray();
        SetI16(hhea, 10, (short)Math.Min(advanceMax, ushort.MaxValue));
        SetI16(hhea, 12, members.Min(m => I16(m.Table("hhea"), 12)));
        SetI16(hhea, 14, members.Min(m => I16(m.Table("hhea"), 14)));
        SetI16(hhea, 16, members.Max(m => I16(m.Table("hhea"), 16)));
        hhea[34] = (byte)(count >> 8);
        hhea[35] = (byte)count;

        byte[] maxp = Table("maxp").ToArray();
        maxp[4] = (byte)(count >> 8);
        maxp[5] = (byte)count;
        for (int field = 6; field + 2 <= maxp.Length && field < 32; field += 2)
        {
            int widest = members.Max(m => m.Table("maxp").Length >= field + 2 ? U16(m.Table("maxp"), field) : 0);
            maxp[field] = (byte)(widest >> 8);
            maxp[field + 1] = (byte)widest;
        }

        var tables = new List<(string Tag, uint Checksum, byte[] Data)>
        {
            ("head", 0, head),
            ("hhea", 0, hhea),
            ("hmtx", 0, hmtx.ToArray()),
            ("maxp", 0, maxp),
            ("loca", 0, loca.ToArray()),
            ("glyf", 0, glyf.ToArray()),
        };

        ReadOnlySpan<byte> post = Table("post");
        if (post.Length >= 32)
        {
            byte[] p = post[..32].ToArray();
            p[0] = 0;
            p[1] = 3;
            p[2] = 0;
            p[3] = 0;
            tables.Add(("post", 0, p));
        }

        if (_tables.ContainsKey("name")) tables.Add(("name", 0, Table("name").ToArray()));

        // Glyphs are selected by id, but some font loaders refuse a TrueType program with no cmap.
        if (members.Find(m => m._tables.ContainsKey("cmap")) is { } withCmap) tables.Add(("cmap", 0, withCmap.Table("cmap").ToArray()));
        foreach (string tag in _tables.Keys.Where(t => !Rebuilt.Contains(t))) tables.Add((tag, 0, Table(tag).ToArray()));
        return SfntBuilder.Build(0x00010000, tables);
    }

    private static void SetI16(byte[] data, int offset, short value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    /// <summary>
    /// Null when <paramref name="candidate"/> renders every glyph in <paramref name="glyphs"/>
    /// exactly as <paramref name="original"/> does: the same tables outside the rebuilt ones,
    /// the same scaling and hinting fields, hinting limits at least as wide, and for each glyph
    /// the same outline bytes (up to zero padding), advance and side bearing.
    /// </summary>
    public static string? FindDifference(ReadOnlySpan<byte> original, ReadOnlySpan<byte> candidate, HashSet<int> glyphs)
    {
        TrueTypeGlyphs? a = Parse(original.ToArray());
        TrueTypeGlyphs? b = Parse(candidate.ToArray());
        if (a is null || b is null) return "is not a readable TrueType program";

        var tagsA = a._tables.Keys.Where(t => !Rebuilt.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var tagsB = b._tables.Keys.Where(t => !Rebuilt.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (!tagsA.SequenceEqual(tagsB)) return "has a different set of tables";
        foreach (string tag in tagsA)
        {
            if (!a.Table(tag).SequenceEqual(b.Table(tag))) return $"table '{tag}' differs";
        }

        if (!FixedFields(a).AsSpan().SequenceEqual(FixedFields(b))) return "header fields differ";
        ReadOnlySpan<byte> maxpA = a.Table("maxp"), maxpB = b.Table("maxp");
        for (int field = 6; field + 2 <= maxpA.Length && field < 32; field += 2)
        {
            if (field + 2 > maxpB.Length || U16(maxpB, field) < U16(maxpA, field)) return "hinting limits are narrower";
        }

        foreach (int gid in glyphs)
        {
            if (!SameSlot(a.Slot(gid), b.Slot(gid))) return $"glyph {gid} differs";
        }

        return null;
    }
}
