using System.IO.Compression;
using System.Text;
using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// One font of an OpenType file: TrueType outlines (glyf) or CFF outlines, from a .ttf, .otf, a
/// collection (.ttc, .otc) or a WOFF file. Reads what layout and embedding need: metrics, names,
/// the Unicode cmap and advance widths.
/// </summary>
internal sealed class OpenTypeFile
{
    /// <summary>Total size a WOFF file may decompress to.</summary>
    private const int MaxWoffSize = 128 << 20;

    /// <summary>Cmap entries to visit at most. Valid cmaps stay far below; overlapping ranges could not.</summary>
    private const int MaxCmapWork = 4 << 20;

    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _cmap;
    private readonly int _numberOfHMetrics;

    private OpenTypeFile(byte[] data, int sfntOffset, bool requireCmap = true)
    {
        Data = data;
        ReadOnlySpan<byte> d = data;
        uint version = U32(d, sfntOffset);
        if (version is not (0x00010000 or 0x74727565 /* true */ or 0x4F54544F /* OTTO */))
        {
            throw new FontFormatException("Not a TrueType or OpenType font.");
        }

        int count = U16(d, sfntOffset + 4);
        for (int i = 0; i < count; i++)
        {
            int record = sfntOffset + 12 + 16 * i;
            string tag = Tag(d, record);
            int offset = Offset32(d, record + 8), length = Offset32(d, record + 12);
            Check(d, offset, length);
            _tables[tag] = (offset, length);
        }

        foreach (string required in new[] { "head", "hhea", "hmtx", "maxp", "cmap" })
        {
            if (!_tables.ContainsKey(required) && (requireCmap || required != "cmap")) throw new FontFormatException($"Font has no '{required}' table.");
        }

        IsCff = _tables.ContainsKey("CFF ");
        if (!IsCff && (!_tables.ContainsKey("glyf") || !_tables.ContainsKey("loca")))
        {
            throw new FontFormatException(_tables.ContainsKey("CFF2")
                ? "CFF2 (variable OpenType) fonts are not supported."
                : "Font has neither TrueType nor CFF outlines.");
        }

        ReadOnlySpan<byte> head = Table("head");
        UnitsPerEm = U16(head, 18);
        if (UnitsPerEm is < 16 or > 16384) throw new FontFormatException("Font units per em are out of range.");
        BBox = [I16(head, 36), I16(head, 38), I16(head, 40), I16(head, 42)];
        MacStyle = U16(head, 44);
        LongLoca = I16(head, 50) != 0;

        GlyphCount = U16(Table("maxp"), 4);
        if (GlyphCount == 0) throw new FontFormatException("Font has no glyphs.");

        ReadOnlySpan<byte> hhea = Table("hhea");
        HheaAscender = I16(hhea, 4);
        HheaDescender = I16(hhea, 6);
        HheaLineGap = I16(hhea, 8);
        _numberOfHMetrics = U16(hhea, 34);
        if (_numberOfHMetrics == 0 || _numberOfHMetrics > GlyphCount) throw new FontFormatException("Font hhea table is invalid.");
        Check(Table("hmtx"), 0, 4 * _numberOfHMetrics);

        ReadOnlySpan<byte> os2 = Table("OS/2");
        if (os2.Length >= 78)
        {
            HasOs2 = true;
            WeightClass = U16(os2, 4);
            FsType = U16(os2, 8);
            StrikeoutSize = I16(os2, 26);
            StrikeoutPosition = I16(os2, 28);
            FsSelection = U16(os2, 62);
            TypoAscender = I16(os2, 68);
            TypoDescender = I16(os2, 70);
            TypoLineGap = I16(os2, 72);
            WinAscent = U16(os2, 74);
            WinDescent = U16(os2, 76);
            if (U16(os2, 0) >= 2 && os2.Length >= 90)
            {
                XHeight = I16(os2, 86);
                CapHeight = I16(os2, 88);
            }
        }

        ReadOnlySpan<byte> post = Table("post");
        if (post.Length >= 16)
        {
            ItalicAngle = (int)U32(post, 4) / 65536.0;
            UnderlinePosition = I16(post, 8);
            UnderlineThickness = I16(post, 10);
            IsFixedPitch = U32(post, 12) != 0;
        }

        FamilyName = Name(16) ?? Name(1) ?? "Unknown";
        SubfamilyName = Name(17) ?? Name(2) ?? "Regular";
        PostScriptName = SanitizePostScriptName(Name(6) ?? Name(4) ?? FamilyName + "-" + SubfamilyName);
        _cmap = HasTable("cmap") ? ReadCmap() : new Dictionary<int, int>();
    }

