using static PdfNetStack.Fonts.OpenType.FontData;

namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// The CFF table of an OpenType font (Adobe Technical Note 5176), read far enough to map glyphs
/// to CIDs and to write a subset for a FontFile3 /CIDFontType0C stream. The subset keeps glyph ids
/// and replaces unused charstrings with a bare endchar; subroutines are kept whole.
/// </summary>
internal sealed class CffFont
{
    private const int OpCharset = 15, OpEncoding = 16, OpCharStrings = 17, OpPrivate = 18, OpSubrs = 19;
    private const int OpRos = 1230, OpFdArray = 1236, OpFdSelect = 1237;

    private readonly byte[] _cff;
    private readonly Index _names;
    private readonly List<DictEntry> _top;
    private readonly Index _strings;
    private readonly Index _globalSubrs;
    private readonly Index _charStrings;
    private readonly int _glyphCount;

    public CffFont(ReadOnlySpan<byte> cff)
    {
        _cff = cff.ToArray();
        ReadOnlySpan<byte> d = _cff;
        if (U8(d, 0) != 1) throw new FontFormatException("Only CFF version 1 is supported.");
        int at = U8(d, 2);
        _names = Index.Read(d, ref at);
        Index tops = Index.Read(d, ref at);
        _strings = Index.Read(d, ref at);
        _globalSubrs = Index.Read(d, ref at);
        if (tops.Count != 1) throw new FontFormatException("CFF font sets with several fonts are not supported.");
        _top = Dict.Read(tops.Item(d, 0));

        int charStrings = Dict.Int(_top, OpCharStrings) ?? throw new FontFormatException("CFF font has no CharStrings.");
        _charStrings = Index.Read(d, ref charStrings);
        _glyphCount = _charStrings.Count;
        IsCidKeyed = Dict.Find(_top, OpRos) is not null;
        GlyphToCid = ReadCharset();
    }

    /// <summary>Number of charstrings.</summary>
    public int GlyphCount => _glyphCount;

    /// <summary>True for a CID-keyed font (ROS operator), whose glyphs are addressed by CID.</summary>
    public bool IsCidKeyed { get; }

    /// <summary>CID of each glyph: the charset for a CID-keyed font, the glyph id itself otherwise.</summary>
    public int[] GlyphToCid { get; }

