using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// Rebuilds the sfnt of a WOFF2 file (W3C WOFF File Format 2.0). One Brotli stream holds every
/// table; glyf and loca (transform 0) and hmtx (transform 1) may be stored transformed and are
/// rebuilt here. A collection (flavor 'ttcf') becomes a TTC. Table checksums and
/// head.checkSumAdjustment are recomputed.
/// </summary>
internal static class Woff2
{
    private const uint Collection = 0x74746366; // 'ttcf'

    // Section 4.1, Known Table Tags: flag values 0 to 62.
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    private sealed class Entry
    {
        public string Tag = "";
        public bool Transformed;
        public int OriginalLength;
        public int Length;
        public int Offset;
        public byte[]? Data;
    }

    private sealed class Directory
    {
        public uint Flavor;
        public readonly List<Entry> Tables = [];
        public readonly List<(uint Flavor, int[] Tables)> Fonts = [];
        public int DataOffset;
        public int CompressedLength;
        public int TotalLength;
    }

    /// <summary>Number of fonts in a WOFF2 file, read from its directories: the collection size, or 1.</summary>
    public static int CountFonts(ReadOnlySpan<byte> data, int maxSize)
    {
        try
        {
            return ReadDirectory(data, maxSize).Fonts.Count;
        }
        catch (FontFormatException)
        {
            return 1;
        }
    }

    /// <summary>The sfnt (or TTC, for a collection) inside a WOFF2 file.</summary>
    /// <param name="maxSize">Limit on the decompressed and on the rebuilt font data.</param>
    /// <param name="managedBrotli">Decompress with <see cref="ManagedBrotli"/> even where .NET has a decoder, for tests.</param>
    /// <param name="dictionary">The Brotli dictionary for <paramref name="managedBrotli"/>.</param>
    /// <exception cref="FontFormatException">The file is damaged or uses an unknown transform.</exception>
    public static byte[] Decode(byte[] data, int maxSize, bool managedBrotli = false, byte[]? dictionary = null)
    {
        Directory directory = ReadDirectory(data, maxSize);
        byte[] block;
        try
        {
            ReadOnlySpan<byte> compressed = data.AsSpan(directory.DataOffset, directory.CompressedLength);
            block = managedBrotli
                ? new ManagedBrotli(compressed.ToArray(), directory.TotalLength, dictionary ?? throw new ArgumentNullException(nameof(dictionary))).Decompress()
                : Brotli.Decompress(compressed, directory.TotalLength);
        }
        catch (InvalidDataException e)
        {
            throw new FontFormatException("WOFF2 font data is damaged.", e);
        }

        // Section 5: tables follow each other in directory order, transformed ones at their transformed length.
        foreach (Entry entry in directory.Tables)
        {
            if (!entry.Transformed) entry.Data = block.AsSpan(entry.Offset, entry.Length).ToArray();
        }

        foreach ((_, int[] tables) in directory.Fonts) RebuildTransformed(directory, block, tables, maxSize);

        long total = 0;
        foreach (Entry entry in directory.Tables)
        {
            if (entry.Data is null) throw new FontFormatException($"WOFF2 table '{entry.Tag}' belongs to no font.");
            total += entry.Data.Length;
        }

        if (total > maxSize) throw new FontFormatException("WOFF2 font is too large.");

        if (directory.Flavor != Collection)
        {
            var tables = directory.Tables.Select(t => (t.Tag, 0u, t.Data!)).ToList();
            return SfntBuilder.Build(directory.Flavor, tables);
        }

        return SfntBuilder.BuildCollection(directory.Fonts, directory.Tables.Select(t => (t.Tag, t.Data!)).ToList());
    }

    /// <summary>Offset and length of the compressed block and its expanded length, for tests.</summary>
    internal static (int Offset, int Length, int Expanded) Layout(byte[] data)
    {
        Directory directory = ReadDirectory(data, int.MaxValue);
        return (directory.DataOffset, directory.CompressedLength, directory.TotalLength);
    }