    /// <summary>The file bytes. For a collection, the whole collection: table offsets are absolute.</summary>
    public byte[] Data { get; }

    public bool IsCff { get; }

    public int UnitsPerEm { get; }

    public int GlyphCount { get; }

    public short[] BBox { get; }

    public int MacStyle { get; }

    public bool LongLoca { get; }

    public short HheaAscender { get; }

    public short HheaDescender { get; }

    public short HheaLineGap { get; }

    public bool HasOs2 { get; }

    public int WeightClass { get; } = 400;

    public int FsType { get; }

    public int FsSelection { get; }

    public short TypoAscender { get; }

    public short TypoDescender { get; }

    public short TypoLineGap { get; }

    public int WinAscent { get; }

    public int WinDescent { get; }

    public short XHeight { get; }

    public short CapHeight { get; }

    public short StrikeoutSize { get; }

    public short StrikeoutPosition { get; }

    public double ItalicAngle { get; }

    public short UnderlinePosition { get; }

    public short UnderlineThickness { get; }

    public bool IsFixedPitch { get; }

    public string FamilyName { get; }

    public string SubfamilyName { get; }

    public string PostScriptName { get; }

    /// <summary>Number of fonts in a file: the count of a collection, otherwise 1.</summary>
    public static int CountFonts(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && Tag(data, 0) == "ttcf" ? (int)Math.Min(U32(data, 8), 0xFFFF) : 1;

    /// <summary>Parses font <paramref name="index"/> of a .ttf, .otf, .ttc, .otc or .woff file.</summary>
    /// <exception cref="FontFormatException">The data is not a supported font.</exception>
    public static OpenTypeFile Parse(byte[] data, int index)
    {
        if (data.Length < 12) throw new FontFormatException("Font data is truncated.");
        string signature = Tag(data, 0);
        switch (signature)
        {
            case "wOF2":
                throw new FontFormatException("WOFF2 fonts are not supported yet. Use the TTF, OTF or WOFF version of the font.");
            case "wOFF":
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index), "A WOFF file holds one font.");
                return new OpenTypeFile(DecodeWoff(data), 0);
            case "ttcf":
                int count = CountFonts(data);
                if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index), $"The collection holds {count} fonts.");
                return new OpenTypeFile(data, Offset32(data, 12 + 4 * index));
            default:
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index), "The file holds one font.");
                return new OpenTypeFile(data, 0);
        }
    }

    /// <summary>Number of full (advance and side bearing) entries in hmtx.</summary>
    public int NumberOfHMetrics => _numberOfHMetrics;

    /// <summary>Reads back an embedded subset, which has no cmap: PDF maps codes to glyphs itself.</summary>
    public static OpenTypeFile ParseSubset(byte[] data) => new(data, 0, requireCmap: false);

    public bool HasTable(string tag) => _tables.ContainsKey(tag);

    /// <summary>Offset of a table in <see cref="Data"/>.</summary>
    public int TableOffset(string tag) => _tables[tag].Offset;

    /// <summary>The table's bytes, or an empty span when the font has no such table.</summary>
    public ReadOnlySpan<byte> Table(string tag) =>
        _tables.TryGetValue(tag, out (int Offset, int Length) t) ? Data.AsSpan(t.Offset, t.Length) : default;

    public bool TryGetGlyph(int codePoint, out int glyph) => _cmap.TryGetValue(codePoint, out glyph);

    /// <summary>Advance width of a glyph in font units.</summary>
    public int Advance(int glyph)
    {
        ReadOnlySpan<byte> hmtx = Table("hmtx");
        return U16(hmtx, 4 * Math.Min(glyph, _numberOfHMetrics - 1));
    }

    /// <summary>The yMax of a TrueType glyph's bounding box, or null for CFF fonts and empty glyphs.</summary>
    public int? GlyphTop(int glyph)
    {
        if (IsCff || glyph < 0 || glyph >= GlyphCount) return null;
        (int start, int end) = GlyphRange(glyph);
        return end - start >= 10 ? I16(Table("glyf"), start + 8) : null;
    }

    /// <summary>Byte range of a glyph in the glyf table, from loca.</summary>
    public (int Start, int End) GlyphRange(int glyph)
    {
        ReadOnlySpan<byte> loca = Table("loca");
        int start = LongLoca ? Offset32(loca, 4 * glyph) : U16(loca, 2 * glyph) * 2;
        int end = LongLoca ? Offset32(loca, 4 * glyph + 4) : U16(loca, 2 * glyph + 2) * 2;
        int glyf = Table("glyf").Length;
        if (start > end || end > glyf) throw new FontFormatException("Font loca table is invalid.");
        return (start, end);
    }

    private string? Name(int nameId) => ReadName(Table("name"), nameId);

    /// <summary>
    /// A string from a 'name' table: the US English Windows entry, else any Windows, Unicode or
    /// Macintosh Roman entry. Null when the font has none.
    /// </summary>
    public static string? ReadName(ReadOnlySpan<byte> name, int nameId)
    {
        if (name.Length < 6) return null;
        int count = U16(name, 2), storage = U16(name, 4);
        string? mac = null, unicode = null, windows = null;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + 12 * i;
            if (r + 12 > name.Length) break;
            if (U16(name, r + 6) != nameId) continue;
            int platform = U16(name, r), encoding = U16(name, r + 2), language = U16(name, r + 4);
            int length = U16(name, r + 8), offset = storage + U16(name, r + 10);
            if (offset + length > name.Length || length == 0) continue;
            ReadOnlySpan<byte> s = name.Slice(offset, length);
            switch (platform)
            {
                case 3 when encoding is 0 or 1 or 10:
                    if (windows is null || language == 0x409) windows = Utf16(s);
                    break;
                case 0:
                    unicode ??= Utf16(s);
                    break;
                case 1 when encoding == 0 && language == 0:
                    mac ??= Encoding.ASCII.GetString(s.ToArray());
                    break;
            }
        }

        string? result = windows ?? unicode ?? mac;
        return string.IsNullOrWhiteSpace(result) ? null : result!.Trim();

        static string Utf16(ReadOnlySpan<byte> s) => Encoding.BigEndianUnicode.GetString(s.ToArray());
    }

    /// <summary>PostScript names are printable ASCII without PDF delimiters or spaces.</summary>
    private static string SanitizePostScriptName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is > ' ' and < (char)0x7F && "()<>[]{}/%#".IndexOf(c) < 0) sb.Append(c);
        }

        return sb.Length == 0 ? "Font" : sb.ToString(0, Math.Min(sb.Length, 63));
    }

    /// <summary>
    /// Reads the best Unicode subtable: full repertoire (3,10 or 0,4/0,6) first, then the BMP
    /// (3,1 or 0,3), then a symbol font's (3,0), whose codes U+F020 to U+F0FF also answer for
    /// U+0020 to U+00FF as Windows does.
    /// </summary>
    private Dictionary<int, int> ReadCmap()
    {
        ReadOnlySpan<byte> cmap = Table("cmap");
        int count = U16(cmap, 2);
        int best = -1, bestRank = int.MaxValue;
        bool symbol = false;
        for (int i = 0; i < count; i++)
        {
            int r = 4 + 8 * i;
            int platform = U16(cmap, r), encoding = U16(cmap, r + 2);
            int offset = Offset32(cmap, r + 4);
            int format = U16(cmap, offset);
            int rank = (platform, encoding, format) switch
            {
                (3, 10, 12) or (0, 4, 12) or (0, 6, 12) => 0,
                (0, _, 12) => 1,
                (3, 1, 4) => 2,
                (0, _, 4) => 3,
                (3, 0, 4) => 4,
                (1, 0, 0 or 6 or 4) => 5,
                _ => int.MaxValue,
            };
            if (rank < bestRank)
            {
                best = offset;
                bestRank = rank;
                symbol = platform == 3 && encoding == 0;
            }
        }

        if (best < 0) throw new FontFormatException("Font has no Unicode cmap.");
        var map = new Dictionary<int, int>();
        int work = 0;
        ReadOnlySpan<byte> sub = cmap.Slice(best);
        switch (U16(sub, 0))
        {
            case 0:
                for (int c = 0; c < 256; c++) Add(map, c, U8(sub, 6 + c));
                break;
            case 4:
                ReadFormat4(sub, map);
                break;
            case 6:
                int first = U16(sub, 6), entries = U16(sub, 8);
                for (int i = 0; i < entries; i++) Add(map, first + i, U16(sub, 10 + 2 * i));
                break;
            case 12:
                ReadFormat12(sub, map);
                break;
        }

        if (symbol)
        {
            for (int c = 0x20; c <= 0xFF; c++)
            {
                if (!map.ContainsKey(c) && map.TryGetValue(0xF000 + c, out int g)) map[c] = g;
            }
        }

        return map;

        void Add(Dictionary<int, int> m, int codePoint, int glyph)
        {
            if (++work > MaxCmapWork) throw new FontFormatException("Font cmap is too large.");
            if (glyph > 0 && glyph < GlyphCount) m[codePoint] = glyph;
        }

        void ReadFormat4(ReadOnlySpan<byte> s, Dictionary<int, int> m)
        {
            int segments = U16(s, 6) / 2;
            int ends = 14, starts = ends + 2 * segments + 2, deltas = starts + 2 * segments, ranges = deltas + 2 * segments;
            Check(s, 0, ranges + 2 * segments);
            for (int i = 0; i < segments; i++)
            {
                int end = U16(s, ends + 2 * i), start = U16(s, starts + 2 * i);
                int delta = U16(s, deltas + 2 * i), range = U16(s, ranges + 2 * i);
                if (start > end) continue;
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph;
                    if (range == 0)
                    {
                        glyph = (c + delta) & 0xFFFF;
                    }
                    else
                    {
                        int at = ranges + 2 * i + range + 2 * (c - start);
                        if (at + 2 > s.Length) break;
                        glyph = U16(s, at);
                        if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                    }

                    Add(m, c, glyph);
                }
            }
        }

        void ReadFormat12(ReadOnlySpan<byte> s, Dictionary<int, int> m)
        {
            long groups = U32(s, 12);
            if (groups > (s.Length - 16) / 12) throw new FontFormatException("Font cmap is truncated.");
            for (int i = 0; i < groups; i++)
            {
                int g = 16 + 12 * i;
                uint start = U32(s, g), end = U32(s, g + 4), glyph = U32(s, g + 8);
                if (start > end || end > 0x10FFFF) continue;
                for (uint c = start; c <= end && glyph + (c - start) < GlyphCount; c++) Add(m, (int)c, (int)(glyph + (c - start)));
            }
        }
    }

    /// <summary>Rebuilds the sfnt of a WOFF 1.0 file: each table is stored as is or zlib compressed.</summary>
    private static byte[] DecodeWoff(byte[] data)
    {
        ReadOnlySpan<byte> d = data;
        uint flavor = U32(d, 4);
        int count = U16(d, 12);
        int sfntSize = Offset32(d, 16);
        if (sfntSize > MaxWoffSize) throw new FontFormatException("WOFF font is too large.");

        var tables = new List<(string Tag, uint Checksum, byte[] Data)>(count);
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            int r = 44 + 20 * i;
            string tag = Tag(d, r);
            int offset = Offset32(d, r + 4), compressed = Offset32(d, r + 8), original = Offset32(d, r + 12);
            uint checksum = U32(d, r + 16);
            total += original;
            if (total > sfntSize || compressed > original) throw new FontFormatException("WOFF table sizes are invalid.");
            ReadOnlySpan<byte> stored = Slice(d, offset, compressed);
            byte[] table;
            if (compressed == original)
            {
                table = stored.ToArray();
            }
            else
            {
                // zlib: a two byte header, then raw deflate.
                if (compressed < 2) throw new FontFormatException("WOFF table is truncated.");
                table = new byte[original];
                try
                {
                    using var inflate = new DeflateStream(new MemoryStream(data, offset + 2, compressed - 2), CompressionMode.Decompress);
                    int read = 0;
                    while (read < original)
                    {
                        int n = inflate.Read(table, read, original - read);
                        if (n == 0) throw new FontFormatException("WOFF table is truncated.");
                        read += n;
                    }

                    if (inflate.ReadByte() >= 0) throw new FontFormatException("WOFF table is longer than declared.");
                }
                catch (InvalidDataException e)
                {
                    throw new FontFormatException("WOFF table data is damaged.", e);
                }
            }

            tables.Add((tag, checksum, table));
        }

        return SfntBuilder.Build(flavor, tables);
    }
}