    /// <summary>Writes the subset: outlines for <paramref name="keep"/> only, same glyph ids.</summary>
    public byte[] Subset(ISet<int> keep)
    {
        ReadOnlySpan<byte> d = _cff;
        var charStrings = new List<byte[]>(_glyphCount);
        for (int g = 0; g < _glyphCount; g++) charStrings.Add(g == 0 || keep.Contains(g) ? _charStrings.Item(d, g).ToArray() : [14]);

        byte[]? charset = RawCharset();
        byte[]? fdSelect = IsCidKeyed ? RawFdSelect() : null;

        // Each font dictionary with its Private DICT and local subroutines.
        var fonts = new List<(List<DictEntry> Dict, byte[] Private, byte[]? Subrs)>();
        if (IsCidKeyed)
        {
            int fdArray = Dict.Int(_top, OpFdArray) ?? throw new FontFormatException("CID-keyed CFF has no FDArray.");
            Index fds = Index.Read(d, ref fdArray);
            for (int i = 0; i < fds.Count; i++)
            {
                List<DictEntry> fd = Dict.Read(fds.Item(d, i));
                (byte[] priv, byte[]? subrs) = ReadPrivate(fd);
                fonts.Add((fd, priv, subrs));
            }
        }
        else
        {
            (byte[] priv, byte[]? subrs) = ReadPrivate(_top);
            fonts.Add((_top, priv, subrs));
        }

        // Offsets are written as 5 byte integers, so sizes do not depend on the offsets themselves.
        byte[] names = _names.Raw(d).ToArray(), strings = _strings.Raw(d).ToArray(), globals = _globalSubrs.Raw(d).ToArray();
        byte[] charStringIndex = Index.Write(charStrings);
        int topSize = Index.Write([BuildTop(0, 0, 0, 0, 0, 0)]).Length;
        int position = 4 + names.Length + topSize + strings.Length + globals.Length;
        int charsetOffset = charset is null ? 0 : position;
        position += charset?.Length ?? 0;
        int fdSelectOffset = fdSelect is null ? 0 : position;
        position += fdSelect?.Length ?? 0;
        int charStringsOffset = position;
        position += charStringIndex.Length;

        int fdArrayOffset = 0;
        byte[]? fdArrayIndex = null;
        var privates = new List<byte[]>();
        if (IsCidKeyed)
        {
            fdArrayOffset = position;
            int fdArraySize = Index.Write(fonts.Select(f => Dict.Write(WithPrivate(f.Dict, f.Private.Length, 0))).ToList()).Length;
            position += fdArraySize;
            var fdDicts = new List<byte[]>();
            foreach ((List<DictEntry> fd, byte[] priv, byte[]? subrs) in fonts)
            {
                fdDicts.Add(Dict.Write(WithPrivate(fd, priv.Length, position)));
                privates.Add(priv);
                position += priv.Length;
                if (subrs is not null)
                {
                    privates.Add(subrs);
                    position += subrs.Length;
                }
            }

            fdArrayIndex = Index.Write(fdDicts);
        }

        int privateOffset = position;
        if (!IsCidKeyed)
        {
            privates.Add(fonts[0].Private);
            if (fonts[0].Subrs is not null) privates.Add(fonts[0].Subrs!);
        }

        var w = new FontWriter(position + 64);
        w.U8(1);
        w.U8(0);
        w.U8(4);
        w.U8(4);
        w.Bytes(names);
        w.Bytes(Index.Write([BuildTop(charsetOffset, charStringsOffset, fonts[0].Private.Length, privateOffset, fdArrayOffset, fdSelectOffset)]));
        w.Bytes(strings);
        w.Bytes(globals);
        if (charset is not null) w.Bytes(charset);
        if (fdSelect is not null) w.Bytes(fdSelect);
        w.Bytes(charStringIndex);
        if (fdArrayIndex is not null) w.Bytes(fdArrayIndex);
        foreach (byte[] p in privates) w.Bytes(p);
        return w.ToArray();

        byte[] BuildTop(int charsetAt, int charStringsAt, int privateSize, int privateAt, int fdArrayAt, int fdSelectAt)
        {
            var top = new List<DictEntry>();
            foreach (DictEntry e in _top)
            {
                switch (e.Op)
                {
                    case OpEncoding: // ignored for CIDFontType0C; the default applies
                    case OpCharset when charset is not null:
                    case OpCharStrings or OpPrivate or OpFdArray or OpFdSelect:
                        continue;
                    default:
                        top.Add(e);
                        break;
                }
            }

            if (charset is not null) top.Add(DictEntry.Offsets(OpCharset, charsetAt));
            top.Add(DictEntry.Offsets(OpCharStrings, charStringsAt));
            if (IsCidKeyed)
            {
                top.Add(DictEntry.Offsets(OpFdArray, fdArrayAt));
                top.Add(DictEntry.Offsets(OpFdSelect, fdSelectAt));
            }
            else
            {
                top.Add(DictEntry.Offsets(OpPrivate, privateSize, privateAt));
            }

            // ROS must come first in a CID-keyed Top DICT.
            int ros = top.FindIndex(e => e.Op == OpRos);
            if (ros > 0)
            {
                DictEntry r = top[ros];
                top.RemoveAt(ros);
                top.Insert(0, r);
            }

            return Dict.Write(top);
        }
    }

    private static List<DictEntry> WithPrivate(List<DictEntry> dict, int size, int offset)
    {
        var copy = dict.Where(e => e.Op != OpPrivate).ToList();
        copy.Add(DictEntry.Offsets(OpPrivate, size, offset));
        return copy;
    }

    /// <summary>A Private DICT, rewritten so its local subroutines follow it directly, and those subroutines.</summary>
    private (byte[] Private, byte[]? Subrs) ReadPrivate(List<DictEntry> owner)
    {
        DictEntry? entry = Dict.Find(owner, OpPrivate);
        if (entry is null) return (Dict.Write([]), null);
        int[] operands = entry.Value.Ints();
        if (operands.Length != 2) throw new FontFormatException("CFF Private operator is invalid.");
        int size = operands[0], offset = operands[1];
        ReadOnlySpan<byte> d = _cff;
        List<DictEntry> priv = Dict.Read(Slice(d, offset, size));
        int? subrs = Dict.Int(priv, OpSubrs);
        if (subrs is null) return (Dict.Write(priv), null);

        int subrsAt = offset + subrs.Value;
        byte[] subrIndex = Index.Read(d, ref subrsAt).Raw(d).ToArray();
        var rewritten = priv.Where(e => e.Op != OpSubrs).ToList();
        int length = Dict.Write(rewritten).Length + DictEntry.Offsets(OpSubrs, 0).Length;
        rewritten.Add(DictEntry.Offsets(OpSubrs, length));
        return (Dict.Write(rewritten), subrIndex);
    }