    /// <summary>Sections 3 and 4: header, table directory and collection directory.</summary>
    private static Directory ReadDirectory(ReadOnlySpan<byte> d, int maxSize)
    {
        if (d.Length < 48 || Tag(d, 0) != "wOF2") throw new FontFormatException("WOFF2 header is truncated.");
        var directory = new Directory { Flavor = U32(d, 4) };
        int count = U16(d, 12);
        if (count == 0) throw new FontFormatException("WOFF2 font has no tables.");
        directory.CompressedLength = Offset32(d, 20);

        int position = 48;
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            int flags = U8(d, position++);
            int version = flags >> 6;
            string tag;
            if ((flags & 0x3F) == 0x3F)
            {
                tag = Tag(d, position);
                position += 4;
            }
            else
            {
                tag = KnownTags[flags & 0x3F];
            }

            // Section 4.1: glyf and loca use version 3 for the null transform, other tables version 0.
            bool glyfOrLoca = tag is "glyf" or "loca";
            bool transformed = glyfOrLoca ? version != 3 : version != 0;
            if (transformed && !(glyfOrLoca ? version == 0 : tag == "hmtx" && version == 1))
            {
                throw new FontFormatException($"WOFF2 table '{tag}' uses an unknown transform.");
            }

            var entry = new Entry { Tag = tag, Transformed = transformed, OriginalLength = ReadBase128(d, ref position) };
            entry.Length = transformed ? ReadBase128(d, ref position) : entry.OriginalLength;
            if (tag == "loca" && transformed && entry.Length != 0) throw new FontFormatException("WOFF2 loca table is invalid.");
            entry.Offset = (int)total;
            total += entry.Length;
            if (total > maxSize) throw new FontFormatException("WOFF2 font is too large.");
            directory.Tables.Add(entry);
        }

        directory.TotalLength = (int)total;
        if (directory.Flavor == Collection)
        {
            // Section 4.2: the collection directory follows the table directory.
            uint version = U32(d, position);
            position += 4;
            if (version is not (0x00010000 or 0x00020000)) throw new FontFormatException("WOFF2 collection version is not supported.");
            int fonts = Read255(d, ref position);
            if (fonts == 0) throw new FontFormatException("WOFF2 collection has no fonts.");
            for (int f = 0; f < fonts; f++)
            {
                int tables = Read255(d, ref position);
                uint flavor = U32(d, position);
                position += 4;
                var indices = new int[tables];
                for (int t = 0; t < tables; t++)
                {
                    indices[t] = Read255(d, ref position);
                    if (indices[t] >= count) throw new FontFormatException("WOFF2 collection refers to a missing table.");
                }

                directory.Fonts.Add((flavor, indices));
            }
        }
        else
        {
            directory.Fonts.Add((directory.Flavor, Enumerable.Range(0, count).ToArray()));
        }