/// <summary>Writes an sfnt: header, sorted table records, then the 4 byte aligned tables.</summary>
internal static class SfntBuilder
{
    /// <summary>Builds the font. A 'head' table gets its checkSumAdjustment set.</summary>
    public static byte[] Build(uint version, List<(string Tag, uint Checksum, byte[] Data)> tables)
    {
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));
        int count = tables.Count;
        int power = 1, log = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            log++;
        }

        var w = new FontWriter(12 + 16 * count + tables.Sum(t => t.Data.Length + 3));
        w.U32(version);
        w.U16(count);
        w.U16(power * 16);
        w.U16(log);
        w.U16(count * 16 - power * 16);
        int offset = 12 + 16 * count;
        int headOffset = -1;
        foreach ((string tag, _, byte[] data) in tables)
        {
            foreach (char c in tag) w.U8(c);
            if (tag == "head")
            {
                headOffset = offset;
                Array.Clear(data, 8, 4);
            }

            w.U32(FontData.Checksum(data));
            w.U32((uint)offset);
            w.U32((uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }

        foreach ((_, _, byte[] data) in tables)
        {
            w.Bytes(data);
            w.Pad(4);
        }

        if (headOffset >= 0) w.SetU32(headOffset + 8, 0xB1B0AFBA - FontData.Checksum(w.Written));
        return w.ToArray();
    }
}