    private int[] ReadCharset()
    {
        var cids = new int[_glyphCount];
        for (int g = 0; g < cids.Length; g++) cids[g] = g;
        if (!IsCidKeyed) return cids;

        int at = Dict.Int(_top, OpCharset) ?? 0;
        if (at <= 2) return cids;
        ReadOnlySpan<byte> d = _cff;
        int format = U8(d, at++);
        int glyph = 1;
        if (format == 0)
        {
            for (; glyph < _glyphCount; glyph++, at += 2) cids[glyph] = U16(d, at);
            return cids;
        }

        if (format is not (1 or 2)) throw new FontFormatException("CFF charset format is invalid.");
        while (glyph < _glyphCount)
        {
            int first = U16(d, at);
            int left = format == 1 ? U8(d, at + 2) : U16(d, at + 2);
            at += format == 1 ? 3 : 4;
            for (int i = 0; i <= left && glyph < _glyphCount; i++) cids[glyph++] = first + i;
        }

        return cids;
    }

    /// <summary>The charset bytes, or null for a predefined charset (offset 0 to 2).</summary>
    private byte[]? RawCharset()
    {
        int start = Dict.Int(_top, OpCharset) ?? 0;
        if (start <= 2) return null;
        ReadOnlySpan<byte> d = _cff;
        int at = start;
        int format = U8(d, at++);
        if (format == 0)
        {
            at += 2 * (_glyphCount - 1);
        }
        else
        {
            for (int covered = 1; covered < _glyphCount;)
            {
                int left = format == 1 ? U8(d, at + 2) : U16(d, at + 2);
                at += format == 1 ? 3 : 4;
                covered += left + 1;
            }
        }

        return Slice(d, start, at - start).ToArray();
    }

    private byte[] RawFdSelect()
    {
        int start = Dict.Int(_top, OpFdSelect) ?? throw new FontFormatException("CID-keyed CFF has no FDSelect.");
        ReadOnlySpan<byte> d = _cff;
        int format = U8(d, start);
        int length = format switch
        {
            0 => 1 + _glyphCount,
            3 => 1 + 2 + 3 * U16(d, start + 1) + 2,
            _ => throw new FontFormatException("CFF FDSelect format is invalid."),
        };
        return Slice(d, start, length).ToArray();
    }

    /// <summary>A CFF INDEX: a count, offsets and the data they point into.</summary>
    private readonly struct Index(int start, int count, int offSize, int dataStart, int end)
    {
        public int Count => count;

        public static Index Read(ReadOnlySpan<byte> d, ref int at)
        {
            int start = at;
            int count = U16(d, at);
            if (count == 0)
            {
                at += 2;
                return new Index(start, 0, 0, at, at);
            }

            int offSize = U8(d, at + 2);
            if (offSize is < 1 or > 4) throw new FontFormatException("CFF INDEX offset size is invalid.");
            int offsets = at + 3;
            int dataStart = offsets + (count + 1) * offSize - 1;
            int last = Offset(d, offsets, offSize, count);
            int end = dataStart + last;
            Check(d, dataStart, last);
            at = end;
            return new Index(start, count, offSize, dataStart, end);
        }

        public ReadOnlySpan<byte> Item(ReadOnlySpan<byte> d, int i)
        {
            int offsets = start + 3;
            int a = Offset(d, offsets, offSize, i), b = Offset(d, offsets, offSize, i + 1);
            if (a < 1 || b < a || dataStart + b > end) throw new FontFormatException("CFF INDEX is invalid.");
            return d.Slice(dataStart + a, b - a);
        }

        public ReadOnlySpan<byte> Raw(ReadOnlySpan<byte> d) => d.Slice(start, end - start);

        public static byte[] Write(List<byte[]> items)
        {
            if (items.Count == 0) return [0, 0];
            int total = items.Sum(i => i.Length) + 1;
            int offSize = total <= 0xFF ? 1 : total <= 0xFFFF ? 2 : total <= 0xFFFFFF ? 3 : 4;
            var w = new FontWriter(3 + (items.Count + 1) * offSize + total);
            w.U16(items.Count);
            w.U8(offSize);
            int offset = 1;
            for (int i = 0; i <= items.Count; i++)
            {
                for (int k = offSize - 1; k >= 0; k--) w.U8(offset >> (8 * k));
                if (i < items.Count) offset += items[i].Length;
            }

            foreach (byte[] item in items) w.Bytes(item);
            return w.ToArray();
        }

        private static int Offset(ReadOnlySpan<byte> d, int offsets, int offSize, int i)
        {
            int v = 0;
            for (int k = 0; k < offSize; k++) v = v << 8 | U8(d, offsets + i * offSize + k);
            if (v < 0) throw new FontFormatException("CFF INDEX offset is out of range.");
            return v;
        }
    }