        Check(d, position, directory.CompressedLength);
        directory.DataOffset = position;
        return directory;
    }

    /// <summary>Section 3.1: UIntBase128, at most 5 bytes, no leading zeros, no overflow.</summary>
    private static int ReadBase128(ReadOnlySpan<byte> d, ref int position)
    {
        uint value = 0;
        for (int i = 0; i < 5; i++)
        {
            int b = U8(d, position++);
            if (i == 0 && b == 0x80) throw new FontFormatException("WOFF2 number has a leading zero.");
            if ((value & 0xFE000000) != 0) throw new FontFormatException("WOFF2 number overflows.");
            value = value << 7 | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                if (value > int.MaxValue) throw new FontFormatException("WOFF2 table is too large.");
                return (int)value;
            }
        }

        throw new FontFormatException("WOFF2 number is too long.");
    }

    /// <summary>Section 3.1: 255UInt16.</summary>
    private static int Read255(ReadOnlySpan<byte> d, ref int position)
    {
        int code = U8(d, position++);
        switch (code)
        {
            case 253:
                int value = U16(d, position);
                position += 2;
                return value;
            case 254:
                return 506 + U8(d, position++);
            case 255:
                return 253 + U8(d, position++);
            default:
                return code;
        }
    }

    /// <summary>Rebuilds the transformed glyf, loca and hmtx tables of one font, once per table.</summary>
    private static void RebuildTransformed(Directory directory, byte[] block, int[] font, int maxSize)
    {
        int glyf = -1, loca = -1, hmtx = -1, head = -1, hhea = -1, maxp = -1;
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (int index in font)
        {
            string tag = directory.Tables[index].Tag;
            if (!tags.Add(tag)) throw new FontFormatException($"WOFF2 font has two '{tag}' tables.");
            switch (tag)
            {
                case "glyf": glyf = index; break;
                case "loca": loca = index; break;
                case "hmtx": hmtx = index; break;
                case "head": head = index; break;
                case "hhea": hhea = index; break;
                case "maxp": maxp = index; break;
            }
        }

        // Sections 4.2 and 5.5: glyf and loca come in pairs, transformed together, and in a
        // collection loca immediately follows its glyf.
        if ((glyf < 0) != (loca < 0)) throw new FontFormatException("WOFF2 font has glyf without loca.");
        if (glyf >= 0)
        {
            Entry g = directory.Tables[glyf], l = directory.Tables[loca];
            if (g.Transformed != l.Transformed) throw new FontFormatException("WOFF2 glyf and loca are not transformed together.");
            if (directory.Flavor == Collection && loca != glyf + 1) throw new FontFormatException("WOFF2 collection does not pair glyf and loca.");
            if (g.Transformed && g.Data is null)
            {
                if (head < 0 || maxp < 0) throw new FontFormatException("WOFF2 font has glyf without head or maxp.");
                (g.Data, l.Data) = RebuildGlyf(block.AsSpan(g.Offset, g.Length), directory.Tables[head].Data!, directory.Tables[maxp].Data!, l.OriginalLength, maxSize);
            }
        }

        Entry? h = hmtx >= 0 ? directory.Tables[hmtx] : null;
        if (h is { Transformed: true, Data: null })
        {
            if (glyf < 0 || head < 0 || hhea < 0 || maxp < 0) throw new FontFormatException("WOFF2 hmtx transform needs glyf, head, hhea and maxp.");
            h.Data = RebuildHmtx(block.AsSpan(h.Offset, h.Length), directory.Tables[glyf].Data!, directory.Tables[loca].Data!, directory.Tables[head].Data!, directory.Tables[hhea].Data!, directory.Tables[maxp].Data!);
        }
    }

    /// <summary>Reads the substreams of a transformed glyf table, with bounds on every read.</summary>
    private sealed class Cursor(byte[] data, int start, int length, string name)
    {
        private int _position = start;
        private readonly int _end = start + length;

        public int U8()
        {
            if (_position >= _end) throw Truncated();
            return data[_position++];
        }

        public int U16() => U8() << 8 | U8();

        public short I16() => (short)U16();

        /// <summary>Section 3.1: 255UInt16.</summary>
        public int Read255()
        {
            int code = U8();
            return code switch
            {
                253 => U16(),
                254 => 506 + U8(),
                255 => 253 + U8(),
                _ => code,
            };
        }

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count > _end - _position) throw Truncated();
            _position += count;
            return data.AsSpan(_position - count, count);
        }

        private FontFormatException Truncated() => new($"WOFF2 glyf {name} stream is truncated.");
    }

    /// <summary>Sections 5.1 to 5.3: rebuilds glyf and loca from a transformed glyf table.</summary>
    private static (byte[] Glyf, byte[] Loca) RebuildGlyf(ReadOnlySpan<byte> table, byte[] head, byte[] maxp, int locaLength, int maxSize)
    {
        byte[] t = table.ToArray();
        int options = U16(t, 2);
        int glyphs = U16(t, 4);
        int indexFormat = U16(t, 6);
        if (indexFormat > 1 || head.Length < 54 || I16(head, 50) != indexFormat) throw new FontFormatException("WOFF2 glyf index format does not match the head table.");
        if (maxp.Length < 6 || U16(maxp, 4) != glyphs) throw new FontFormatException("WOFF2 glyf glyph count does not match the maxp table.");
        if (locaLength != (glyphs + 1) * (indexFormat == 0 ? 2 : 4)) throw new FontFormatException("WOFF2 loca length is invalid.");

        int position = 36;
        var streams = new Cursor[7];
        string[] names = ["nContour", "nPoints", "flag", "glyph", "composite", "bbox", "instruction"];
        for (int i = 0; i < 7; i++)
        {
            int size = Offset32(t, 8 + 4 * i);
            Check(t, position, size);
            streams[i] = new Cursor(t, position, size, names[i]);
            position += size;
        }

        Cursor contours = streams[0], points = streams[1], flagStream = streams[2], glyphStream = streams[3];
        Cursor composites = streams[4], boxes = streams[5], instructions = streams[6];
        ReadOnlySpan<byte> boxBitmap = boxes.Bytes(4 * ((glyphs + 31) / 32));
        ReadOnlySpan<byte> overlapBitmap = (options & 1) != 0 ? Slice(t, position, (glyphs + 7) / 8) : default;

        var glyf = new FontWriter(t.Length);
        var offsets = new int[glyphs + 1];
        for (int g = 0; g < glyphs; g++)
        {
            offsets[g] = glyf.Length;
            int contourCount = contours.I16();
            bool hasBox = (boxBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;
            if (contourCount == 0)
            {
                if (hasBox) throw new FontFormatException("WOFF2 empty glyph has a bounding box.");
                continue;
            }

            if (contourCount > 0)
            {
                bool overlap = !overlapBitmap.IsEmpty && (overlapBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;
                WriteSimpleGlyph(glyf, contourCount, hasBox, overlap, points, flagStream, glyphStream, boxes, instructions);
            }
            else if (contourCount == -1)
            {
                if (!hasBox) throw new FontFormatException("WOFF2 composite glyph has no bounding box.");
                WriteCompositeGlyph(glyf, composites, glyphStream, boxes, instructions);
            }
            else
            {
                throw new FontFormatException("WOFF2 glyph contour count is invalid.");
            }

            glyf.Pad(indexFormat == 0 ? 2 : 4);
            if (glyf.Length > maxSize) throw new FontFormatException("WOFF2 font is too large.");
        }

        offsets[glyphs] = glyf.Length;
        if (indexFormat == 0 && glyf.Length > 0x1FFFE) throw new FontFormatException("WOFF2 glyf table is too large for short loca offsets.");
        var loca = new FontWriter(locaLength);
        foreach (int offset in offsets)
        {
            if (indexFormat == 0) loca.U16(offset / 2);
            else loca.U32((uint)offset);
        }

        return (glyf.ToArray(), loca.ToArray());
    }

    /// <summary>Sections 5.1 and 5.2: one simple glyph in standard TrueType form.</summary>
    private static void WriteSimpleGlyph(FontWriter w, int contourCount, bool hasBox, bool overlap, Cursor points, Cursor flagStream, Cursor glyphStream, Cursor boxes, Cursor instructions)
    {
        var endPoints = new int[contourCount];
        int total = 0;
        for (int c = 0; c < contourCount; c++)
        {
            int n = points.Read255();
            total += n;
            if (n == 0 || total > 0xFFFF) throw new FontFormatException("WOFF2 glyph point count is invalid.");
            endPoints[c] = total - 1;
        }

        var xs = new short[total];
        var ys = new short[total];
        var flags = new byte[total];
        int x = 0, y = 0;
        for (int p = 0; p < total; p++)
        {
            int flag = flagStream.U8();
            (int dx, int dy) = Triplet(flag & 0x7F, glyphStream);
            x = (short)(x + dx);
            y = (short)(y + dy);
            xs[p] = (short)x;
            ys[p] = (short)y;

            // OFF 5.3.3 flags: on curve, then x and y as a byte (sign in the "same" bit), a repeat
            // of the previous coordinate, or a 16 bit delta.
            int px = p == 0 ? 0 : xs[p - 1], py = p == 0 ? 0 : ys[p - 1];
            int fx = Delta(xs[p] - px, 0x02, 0x10), fy = Delta(ys[p] - py, 0x04, 0x20);
            flags[p] = (byte)(((flag & 0x80) == 0 ? 0x01 : 0) | fx | fy);
        }

        if (overlap) flags[0] |= 0x40;
        int instructionLength = glyphStream.Read255();
        ReadOnlySpan<byte> code = instructions.Bytes(instructionLength);

        w.U16(contourCount);
        if (hasBox)
        {
            for (int i = 0; i < 4; i++) w.U16(boxes.U16());
        }
        else
        {
            w.U16(xs.Min());
            w.U16(ys.Min());
            w.U16(xs.Max());
            w.U16(ys.Max());
        }

        foreach (int end in endPoints) w.U16(end);
        w.U16(instructionLength);
        w.Bytes(code);
        for (int p = 0; p < total;)
        {
            int run = 1;
            while (p + run < total && run < 256 && flags[p + run] == flags[p]) run++;
            if (run > 1)
            {
                w.U8(flags[p] | 0x08);
                w.U8(run - 1);
            }
            else
            {
                w.U8(flags[p]);
            }

            p += run;
        }

        for (int p = 0; p < total; p++) WriteCoordinate(w, (short)(xs[p] - (p == 0 ? 0 : xs[p - 1])), flags[p], 0x02, 0x10);
        for (int p = 0; p < total; p++) WriteCoordinate(w, (short)(ys[p] - (p == 0 ? 0 : ys[p - 1])), flags[p], 0x04, 0x20);
    }

    /// <summary>Flag bits for a coordinate delta, wrapped to 16 bits as glyf stores it.</summary>
    private static int Delta(int delta, int shortFlag, int sameFlag)
    {
        delta = (short)delta;
        if (delta == 0) return sameFlag;
        if (delta is >= -255 and <= 255) return shortFlag | (delta > 0 ? sameFlag : 0);
        return 0;
    }

    private static void WriteCoordinate(FontWriter w, short delta, int flag, int shortFlag, int sameFlag)
    {
        if ((flag & shortFlag) != 0) w.U8(Math.Abs((int)delta));
        else if ((flag & sameFlag) == 0) w.U16(delta);
    }

    /// <summary>Section 5.2: the coordinate deltas of one point from its triplet encoding.</summary>
    private static (int Dx, int Dy) Triplet(int index, Cursor s)
    {
        static int Sign(int flag, int value) => (flag & 1) != 0 ? value : -value;

        if (index < 10) return (0, Sign(index, ((index & 14) << 7) + s.U8()));
        if (index < 20) return (Sign(index, (((index - 10) & 14) << 7) + s.U8()), 0);
        if (index < 84)
        {
            int b0 = index - 20, b1 = s.U8();
            return (Sign(index, 1 + (b0 & 0x30) + (b1 >> 4)), Sign(index >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F)));
        }

        if (index < 120)
        {
            int b0 = index - 84, b1 = s.U8(), b2 = s.U8();
            return (Sign(index, 1 + ((b0 / 12) << 8) + b1), Sign(index >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2));
        }

        if (index < 124)
        {
            int b1 = s.U8(), b2 = s.U8(), b3 = s.U8();
            return (Sign(index, (b1 << 4) + (b2 >> 4)), Sign(index >> 1, ((b2 & 0x0F) << 8) + b3));
        }

        int c1 = s.U8(), c2 = s.U8(), c3 = s.U8(), c4 = s.U8();
        return (Sign(index, (c1 << 8) + c2), Sign(index >> 1, (c3 << 8) + c4));
    }

    /// <summary>Section 5.1: a composite glyph, its components copied from the composite stream.</summary>
    private static void WriteCompositeGlyph(FontWriter w, Cursor composites, Cursor glyphStream, Cursor boxes, Cursor instructions)
    {
        w.U16(-1);
        for (int i = 0; i < 4; i++) w.U16(boxes.U16());
        bool hasInstructions = false;
        int flags;
        do
        {
            flags = composites.U16();
            hasInstructions |= (flags & 0x0100) != 0;

            // Glyph index, then byte or word arguments, then a scale, x and y scales or a 2x2 matrix.
            int size = 2 + ((flags & 0x0001) != 0 ? 4 : 2);
            if ((flags & 0x0008) != 0) size += 2;
            else if ((flags & 0x0040) != 0) size += 4;
            else if ((flags & 0x0080) != 0) size += 8;
            w.U16(flags);
            w.Bytes(composites.Bytes(size));
        }
        while ((flags & 0x0020) != 0);

        if (!hasInstructions) return;
        int length = glyphStream.Read255();
        w.U16(length);
        w.Bytes(instructions.Bytes(length));
    }

    /// <summary>Section 5.4: rebuilds hmtx, taking omitted left side bearings from the glyph xMin values.</summary>
    private static byte[] RebuildHmtx(ReadOnlySpan<byte> t, byte[] glyf, byte[] loca, byte[] head, byte[] hhea, byte[] maxp)
    {
        int flags = U8(t, 0);
        if ((flags & 0xFC) != 0 || (flags & 3) == 0) throw new FontFormatException("WOFF2 hmtx transform flags are invalid.");
        int glyphs = U16(maxp, 4), metrics = U16(hhea, 34);
        if (metrics == 0 || metrics > glyphs) throw new FontFormatException("WOFF2 hhea table is invalid.");
        bool longLoca = I16(head, 50) != 0;
        if (loca.Length < (glyphs + 1) * (longLoca ? 4 : 2)) throw new FontFormatException("WOFF2 loca table is truncated.");

        bool proportionalOmitted = (flags & 1) != 0, monospacedOmitted = (flags & 2) != 0;
        int expected = 1 + 2 * metrics + (proportionalOmitted ? 0 : 2 * metrics) + (monospacedOmitted ? 0 : 2 * (glyphs - metrics));
        if (t.Length != expected) throw new FontFormatException("WOFF2 hmtx table has the wrong length.");

        int bearings = 1 + 2 * metrics, position = bearings;
        var w = new FontWriter(4 * metrics + 2 * (glyphs - metrics));
        for (int g = 0; g < glyphs; g++)
        {
            bool omitted = g < metrics ? proportionalOmitted : monospacedOmitted;
            int lsb;
            if (omitted)
            {
                lsb = XMin(glyf, loca, longLoca, g);
            }
            else
            {
                lsb = I16(t, position);
                position += 2;
            }

            if (g < metrics) w.U16(U16(t, 1 + 2 * g));
            w.U16(lsb);
        }

        return w.ToArray();
    }

    /// <summary>A glyph's xMin, or zero for an empty glyph.</summary>
    private static int XMin(byte[] glyf, byte[] loca, bool longLoca, int glyph)
    {
        int start = longLoca ? Offset32(loca, 4 * glyph) : 2 * U16(loca, 2 * glyph);
        int end = longLoca ? Offset32(loca, 4 * glyph + 4) : 2 * U16(loca, 2 * glyph + 2);
        if (start > end || end > glyf.Length) throw new FontFormatException("WOFF2 loca table is invalid.");
        return end - start >= 10 ? I16(glyf, start + 2) : 0;
    }
}