    /// <summary>One DICT operator with its operands, kept as their original bytes.</summary>
    private readonly record struct DictEntry(int Op, byte[] Operands)
    {
        public int Length => Operands.Length + (Op >= 1200 ? 2 : 1);

        /// <summary>An operator whose operands are offsets, written as 5 byte integers.</summary>
        public static DictEntry Offsets(int op, params int[] values)
        {
            var bytes = new byte[5 * values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                bytes[5 * i] = 29;
                bytes[5 * i + 1] = (byte)(values[i] >> 24);
                bytes[5 * i + 2] = (byte)(values[i] >> 16);
                bytes[5 * i + 3] = (byte)(values[i] >> 8);
                bytes[5 * i + 4] = (byte)values[i];
            }

            return new DictEntry(op, bytes);
        }

        /// <summary>The operands as integers; reals are not expected for the operators this is used on.</summary>
        public int[] Ints()
        {
            var values = new List<int>();
            ReadOnlySpan<byte> o = Operands;
            for (int i = 0; i < o.Length;)
            {
                int b0 = o[i];
                switch (b0)
                {
                    case >= 32 and <= 246:
                        values.Add(b0 - 139);
                        i++;
                        break;
                    case >= 247 and <= 250:
                        values.Add((b0 - 247) * 256 + U8(o, i + 1) + 108);
                        i += 2;
                        break;
                    case >= 251 and <= 254:
                        values.Add(-(b0 - 251) * 256 - U8(o, i + 1) - 108);
                        i += 2;
                        break;
                    case 28:
                        values.Add(I16(o, i + 1));
                        i += 3;
                        break;
                    case 29:
                        values.Add((int)U32(o, i + 1));
                        i += 5;
                        break;
                    default:
                        throw new FontFormatException("CFF offset operand is not an integer.");
                }
            }

            return [.. values];
        }
    }

    private static class Dict
    {
        public static List<DictEntry> Read(ReadOnlySpan<byte> d)
        {
            var entries = new List<DictEntry>();
            int operandStart = 0;
            for (int i = 0; i < d.Length;)
            {
                int b0 = d[i];
                if (b0 <= 21)
                {
                    int op = b0;
                    int length = 1;
                    if (b0 == 12)
                    {
                        op = 1200 + U8(d, i + 1);
                        length = 2;
                    }

                    entries.Add(new DictEntry(op, d.Slice(operandStart, i - operandStart).ToArray()));
                    i += length;
                    operandStart = i;
                    continue;
                }

                i += b0 switch
                {
                    28 => 3,
                    29 => 5,
                    30 => RealLength(d, i),
                    >= 32 and <= 246 => 1,
                    >= 247 and <= 254 => 2,
                    _ => throw new FontFormatException("CFF DICT is invalid."),
                };
                if (i > d.Length) throw new FontFormatException("CFF DICT is truncated.");
            }

            return entries;
        }

        public static byte[] Write(List<DictEntry> entries)
        {
            var w = new FontWriter(entries.Sum(e => e.Length));
            foreach (DictEntry e in entries)
            {
                w.Bytes(e.Operands);
                if (e.Op >= 1200)
                {
                    w.U8(12);
                    w.U8(e.Op - 1200);
                }
                else
                {
                    w.U8(e.Op);
                }
            }

            return w.ToArray();
        }

        public static DictEntry? Find(List<DictEntry> entries, int op)
        {
            foreach (DictEntry e in entries)
            {
                if (e.Op == op) return e;
            }

            return null;
        }

        /// <summary>The last integer operand of an operator, or null when the operator is absent.</summary>
        public static int? Int(List<DictEntry> entries, int op)
        {
            DictEntry? e = Find(entries, op);
            if (e is null) return null;
            int[] values = e.Value.Ints();
            return values.Length == 0 ? throw new FontFormatException("CFF operator has no operand.") : values[^1];
        }

        private static int RealLength(ReadOnlySpan<byte> d, int i)
        {
            for (int k = i + 1; k < d.Length; k++)
            {
                if ((d[k] & 0x0F) == 0x0F || (d[k] & 0xF0) == 0xF0) return k - i + 1;
            }

            throw new FontFormatException("CFF real number is truncated.");
        }
    }
}
